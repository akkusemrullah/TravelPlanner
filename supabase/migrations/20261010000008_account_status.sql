-- =====================================================================================================
-- TravelPlanner - G7 / 8: banned and deleted accounts.
-- * The app asks account_status(<its user id>) at start-up and when it comes back to the foreground: if the
--   account was deleted or banned (Dashboard -> Authentication -> Users, or private.ban_user below) it signs
--   out and tells the user why. Works with the publishable key alone, because by then the session may be gone.
-- * Every RPC, table policy and Storage policy refuses a deleted or banned account at once (require_uid,
--   has_trip_role; apply_changes / pull_changes in migration 3), even while an access token issued before
--   the ban is still valid (up to an hour).
-- * Supabase Auth itself refuses to sign a banned user in again (e-mail or Google: error "user_banned").
-- Admin helpers, run in the SQL Editor:
--   select private.ban_user('someone@example.com');                 -- banned "for good" (100 years)
--   select private.ban_user('someone@example.com', interval '7 days');
--   select private.unban_user('someone@example.com');
-- Safe to run again.
-- =====================================================================================================

-- 'active' | 'banned' | 'deleted'
create or replace function private.account_state(p_uid uuid) returns text
language sql
stable
security definer
set search_path = ''
as $$
    select case
               when u.id is null then 'deleted'
               when u.banned_until is not null and u.banned_until > now() then 'banned'
               else 'active'
           end
    from (select 1) as one
    left join auth.users u on u.id = p_uid
$$;

revoke all on function private.account_state(uuid) from public, anon;
grant execute on function private.account_state(uuid) to authenticated;

-- Only the state is revealed, nothing else about the account.
create or replace function public.account_status(p_user_id uuid) returns text
language sql
stable
security definer
set search_path = ''
as $$
    select private.account_state(p_user_id)
$$;

revoke all on function public.account_status(uuid) from public;
grant execute on function public.account_status(uuid) to anon, authenticated;

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
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    return v_uid;
end;
$$;

-- Roles count only for an active account: RLS and Storage refuse a banned one at once too.
create or replace function private.has_trip_role(p_trip_id uuid, p_roles text[])
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members m
        where m.trip_id = p_trip_id
          and m.user_id = (select auth.uid())
          and m.role = any (p_roles))
       and private.account_state((select auth.uid())) = 'active';
$$;

-- ---------------------------------------------------------------------------------- admin helpers
create or replace function private.ban_user(p_email text, p_duration interval default interval '100 years') returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid;
    v_until timestamptz := now() + p_duration;
begin
    select id into v_uid from auth.users where lower(email) = lower(trim(p_email));
    if v_uid is null then
        raise exception 'no account with e-mail %', p_email;
    end if;

    update auth.users set banned_until = v_until where id = v_uid;
    -- sign out every device: their refresh tokens stop working at once
    delete from auth.sessions where user_id = v_uid;
    delete from auth.refresh_tokens where user_id = v_uid::text;

    return format('%s banned until %s', p_email, v_until);
end;
$$;

create or replace function private.unban_user(p_email text) returns text
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where lower(email) = lower(trim(p_email));
    if not found then
        raise exception 'no account with e-mail %', p_email;
    end if;
    return format('%s can sign in again', p_email);
end;
$$;

revoke all on function private.ban_user(text, interval) from public, anon, authenticated;
revoke all on function private.unban_user(text) from public, anon, authenticated;
