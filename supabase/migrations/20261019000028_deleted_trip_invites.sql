-- =====================================================================================================
-- TravelPlanner - 28: invite codes of a deleted trip.
--
-- Until now:
--   * a trip moved to the trash kept its open invites. accept_invite refused them while the trip was in the trash,
--     but restoring the trip brought every code that hadn't expired back to life;
--   * a trip deleted for good (its owner closed the account) took its invites with it, so the code simply
--     didn't exist any more;
--   * either way the person typing the code only heard "invalid code", and it counted as a wrong guess (10 an
--     hour, then a block).
-- From now on:
--   1. moving a trip to the trash revokes its open invites (a restored trip needs a new code);
--   2. an invite that is still within its time when its trip is deleted for good leaves its code behind in
--      private.closed_invites until it would have expired;
--   3. accept_invite answers a code that exists but no longer works with one row without a trip_id and a reason:
--      'trip_deleted', 'revoked', 'expired' or 'used_up'. Such a code is not counted as a wrong guess (it was
--      not guessed). Older app versions read a row without trip_id as "invalid code", as before.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. trash
create or replace function private.revoke_invites_of_deleted_trip() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if new.deleted_at is not null and old.deleted_at is null then
        update public.trip_invites set revoked_at = now() where trip_id = new.id and revoked_at is null;
    end if;
    return null;
end;
$$;

drop trigger if exists trips_revoke_invites on public.trips;
create trigger trips_revoke_invites
    after update of deleted_at on public.trips
    for each row execute function private.revoke_invites_of_deleted_trip();

-- Invites already open on trips that are in the trash now.
update public.trip_invites i
set revoked_at = now()
from public.trips t
where t.id = i.trip_id and t.deleted_at is not null and i.revoked_at is null;

-- ------------------------------------------------------------------------------------------- 2. deleted for good
create table if not exists private.closed_invites (
    code       text primary key,
    expires_at timestamptz not null
);
alter table private.closed_invites enable row level security;
revoke all on table private.closed_invites from public, anon, authenticated;

create or replace function private.remember_closed_invite() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    -- only when the trip itself is gone (a cascade from public.trips), and the code could still be typed
    if old.expires_at > now() and not exists (select 1 from public.trips t where t.id = old.trip_id) then
        insert into private.closed_invites (code, expires_at) values (old.code, old.expires_at)
        on conflict (code) do update set expires_at = excluded.expires_at;
    end if;
    delete from private.closed_invites where expires_at < now();
    return null;
end;
$$;

drop trigger if exists trip_invites_remember_closed on public.trip_invites;
create trigger trip_invites_remember_closed
    after delete on public.trip_invites
    for each row execute function private.remember_closed_invite();

-- ------------------------------------------------------------------------------------------- 3. accept_invite
-- The answer gets a new column, so the function is dropped and created again (grants below).
drop function if exists public.accept_invite(text);

create function public.accept_invite(p_code text)
returns table (trip_id uuid, role text, trip_name text, reason text)
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
    v_reason text;
begin
    if exists (select 1 from private.invite_attempts a where a.user_id = v_uid and a.blocked_until > now()) then
        raise exception 'too many wrong invite codes, try again later' using errcode = 'PT429';
    end if;

    select * into v_invite from public.trip_invites i where i.code = v_code for update;

    -- Already a member (the owner typing their own code, or a retry after a lost answer): nothing is used up.
    if v_invite.id is not null and v_invite.revoked_at is null then
        select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
        if v_role is not null then
            return query select v_invite.trip_id, v_role, t.name, null::text from public.trips t where t.id = v_invite.trip_id;
            return;
        end if;
    end if;

    if v_invite.id is null then
        if exists (select 1 from private.closed_invites c where c.code = v_code and c.expires_at > now()) then
            v_reason := 'trip_deleted';
        else
            perform private.note_failed_invite(v_uid);
            return;   -- no rows = a code that never existed (raising here would undo the counter)
        end if;
    elsif exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        v_reason := 'trip_deleted';
    elsif v_invite.revoked_at is not null then
        v_reason := 'revoked';
    elsif v_invite.expires_at < now() then
        v_reason := 'expired';
    elsif v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses then
        v_reason := 'used_up';
    end if;

    -- a real code that no longer works: say why, and don't count it as a guess
    if v_reason is not null then
        return query select null::uuid, null::text, null::text, v_reason;
        return;
    end if;

    insert into public.trip_members (trip_id, user_id, role, invited_by)
    values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
    update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;

    return query select v_invite.trip_id, v_invite.role, t.name, null::text from public.trips t where t.id = v_invite.trip_id;
end;
$$;

revoke all on function public.accept_invite(text) from public, anon;
grant execute on function public.accept_invite(text) to authenticated;

revoke all on function private.revoke_invites_of_deleted_trip() from public, anon, authenticated;
revoke all on function private.remember_closed_invite() from public, anon, authenticated;

-- PostgREST: accept_invite answers with a new column.
notify pgrst, 'reload schema';
