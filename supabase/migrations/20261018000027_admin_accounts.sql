-- =====================================================================================================
-- TravelPlanner - admin / 27: account operations and catalog delete for the admin panel.
--
--   admin_ban_user(id, days|null)   bans the account (null = for good) and signs it out on every device; the app
--                                   signs out and says why (migration 8), Supabase Auth refuses new sign-ins
--   admin_unban_user(id)            lets it sign in again
--   admin_sign_out_user(id)         signs it out on every device (refresh tokens stop working; an access token
--                                   still works until it expires, at most an hour)
--   admin_delete_user(id)           deletes the account exactly as "delete my account" in the app does
--                                   (public.delete_account: owned trips and their files, profile, memberships)
--   admin_explore_delete(slug)      deletes a sample trip for good (users' copies are independent and stay)
--   admin_user / admin_users        now also return banned_until; admin_overview counts banned accounts
--
-- Like migration 25: service_role only. Safe to run again.
-- =====================================================================================================

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
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
    return v_until;
end;
$$;

create or replace function public.admin_unban_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
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
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
end;
$$;

-- Runs public.delete_account as that account (auth.uid() reads request.jwt.claim.sub first), so there is one
-- deletion path. A banned account is unbanned first, because delete_account only works for active accounts.
create or replace function public.admin_delete_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform set_config('request.jwt.claim.sub', p_user_id::text, true);
    perform public.delete_account();
    perform set_config('request.jwt.claim.sub', '', true);
end;
$$;

create or replace function public.admin_explore_delete(p_slug text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    delete from public.explore_trips where slug = p_slug;
    if not found then
        raise exception 'no sample trip "%"', p_slug using errcode = 'P0002';
    end if;
end;
$$;

-- Start-page counts (migration 25, plus banned accounts).
create or replace function public.admin_overview() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'users',             (select count(*) from auth.users),
        'users_7d',          (select count(*) from auth.users where created_at > now() - interval '7 days'),
        'users_banned',      (select count(*) from auth.users where banned_until > now()),
        'trips',             (select count(*) from public.trips where deleted_at is null),
        'trips_7d',          (select count(*) from public.trips where deleted_at is null and created_at > now() - interval '7 days'),
        'ai_today',          coalesce((select count from private.ai_global_usage where day = current_date), 0),
        'ai_global_limit',   private.ai_global_daily_limit(),
        'ai_daily_limit',    private.ai_daily_limit(),
        'explore_published', (select count(*) from public.explore_trips where status = 'published'),
        'explore_draft',     (select count(*) from public.explore_trips where status = 'draft'),
        'explore_archived',  (select count(*) from public.explore_trips where status = 'archived'))
$$;

-- One account as the panel shows it (migration 26, plus banned_until).
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id), 'banned_until', u.banned_until,
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

do $$
declare f text;
begin
    foreach f in array array[
        'public.admin_ban_user(uuid, int)', 'public.admin_unban_user(uuid)', 'public.admin_sign_out_user(uuid)',
        'public.admin_delete_user(uuid)', 'public.admin_explore_delete(text)', 'public.admin_overview()'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;
