-- =====================================================================================================
-- TravelPlanner - admin / 29: app settings managed from the admin panel.
--
-- Until now the AI limits were constants in SQL (private.ai_daily_limit = 5, ai_global_daily_limit = 200,
-- ai_new_account_limit = 2, ai_daily_refund_limit = 2) and changing one meant editing a migration. They now live
-- in one row, private.app_settings, together with the other things the panel manages:
--
--   ai_enabled / ai_paused_message   AI planning on, or paused for maintenance (with an optional message)
--   ai_*_limit                       the four AI limits above (same defaults)
--   announcement                     a short notice the app shows on its home page (empty = none)
--   min_app_version                  older app versions are asked to update (empty = nobody)
--   default_currency / _reminder_hour  defaults for users who haven't picked their own in Tercihler
--
--   public.app_config()              what the app reads (anon + signed in): everything above but the limits
--   public.admin_settings()          the whole row for the panel; admin_set_settings(json) changes it
--
-- The private.ai_*_limit functions keep their names and now read the row, so ai_plan_quota, admin_overview and
-- the rest use the new numbers without a change. When AI is paused ai_plan_quota answers HTTP 503 (SQLSTATE PT503;
-- plan-trip turns it into "ai_paused") and ai_plan_quota_status says paused. Safe to run again.
-- =====================================================================================================

create table if not exists private.app_settings (
    id                    boolean primary key default true check (id),
    ai_enabled            boolean not null default true,
    ai_paused_message     text check (char_length(ai_paused_message) <= 300),
    ai_daily_limit        int not null default 5 check (ai_daily_limit between 0 and 100),
    ai_new_account_limit  int not null default 2 check (ai_new_account_limit between 0 and 100),
    ai_global_daily_limit int not null default 200 check (ai_global_daily_limit between 0 and 100000),
    ai_daily_refund_limit int not null default 2 check (ai_daily_refund_limit between 0 and 20),
    announcement          text check (char_length(announcement) <= 300),
    min_app_version       text check (min_app_version ~ '^[0-9]{1,4}(\.[0-9]{1,4}){0,3}$'),
    default_currency      text not null default 'TRY' check (default_currency in ('TRY', 'EUR', 'USD', 'GBP')),
    default_reminder_hour int not null default 9 check (default_reminder_hour between 0 and 23),
    updated_at            timestamptz not null default now(),
    updated_by            text
);
alter table private.app_settings enable row level security;
revoke all on table private.app_settings from public, anon, authenticated;
insert into private.app_settings (id) values (true) on conflict (id) do nothing;

-- ------------------------------------------------------------------------------------------- AI limits
-- Same names as migrations 9 and 12; not "immutable" any more (they read the table). The defaults stand in when
-- the row is missing.
create or replace function private.ai_daily_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_daily_limit from private.app_settings where id), 5)
$$;

create or replace function private.ai_new_account_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_new_account_limit from private.app_settings where id), 2)
$$;

create or replace function private.ai_global_daily_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_global_daily_limit from private.app_settings where id), 200)
$$;

create or replace function private.ai_daily_refund_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_daily_refund_limit from private.app_settings where id), 2)
$$;

create or replace function private.ai_enabled() returns boolean
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_enabled from private.app_settings where id), true)
$$;

revoke all on function private.ai_daily_limit(), private.ai_new_account_limit(), private.ai_global_daily_limit(),
    private.ai_daily_refund_limit(), private.ai_enabled() from public, anon, authenticated;

-- Same as migration 26, plus: paused (and its message) when AI is in maintenance; nothing left then.
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    perform private.ai_note_device(v_uid, v_device);
    if not private.ai_enabled() then
        return jsonb_build_object('limit', v_limit, 'remaining', 0, 'paused', true,
                                  'message', (select ai_paused_message from private.app_settings where id));
    end if;
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    v_user := coalesce(v_user, 0);
    v_dev := case when v_own then 0 else coalesce(v_dev, 0) end;
    return jsonb_build_object(
        'limit', v_limit,
        'paused', false,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(v_user, v_dev), 0) end);
end;
$$;

-- Same as migration 26, plus: HTTP 503 (SQLSTATE PT503) while AI is paused - before any plan is counted.
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    if not private.ai_enabled() then
        raise exception 'AI planning is paused' using errcode = 'PT503';
    end if;

    perform private.ai_note_device(v_uid, v_device);

    insert into private.ai_usage (user_id, day, count) values (v_uid, current_date, 0)
    on conflict (user_id, day) do nothing;
    insert into private.ai_device_usage (device, day, count) values (v_device, current_date, 0)
    on conflict (device, day) do nothing;
    insert into private.ai_global_usage (day, count) values (current_date, 0)
    on conflict (day) do nothing;

    -- lock the rows (always in this order: account, device, global), so two requests at once can't both take
    -- the last plan
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date for update;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date for update;
    select count into v_global from private.ai_global_usage where day = current_date for update;

    if v_user >= v_limit or (not v_own and v_dev >= v_limit) then
        raise exception 'daily AI planning limit reached' using errcode = 'PT429';
    end if;
    if v_global >= private.ai_global_daily_limit() then
        raise exception 'global daily AI planning limit reached' using errcode = 'PT429';
    end if;

    update private.ai_usage set count = count + 1 where user_id = v_uid and day = current_date;
    update private.ai_device_usage set count = count + 1 where device = v_device and day = current_date;
    update private.ai_global_usage set count = count + 1 where day = current_date;

    insert into private.ai_quota_ticket (user_id, device, day) values (v_uid, v_device, current_date)
    returning id into v_ticket;

    -- keep the tables small
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;
    delete from private.ai_global_usage where day < current_date - 366;
    delete from private.ai_quota_ticket where created_at < now() - interval '2 days';
    delete from private.ai_user_devices where last_seen < current_date - 31;

    return jsonb_build_object('limit', v_limit,
                              'remaining', v_limit - (case when v_own then v_user else greatest(v_user, v_dev) end) - 1,
                              'ticket', v_ticket);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;

-- ------------------------------------------------------------------------------------------- app
-- What the app needs to know, also before sign-in (guests see the notice too). No limits, no admin data.
create or replace function public.app_config() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'ai_enabled', coalesce(s.ai_enabled, true),
        'ai_paused_message', s.ai_paused_message,
        'announcement', s.announcement,
        'min_app_version', s.min_app_version,
        'default_currency', coalesce(s.default_currency, 'TRY'),
        'default_reminder_hour', coalesce(s.default_reminder_hour, 9))
    from (select 1) one
    left join private.app_settings s on s.id
$$;
revoke all on function public.app_config() from public;
grant execute on function public.app_config() to anon, authenticated, service_role;

-- ------------------------------------------------------------------------------------------- panel
create or replace function public.admin_settings() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select to_jsonb(s) - 'id'
           || jsonb_build_object('ai_today', coalesce((select count from private.ai_global_usage where day = current_date), 0))
    from private.app_settings s
    where s.id
$$;

-- Changes the keys given in p (others stay); an empty text clears a text setting. Returns the new settings.
-- p.updated_by: who changed it (the panel sends the admin's e-mail).
create or replace function public.admin_set_settings(p jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
begin
    if p is null or jsonb_typeof(p) <> 'object' then
        raise exception 'settings must be a JSON object' using errcode = '22023';
    end if;
    insert into private.app_settings (id) values (true) on conflict (id) do nothing;

    update private.app_settings set
        ai_enabled            = case when p ? 'ai_enabled' then (p ->> 'ai_enabled')::boolean else ai_enabled end,
        ai_paused_message     = case when p ? 'ai_paused_message' then nullif(btrim(p ->> 'ai_paused_message'), '') else ai_paused_message end,
        ai_daily_limit        = case when p ? 'ai_daily_limit' then (p ->> 'ai_daily_limit')::int else ai_daily_limit end,
        ai_new_account_limit  = case when p ? 'ai_new_account_limit' then (p ->> 'ai_new_account_limit')::int else ai_new_account_limit end,
        ai_global_daily_limit = case when p ? 'ai_global_daily_limit' then (p ->> 'ai_global_daily_limit')::int else ai_global_daily_limit end,
        ai_daily_refund_limit = case when p ? 'ai_daily_refund_limit' then (p ->> 'ai_daily_refund_limit')::int else ai_daily_refund_limit end,
        announcement          = case when p ? 'announcement' then nullif(btrim(p ->> 'announcement'), '') else announcement end,
        min_app_version       = case when p ? 'min_app_version' then nullif(btrim(p ->> 'min_app_version'), '') else min_app_version end,
        default_currency      = case when p ? 'default_currency' then upper(p ->> 'default_currency') else default_currency end,
        default_reminder_hour = case when p ? 'default_reminder_hour' then (p ->> 'default_reminder_hour')::int else default_reminder_hour end,
        updated_at            = now(),
        updated_by            = left(nullif(btrim(p ->> 'updated_by'), ''), 200)
    where id;

    return public.admin_settings();
end;
$$;

do $$
declare f text;
begin
    foreach f in array array['public.admin_settings()', 'public.admin_set_settings(jsonb)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

notify pgrst, 'reload schema';
