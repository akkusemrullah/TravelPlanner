-- =====================================================================================================
-- TravelPlanner - admin / 30: "Tüm cihazlardan çıkış" takes effect at once.
--
-- admin_sign_out_user (migration 27) deletes the account's sessions and refresh tokens, but an access token the
-- phone already has stays valid until it expires (up to an hour), and the app only noticed when its refresh failed.
-- Now the time of the sign-out is kept (private.forced_sign_outs) and account_status answers 'signed_out' for a
-- token issued before it; the app checks account_status at start and when it comes back, and signs out.
-- A ban does the same. Signing in again afterwards gives a new token, which is fine. Safe to run again.
-- =====================================================================================================

create table if not exists private.forced_sign_outs (
    user_id uuid primary key references auth.users (id) on delete cascade,
    at      timestamptz not null default now()
);
alter table private.forced_sign_outs enable row level security;
revoke all on table private.forced_sign_outs from public, anon, authenticated;

create or replace function private.force_sign_out(p_user_id uuid) returns void
language sql
security definer
set search_path = ''
as $$
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
    insert into private.forced_sign_outs (user_id, at) values (p_user_id, now())
    on conflict (user_id) do update set at = excluded.at;
$$;
revoke all on function private.force_sign_out(uuid) from public, anon, authenticated;

-- Same as migration 27, through private.force_sign_out.
create or replace function public.admin_ban_user(p_user_id uuid, p_days int default null) returns timestamptz
language plpgsql
security definer
set search_path = ''
as $$
declare v_until timestamptz;
begin
    if p_days is not null and p_days not between 1 and 3650 then
        raise exception 'days must be between 1 and 3650' using errcode = '22023';
    end if;
    v_until := now() + coalesce(make_interval(days => p_days), interval '100 years');

    update auth.users set banned_until = v_until where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform private.force_sign_out(p_user_id);
    return v_until;
end;
$$;

create or replace function public.admin_sign_out_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform private.force_sign_out(p_user_id);
end;
$$;

-- Same as migration 21, plus 'signed_out': the caller's token was issued before the admin signed the account out.
create or replace function public.account_status(p_user_id uuid) returns text
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid   uuid := (select auth.uid());
    v_state text;
    v_iat   bigint;
begin
    -- (not require_uid: a deleted or banned account must still be able to learn that it is)
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if p_user_id is distinct from v_uid then
        raise exception 'only your own account' using errcode = '42501';
    end if;
    v_state := private.account_state(v_uid);
    if v_state <> 'active' then
        return v_state;
    end if;

    v_iat := case when (auth.jwt() ->> 'iat') ~ '^[0-9]+$' then (auth.jwt() ->> 'iat')::bigint end;
    if v_iat is not null and exists (select 1 from private.forced_sign_outs f
                                     where f.user_id = v_uid and f.at > to_timestamp(v_iat)) then
        return 'signed_out';
    end if;
    return 'active';
end;
$$;

revoke all on function public.account_status(uuid) from public, anon;
grant execute on function public.account_status(uuid) to authenticated;

do $$
declare f text;
begin
    foreach f in array array['public.admin_ban_user(uuid, int)', 'public.admin_sign_out_user(uuid)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;
