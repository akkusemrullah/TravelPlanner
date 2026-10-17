-- =====================================================================================================
-- TravelPlanner - TP-4 / 14: invite codes can no longer be passed around, and members' e-mails stay private.
--
-- 1. create_invite: an invite is single-use and valid for 48 hours by default. A code for a group has to be
--    asked for on purpose (p_max_uses up to 10, still 48 hours). Whoever joins becomes a VIEWER; the owner
--    makes them an editor afterwards (set_member_role). Older app versions still call
--    create_invite(trip, 'editor', 168, null): that now gives the same single-use 48-hour viewer invite.
--    Invites that were already out are cut down to the same rules.
-- 2. accept_invite: at most 10 wrong codes per account per hour, then that account is blocked for an hour
--    (HTTP 429, PT429). A wrong code returns no rows instead of an error - an error would roll the counter
--    back. (Old and new apps both read "no rows" as "invalid code".) Someone who is already a member gets
--    their trip back without using the code up (e.g. a retry after the first answer was lost).
-- 3. E-mail addresses: get_trip_members returns them only to the trip's owner (and each member's own);
--    editors and viewers see names and photos. Reading public.profiles directly no longer includes the
--    email column for anyone (column privileges); the app takes the user's own address from the session.
-- 4. get_recent_joins(since): the owner's app asks who joined their trips, to show "Ayşe joined Roma".
--
-- Every number lives in private.invite_limit(). Safe to run again.
-- =====================================================================================================

create or replace function private.invite_limit(p_key text) returns int
language sql
immutable
set search_path = ''
as $$
    select case p_key
        when 'valid_hours'        then 48   -- an invite works for at most this long
        when 'group_max_uses'     then 10   -- the most people one (group) code can bring in
        when 'failed_attempts'    then 10   -- wrong codes per account ...
        when 'attempt_window_min' then 60   -- ... within this many minutes ...
        when 'block_minutes'      then 60   -- ... block the account for this long
        when 'join_notice_days'   then 7    -- get_recent_joins looks back at most this far
    end;
$$;

-- ------------------------------------------------------------------------------------------- 1. create_invite
-- Same signature and answer as before (older app versions keep working); only the rules changed.
create or replace function public.create_invite(
    p_trip_id uuid, p_role text default 'viewer', p_valid_hours int default 48, p_max_uses int default 1)
returns table (code text, expires_at timestamptz)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_owner(p_trip_id);
    v_code  text;
    v_hours int;
    v_uses  int := coalesce(p_max_uses, 1);
begin
    if p_role is null or p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    if p_valid_hours is null or p_valid_hours < 1 then
        raise exception 'valid_hours must be at least 1' using errcode = '22023';
    end if;
    if v_uses < 1 or v_uses > private.invite_limit('group_max_uses') then
        raise exception 'max_uses must be between 1 and %', private.invite_limit('group_max_uses') using errcode = '22023';
    end if;
    if exists (select 1 from public.trips t where t.id = p_trip_id and t.deleted_at is not null) then
        raise exception 'trip is deleted' using errcode = '22023';
    end if;
    -- longer requests (older apps ask for 7 days) are cut down instead of refused
    v_hours := least(p_valid_hours, private.invite_limit('valid_hours'));

    loop
        v_code := private.new_invite_code();
        exit when not exists (select 1 from public.trip_invites i where i.code = v_code);
    end loop;

    -- everyone joins as a viewer; editing rights are given by the owner afterwards
    return query
    insert into public.trip_invites (trip_id, code, role, created_by, expires_at, max_uses)
    values (p_trip_id, v_code, 'viewer', v_uid, now() + make_interval(hours => v_hours), v_uses)
    returning trip_invites.code, trip_invites.expires_at;
end;
$$;

-- Invites already handed out under the old rules (unlimited, 7 days, editor): one more join at most, as a
-- viewer, within 48 hours from now. Invites that already follow the rules are left alone (safe to re-run).
update public.trip_invites i
set role       = 'viewer',
    max_uses   = least(coalesce(i.max_uses, i.used_count + 1), i.used_count + 1),
    expires_at = least(i.expires_at, now() + make_interval(hours => private.invite_limit('valid_hours')))
where i.revoked_at is null
  and i.expires_at > now()
  and (i.max_uses is null
       or i.max_uses > private.invite_limit('group_max_uses')
       or i.role <> 'viewer'
       or i.expires_at > now() + make_interval(hours => private.invite_limit('valid_hours')));

-- ------------------------------------------------------------------------------------------- 2. accept_invite
-- Wrong codes per account. Not reachable through the API.
create table if not exists private.invite_attempts (
    user_id       uuid primary key references auth.users (id) on delete cascade,
    window_start  timestamptz not null default now(),
    failures      int not null default 0,
    blocked_until timestamptz
);
alter table private.invite_attempts enable row level security;
revoke all on table private.invite_attempts from public, anon, authenticated;

-- Counts one wrong code; the attempt that reaches the limit blocks the account.
create or replace function private.note_failed_invite(p_uid uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_window   interval := make_interval(mins => private.invite_limit('attempt_window_min'));
    v_failures int;
begin
    insert into private.invite_attempts as a (user_id, window_start, failures)
    values (p_uid, now(), 1)
    on conflict (user_id) do update
        set failures     = case when a.window_start > now() - v_window then a.failures + 1 else 1 end,
            window_start = case when a.window_start > now() - v_window then a.window_start else now() end
    returning a.failures into v_failures;

    if v_failures >= private.invite_limit('failed_attempts') then
        update private.invite_attempts
        set blocked_until = now() + make_interval(mins => private.invite_limit('block_minutes')),
            failures      = 0,
            window_start  = now()
        where user_id = p_uid;
    end if;
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
    v_code   text := upper(regexp_replace(coalesce(p_code, ''), '[^A-Za-z0-9]', '', 'g'));
    v_invite public.trip_invites;
    v_role   text;
begin
    if exists (select 1 from private.invite_attempts a where a.user_id = v_uid and a.blocked_until > now()) then
        raise exception 'too many wrong invite codes, try again later' using errcode = 'PT429';
    end if;

    select * into v_invite from public.trip_invites i where i.code = v_code for update;

    -- Already a member (the owner typing their own code, or a retry after a lost answer): nothing is used up.
    if v_invite.id is not null and v_invite.revoked_at is null then
        select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
        if v_role is not null then
            return query select v_invite.trip_id, v_role, t.name from public.trips t where t.id = v_invite.trip_id;
            return;
        end if;
    end if;

    if v_invite.id is null or v_invite.revoked_at is not null or v_invite.expires_at < now()
       or (v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses)
       or exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        perform private.note_failed_invite(v_uid);
        return;   -- no rows = invalid / expired / used up (raising here would undo the counter)
    end if;

    insert into public.trip_members (trip_id, user_id, role, invited_by)
    values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
    update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;

    return query select v_invite.trip_id, v_invite.role, t.name from public.trips t where t.id = v_invite.trip_id;
end;
$$;

-- ------------------------------------------------------------------------------------------- 3. e-mails
create or replace function public.get_trip_members(p_trip_id uuid)
returns table (user_id uuid, role text, display_name text, email text, avatar_url text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_uid();
    v_owner boolean;
begin
    if not private.has_trip_role(p_trip_id, array['owner', 'editor', 'viewer']) then
        raise exception 'not a member of this trip' using errcode = '42501';
    end if;
    v_owner := private.has_trip_role(p_trip_id, array['owner']);

    return query
    select m.user_id, m.role, p.display_name,
           case when v_owner or m.user_id = v_uid then p.email end,   -- TP-4: only the owner sees e-mails
           p.avatar_url, m.joined_at
    from public.trip_members m
    left join public.profiles p on p.id = m.user_id
    where m.trip_id = p_trip_id
    order by case m.role when 'owner' then 0 when 'editor' then 1 else 2 end, m.joined_at;
end;
$$;

-- Trip mates still read each other's name and photo, but no longer the e-mail column (nor does anyone
-- else, through the API: the app reads the user's own address from the session).
revoke select on public.profiles from authenticated;
grant select (id, display_name, avatar_url, created_at, updated_at) on public.profiles to authenticated;

-- ------------------------------------------------------------------------------------------- 4. new members
-- People who joined the caller's own trips after p_since (at most join_notice_days back, at most 20).
create or replace function public.get_recent_joins(p_since timestamptz)
returns table (trip_id uuid, trip_name text, user_id uuid, display_name text, role text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_uid();
    v_floor timestamptz := now() - make_interval(days => private.invite_limit('join_notice_days'));
begin
    return query
    select m.trip_id, t.name, m.user_id, p.display_name, m.role, m.joined_at
    from public.trip_members mine
    join public.trips t on t.id = mine.trip_id and t.deleted_at is null
    join public.trip_members m on m.trip_id = mine.trip_id and m.user_id <> v_uid
    left join public.profiles p on p.id = m.user_id
    where mine.user_id = v_uid
      and mine.role = 'owner'
      and m.joined_at > greatest(coalesce(p_since, v_floor), v_floor)
    order by m.joined_at
    limit 20;
end;
$$;

-- ------------------------------------------------------------------------------------------- grants
do $$
declare f text;
begin
    foreach f in array array[
        'public.create_invite(uuid, text, int, int)',
        'public.accept_invite(text)',
        'public.get_trip_members(uuid)',
        'public.get_recent_joins(timestamptz)'] loop
        execute format('revoke all on function %s from public, anon', f);
        execute format('grant execute on function %s to authenticated', f);
    end loop;
end $$;

revoke all on function private.invite_limit(text) from public, anon, authenticated;
revoke all on function private.note_failed_invite(uuid) from public, anon, authenticated;

-- PostgREST: new function get_recent_joins.
notify pgrst, 'reload schema';
