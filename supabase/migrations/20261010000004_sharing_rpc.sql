-- =====================================================================================================
-- TravelPlanner - G2 / 4: sharing a trip and managing the account. All of these need a connection
-- (the app greys them out offline).
--   create_invite(trip, role, valid_hours, max_uses) -> code      owner
--   revoke_invite(code)                                           owner
--   accept_invite(code) -> trip_id, role, trip_name               anyone signed in
--   get_trip_members(trip) -> user_id, role, display_name, ...    any member
--   set_member_role(trip, user, role)                             owner (editor <-> viewer)
--   remove_member(trip, user)                                     owner
--   leave_trip(trip)                                              editor / viewer
--   delete_account()                                              the user themself
-- =====================================================================================================

create or replace function private.require_uid() returns uuid
language plpgsql
stable
set search_path = ''
as $$
declare v_uid uuid := (select auth.uid());
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    return v_uid;
end;
$$;

create or replace function private.require_owner(p_trip_id uuid) returns uuid
language plpgsql
stable
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    if not exists (select 1 from public.trip_members m
                   where m.trip_id = p_trip_id and m.user_id = v_uid and m.role = 'owner') then
        raise exception 'only the trip owner can do this' using errcode = '42501';
    end if;
    return v_uid;
end;
$$;

-- 8 characters from an alphabet without look-alikes (0/O, 1/I/L): 31^8 ≈ 8.5 * 10^11 codes.
create or replace function private.new_invite_code() returns text
language plpgsql
volatile
set search_path = ''
as $$
declare
    v_alphabet constant text := 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';  -- 31 letters/digits
    v_bytes bytea := uuid_send(gen_random_uuid()) || uuid_send(gen_random_uuid());
    v_code  text := '';
    i int;
begin
    for i in 0..7 loop
        v_code := v_code || substr(v_alphabet, (get_byte(v_bytes, i) % length(v_alphabet)) + 1, 1);
    end loop;
    return v_code;
end;
$$;

create or replace function public.create_invite(
    p_trip_id uuid, p_role text default 'editor', p_valid_hours int default 168, p_max_uses int default null)
returns table (code text, expires_at timestamptz)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid  uuid := private.require_owner(p_trip_id);
    v_code text;
begin
    if p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    if p_valid_hours is null or p_valid_hours < 1 or p_valid_hours > 24 * 30 then
        raise exception 'valid_hours must be between 1 and 720' using errcode = '22023';
    end if;
    if exists (select 1 from public.trips t where t.id = p_trip_id and t.deleted_at is not null) then
        raise exception 'trip is deleted' using errcode = '22023';
    end if;

    loop
        v_code := private.new_invite_code();
        exit when not exists (select 1 from public.trip_invites i where i.code = v_code);
    end loop;

    return query
    insert into public.trip_invites (trip_id, code, role, created_by, expires_at, max_uses)
    values (p_trip_id, v_code, p_role, v_uid, now() + make_interval(hours => p_valid_hours), p_max_uses)
    returning trip_invites.code, trip_invites.expires_at;
end;
$$;

create or replace function public.revoke_invite(p_code text) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_trip uuid;
begin
    select i.trip_id into v_trip from public.trip_invites i where i.code = upper(trim(p_code));
    if v_trip is null then
        return;
    end if;
    perform private.require_owner(v_trip);
    update public.trip_invites set revoked_at = now() where code = upper(trim(p_code)) and revoked_at is null;
end;
$$;

create or replace function public.accept_invite(p_code text)
returns table (trip_id uuid, role text, trip_name text)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid    uuid := private.require_uid();
    v_invite public.trip_invites;
    v_role   text;
begin
    select * into v_invite from public.trip_invites i where i.code = upper(trim(p_code)) for update;

    if v_invite.id is null or v_invite.revoked_at is not null or v_invite.expires_at < now()
       or (v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses)
       or exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        raise exception 'invite is invalid or expired' using errcode = 'P0002';
    end if;

    -- Already a member: keep the current role (an invite never demotes the owner or changes a role).
    select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
    if v_role is null then
        insert into public.trip_members (trip_id, user_id, role, invited_by)
        values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
        update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;
        v_role := v_invite.role;
    end if;

    return query select v_invite.trip_id, v_role, t.name from public.trips t where t.id = v_invite.trip_id;
end;
$$;

create or replace function public.get_trip_members(p_trip_id uuid)
returns table (user_id uuid, role text, display_name text, email text, avatar_url text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
begin
    perform private.require_uid();
    if not private.has_trip_role(p_trip_id, array['owner', 'editor', 'viewer']) then
        raise exception 'not a member of this trip' using errcode = '42501';
    end if;

    return query
    select m.user_id, m.role, p.display_name, p.email, p.avatar_url, m.joined_at
    from public.trip_members m
    left join public.profiles p on p.id = m.user_id
    where m.trip_id = p_trip_id
    order by case m.role when 'owner' then 0 when 'editor' then 1 else 2 end, m.joined_at;
end;
$$;

create or replace function public.set_member_role(p_trip_id uuid, p_user_id uuid, p_role text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.require_owner(p_trip_id);
    if p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    update public.trip_members
    set role = p_role
    where trip_id = p_trip_id and user_id = p_user_id and role <> 'owner';
    if not found then
        raise exception 'no such member (or it is the owner)' using errcode = 'P0002';
    end if;
end;
$$;

create or replace function public.remove_member(p_trip_id uuid, p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.require_owner(p_trip_id);
    delete from public.trip_members
    where trip_id = p_trip_id and user_id = p_user_id and role <> 'owner';
    if not found then
        raise exception 'no such member (or it is the owner)' using errcode = 'P0002';
    end if;
end;
$$;

create or replace function public.leave_trip(p_trip_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    delete from public.trip_members
    where trip_id = p_trip_id and user_id = v_uid and role <> 'owner';
    if not found then
        raise exception 'not a member, or the owner (the owner deletes the trip instead)' using errcode = 'P0002';
    end if;
end;
$$;

-- Deletes the account and everything it owns (owned trips with all their content, memberships, packing
-- templates, profile). The app deletes the account's files from Storage first (G7).
create or replace function public.delete_account() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    delete from public.trips where owner_id = v_uid;   -- cascades to content, members, invites
    delete from auth.users where id = v_uid;           -- cascades to profile, memberships, templates
end;
$$;

do $$
declare f text;
begin
    foreach f in array array[
        'public.create_invite(uuid, text, int, int)',
        'public.revoke_invite(text)',
        'public.accept_invite(text)',
        'public.get_trip_members(uuid)',
        'public.set_member_role(uuid, uuid, text)',
        'public.remove_member(uuid, uuid)',
        'public.leave_trip(uuid)',
        'public.delete_account()'] loop
        execute format('revoke all on function %s from public, anon', f);
        execute format('grant execute on function %s to authenticated', f);
    end loop;
end $$;

revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;
