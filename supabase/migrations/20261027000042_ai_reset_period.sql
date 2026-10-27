-- =====================================================================================================
-- TravelPlanner - TP-92 / 42: when the AI plans come back is chosen in the admin panel, and so is the rest of AI.
--
-- Until now the plans of an account (and of a device) came back every day at 00:00 in Türkiye (migration 40).
-- private.app_settings (migration 29) now also says how often:
--
--   ai_reset_period     daily | days | weekly | monthly | never
--   ai_reset_hour       0..23, Türkiye time (the period starts at this hour)
--   ai_reset_days       2..365, for 'days' ("every N days", counted from ai_reset_anchor)
--   ai_reset_weekday    1..7 (ISO, 1 = Monday), for 'weekly'
--   ai_reset_monthday   1..28, for 'monthly'
--   ai_reset_anchor     the day the period settings last changed; 'days' counts from it, 'never' keeps it forever
--   ai_device_limit     false = only the account counts (the phone's count is still kept, just not checked)
--   ai_max_days         1..10: plan-trip plans at most this many days of a trip (it used to be 10, fixed)
--   ai_max_tokens       2000..32000: upper bound of the plan's max_completion_tokens; empty = the AI_MAX_TOKENS
--                       secret, else 16 000
--
-- 'never' is for a paid tier later: everyone keeps what is left of the current period and nothing comes back
-- (admin_set_ai_limit, admin_reset_ai_today and admin_reset_ai_all still give plans by hand).
--
-- How: private.ai_usage and private.ai_device_usage are still keyed by "day", but that day is now the first day of
-- the period (private.ai_period_start()); the global ceiling (ai_global_usage) stays per calendar day in Türkiye.
-- When the panel changes the period, the counts of the running period move to the new one, so a change neither
-- hands out plans nor takes them away; "Herkesin hakkını yenile" (admin_reset_ai_all) does that on purpose.
-- ai_quota_ticket gets a "period" column so a refund gives the plan back to the right row.
--
-- ai_plan_quota_status also tells the app the period, when it ends (resets_at, null for 'never') and max_days.
-- ai_model_settings also gives plan-trip max_days and max_tokens. Safe to run again.
-- =====================================================================================================

alter table private.app_settings add column if not exists ai_reset_period text not null default 'daily';
alter table private.app_settings add column if not exists ai_reset_hour int not null default 0;
alter table private.app_settings add column if not exists ai_reset_days int not null default 7;
alter table private.app_settings add column if not exists ai_reset_weekday int not null default 1;
alter table private.app_settings add column if not exists ai_reset_monthday int not null default 1;
alter table private.app_settings add column if not exists ai_reset_anchor date;
alter table private.app_settings add column if not exists ai_device_limit boolean not null default true;
alter table private.app_settings add column if not exists ai_max_days int not null default 10;
alter table private.app_settings add column if not exists ai_max_tokens int;

do $$
declare c record;
begin
    for c in select * from (values
        ('app_settings_ai_reset_period_check', 'check (ai_reset_period in (''daily'', ''days'', ''weekly'', ''monthly'', ''never''))'),
        ('app_settings_ai_reset_hour_check', 'check (ai_reset_hour between 0 and 23)'),
        ('app_settings_ai_reset_days_check', 'check (ai_reset_days between 2 and 365)'),
        ('app_settings_ai_reset_weekday_check', 'check (ai_reset_weekday between 1 and 7)'),
        ('app_settings_ai_reset_monthday_check', 'check (ai_reset_monthday between 1 and 28)'),
        ('app_settings_ai_max_days_check', 'check (ai_max_days between 1 and 10)'),
        ('app_settings_ai_max_tokens_check', 'check (ai_max_tokens between 2000 and 32000)')) v(name, def)
    loop
        if not exists (select 1 from pg_constraint where conname = c.name and conrelid = 'private.app_settings'::regclass) then
            execute format('alter table private.app_settings add constraint %I %s', c.name, c.def);
        end if;
    end loop;
end $$;

alter table private.ai_quota_ticket add column if not exists period date;

-- ------------------------------------------------------------------------------------------- the period
-- The first day of the period p_at falls in. A period starts at ai_reset_hour (Türkiye time) of that day.
create or replace function private.ai_period_start(p_at timestamptz default now()) returns date
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    s private.app_settings;
    v_day date;
    v_anchor date;
    v_md int;
begin
    select * into s from private.app_settings where id;
    v_day := ((p_at at time zone 'Europe/Istanbul') - make_interval(hours => coalesce(s.ai_reset_hour, 0)))::date;
    v_anchor := coalesce(s.ai_reset_anchor, date '2026-01-01');
    case coalesce(s.ai_reset_period, 'daily')
        when 'days' then
            return v_anchor + (floor((v_day - v_anchor)::numeric / s.ai_reset_days) * s.ai_reset_days)::int;
        when 'weekly' then
            return v_day - ((extract(isodow from v_day)::int - s.ai_reset_weekday + 7) % 7);
        when 'monthly' then
            v_md := s.ai_reset_monthday;
            if extract(day from v_day)::int >= v_md then
                return (date_trunc('month', v_day)::date + (v_md - 1));
            end if;
            return ((date_trunc('month', v_day) - interval '1 month')::date + (v_md - 1));
        when 'never' then
            return v_anchor;
        else
            return v_day;
    end case;
end;
$$;

-- When the period starting on p_start ends (the next reset); null for 'never'.
create or replace function private.ai_period_end(p_start date) returns timestamptz
language sql
stable
security definer
set search_path = ''
as $$
    select case coalesce(s.ai_reset_period, 'daily')
               when 'never' then null
               else ((case coalesce(s.ai_reset_period, 'daily')
                          when 'days' then p_start + s.ai_reset_days
                          when 'weekly' then p_start + 7
                          when 'monthly' then (p_start + interval '1 month')::date
                          else p_start + 1
                      end)::timestamp + make_interval(hours => coalesce(s.ai_reset_hour, 0))) at time zone 'Europe/Istanbul'
           end
    from (select 1) one
    left join private.app_settings s on s.id
$$;

create or replace function private.ai_device_limit() returns boolean
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_device_limit from private.app_settings where id), true)
$$;

revoke all on function private.ai_period_start(timestamptz), private.ai_period_end(date), private.ai_device_limit()
    from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- the app
-- Same as migration 29, counted by period; plus period, resets_at and max_days for the app. An account with its own
-- limit, or ai_device_limit = false, ignores the phone's count.
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid) or not private.ai_device_limit();
    v_period date := private.ai_period_start();
    v_info   jsonb;
    v_user   int;
    v_dev    int;
    v_global int;
begin
    perform private.ai_note_device(v_uid, v_device);
    v_info := jsonb_build_object(
        'period', coalesce((select ai_reset_period from private.app_settings where id), 'daily'),
        'resets_at', private.ai_period_end(v_period),
        'max_days', coalesce((select ai_max_days from private.app_settings where id), 10));
    if not private.ai_enabled() then
        return v_info || jsonb_build_object('limit', v_limit, 'remaining', 0, 'paused', true,
                                            'message', (select ai_paused_message from private.app_settings where id));
    end if;
    select count into v_user from private.ai_usage where user_id = v_uid and day = v_period;
    select count into v_dev from private.ai_device_usage where device = v_device and day = v_period;
    select count into v_global from private.ai_global_usage where day = current_date;
    v_user := coalesce(v_user, 0);
    v_dev := case when v_own then 0 else coalesce(v_dev, 0) end;
    return v_info || jsonb_build_object(
        'limit', v_limit,
        'paused', false,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(v_user, v_dev), 0) end);
end;
$$;

-- Same as migration 29, counted by period (the global ceiling stays per day).
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid) or not private.ai_device_limit();
    v_period date := private.ai_period_start();
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    if not private.ai_enabled() then
        raise exception 'AI planning is paused' using errcode = 'PT503';
    end if;

    perform private.ai_note_device(v_uid, v_device);

    insert into private.ai_usage (user_id, day, count) values (v_uid, v_period, 0)
    on conflict (user_id, day) do nothing;
    insert into private.ai_device_usage (device, day, count) values (v_device, v_period, 0)
    on conflict (device, day) do nothing;
    insert into private.ai_global_usage (day, count) values (current_date, 0)
    on conflict (day) do nothing;

    -- lock the rows (always in this order: account, device, global), so two requests at once can't both take
    -- the last plan
    select count into v_user from private.ai_usage where user_id = v_uid and day = v_period for update;
    select count into v_dev from private.ai_device_usage where device = v_device and day = v_period for update;
    select count into v_global from private.ai_global_usage where day = current_date for update;

    if v_user >= v_limit or (not v_own and v_dev >= v_limit) then
        raise exception 'AI planning limit reached' using errcode = 'PT429';
    end if;
    if v_global >= private.ai_global_daily_limit() then
        raise exception 'global daily AI planning limit reached' using errcode = 'PT429';
    end if;

    update private.ai_usage set count = count + 1 where user_id = v_uid and day = v_period;
    update private.ai_device_usage set count = count + 1 where device = v_device and day = v_period;
    update private.ai_global_usage set count = count + 1 where day = current_date;

    insert into private.ai_quota_ticket (user_id, device, day, period) values (v_uid, v_device, current_date, v_period)
    returning id into v_ticket;

    -- keep the tables small (the running period's rows always stay, however old it is)
    delete from private.ai_usage where day < least(current_date, v_period) - 31;
    delete from private.ai_device_usage where day < least(current_date, v_period) - 31;
    delete from private.ai_global_usage where day < current_date - 366;
    delete from private.ai_quota_ticket where created_at < now() - interval '2 days';
    delete from private.ai_user_devices where last_seen < current_date - 31;

    return jsonb_build_object('limit', v_limit,
                              'remaining', v_limit - (case when v_own then v_user else greatest(v_user, v_dev) end) - 1,
                              'ticket', v_ticket);
end;
$$;

-- Same as migration 12; the account and device rows are the ticket's period (older tickets: its day). The refund
-- limit stays per calendar day.
create or replace function public.ai_plan_refund(p_ticket uuid) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid      uuid := private.require_uid();
    v_refunded int;
    t          private.ai_quota_ticket;
begin
    select * into t
      from private.ai_quota_ticket
     where id = p_ticket
       and user_id = v_uid
       and not refunded
       and created_at > now() - interval '10 minutes';
    if t.id is null then
        return false;
    end if;

    -- the account's usage row serializes refunds of one account (same lock order as ai_plan_quota)
    perform 1 from private.ai_usage where user_id = t.user_id and day = coalesce(t.period, t.day) for update;

    select count(*) into v_refunded
      from private.ai_quota_ticket
     where user_id = v_uid and day = t.day and refunded;
    if v_refunded >= private.ai_daily_refund_limit() then
        return false;
    end if;

    update private.ai_quota_ticket set refunded = true where id = t.id and not refunded;
    if not found then
        return false;
    end if;

    update private.ai_usage set count = count - 1 where user_id = t.user_id and day = coalesce(t.period, t.day) and count > 0;
    update private.ai_device_usage set count = count - 1 where device = t.device and day = coalesce(t.period, t.day) and count > 0;
    update private.ai_global_usage set count = count - 1 where day = t.day and count > 0;
    return true;
end;
$$;

revoke all on function public.ai_plan_quota(text), public.ai_plan_quota_status(text), public.ai_plan_refund(uuid) from public, anon;
grant execute on function public.ai_plan_quota(text), public.ai_plan_quota_status(text), public.ai_plan_refund(uuid) to authenticated;

-- plan-trip asks this once per plan, with the service role key (migration 31, plus max_days and max_tokens).
create or replace function public.ai_model_settings() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object('model', s.ai_model, 'reasoning', s.ai_reasoning,
                              'max_days', s.ai_max_days, 'max_tokens', s.ai_max_tokens)
    from (select 1) one
    left join private.app_settings s on s.id
$$;

-- ------------------------------------------------------------------------------------------- panel
-- One account as the panel shows it (migration 37); "today" is now the running period.
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id),
        'banned_until', u.banned_until,
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = private.ai_period_start()), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = private.ai_period_start()
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id),
        'device_accounts', private.device_account_count(u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

-- Same as migration 26, for the running period.
create or replace function public.admin_reset_ai_today(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
declare
    v_period date := private.ai_period_start();
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    -- same lock order as ai_plan_quota: account, then device
    update private.ai_usage set count = 0 where user_id = p_user_id and day = v_period;
    update private.ai_device_usage d set count = 0
     where d.day = v_period
       and (d.device in (select t.device from private.ai_quota_ticket t where t.user_id = p_user_id and coalesce(t.period, t.day) = v_period)
            or d.device in (select u.device from private.ai_user_devices u where u.user_id = p_user_id));
end;
$$;

-- Everyone gets their plans back now (the running period starts over); the number of accounts that had used some.
create or replace function public.admin_reset_ai_all() returns int
language plpgsql
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
declare
    v_period date := private.ai_period_start();
    v_count  int;
begin
    delete from private.ai_usage where day = v_period and count > 0;
    get diagnostics v_count = row_count;
    delete from private.ai_usage where day = v_period;
    delete from private.ai_device_usage where day = v_period;
    return v_count;
end;
$$;

-- The row for the panel, plus today's global count and the running period.
create or replace function public.admin_settings() returns jsonb
language sql
stable
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
    select to_jsonb(s) - 'id'
           || jsonb_build_object('ai_today', coalesce((select count from private.ai_global_usage where day = current_date), 0),
                                 'ai_period_start', private.ai_period_start(),
                                 'ai_period_end', private.ai_period_end(private.ai_period_start()),
                                 'ai_period_users', (select count(*) from private.ai_usage a
                                                      where a.day = private.ai_period_start() and a.count > 0))
    from private.app_settings s
    where s.id
$$;

-- Same as migration 31, plus the period keys, ai_device_limit, ai_max_days and ai_max_tokens (empty = default).
-- A change of the period carries the running period's counts over to the new one.
create or replace function public.admin_set_settings(p jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
set timezone = 'Europe/Istanbul'
as $$
declare
    v_old    date;
    v_new    date;
    v_before jsonb;
    v_after  jsonb;
begin
    if p is null or jsonb_typeof(p) <> 'object' then
        raise exception 'settings must be a JSON object' using errcode = '22023';
    end if;
    insert into private.app_settings (id) values (true) on conflict (id) do nothing;

    select jsonb_build_array(ai_reset_period, ai_reset_hour, ai_reset_days, ai_reset_weekday, ai_reset_monthday)
      into v_before from private.app_settings where id;
    v_old := private.ai_period_start();

    update private.app_settings set
        ai_enabled            = case when p ? 'ai_enabled' then (p ->> 'ai_enabled')::boolean else ai_enabled end,
        ai_paused_message     = case when p ? 'ai_paused_message' then nullif(btrim(p ->> 'ai_paused_message'), '') else ai_paused_message end,
        ai_model              = case when p ? 'ai_model' then nullif(lower(btrim(p ->> 'ai_model')), '') else ai_model end,
        ai_reasoning          = case when p ? 'ai_reasoning' then nullif(lower(btrim(p ->> 'ai_reasoning')), '') else ai_reasoning end,
        ai_daily_limit        = case when p ? 'ai_daily_limit' then (p ->> 'ai_daily_limit')::int else ai_daily_limit end,
        ai_new_account_limit  = case when p ? 'ai_new_account_limit' then (p ->> 'ai_new_account_limit')::int else ai_new_account_limit end,
        ai_global_daily_limit = case when p ? 'ai_global_daily_limit' then (p ->> 'ai_global_daily_limit')::int else ai_global_daily_limit end,
        ai_daily_refund_limit = case when p ? 'ai_daily_refund_limit' then (p ->> 'ai_daily_refund_limit')::int else ai_daily_refund_limit end,
        ai_reset_period       = case when p ? 'ai_reset_period' then lower(btrim(p ->> 'ai_reset_period')) else ai_reset_period end,
        ai_reset_hour         = case when p ? 'ai_reset_hour' then (p ->> 'ai_reset_hour')::int else ai_reset_hour end,
        ai_reset_days         = case when p ? 'ai_reset_days' then (p ->> 'ai_reset_days')::int else ai_reset_days end,
        ai_reset_weekday      = case when p ? 'ai_reset_weekday' then (p ->> 'ai_reset_weekday')::int else ai_reset_weekday end,
        ai_reset_monthday     = case when p ? 'ai_reset_monthday' then (p ->> 'ai_reset_monthday')::int else ai_reset_monthday end,
        ai_device_limit       = case when p ? 'ai_device_limit' then (p ->> 'ai_device_limit')::boolean else ai_device_limit end,
        ai_max_days           = case when p ? 'ai_max_days' then (p ->> 'ai_max_days')::int else ai_max_days end,
        ai_max_tokens         = case when p ? 'ai_max_tokens' then nullif(btrim(p ->> 'ai_max_tokens'), '')::int else ai_max_tokens end,
        announcement          = case when p ? 'announcement' then nullif(btrim(p ->> 'announcement'), '') else announcement end,
        min_app_version       = case when p ? 'min_app_version' then nullif(btrim(p ->> 'min_app_version'), '') else min_app_version end,
        default_currency      = case when p ? 'default_currency' then upper(p ->> 'default_currency') else default_currency end,
        default_reminder_hour = case when p ? 'default_reminder_hour' then (p ->> 'default_reminder_hour')::int else default_reminder_hour end,
        updated_at            = now(),
        updated_by            = left(nullif(btrim(p ->> 'updated_by'), ''), 200)
    where id;

    select jsonb_build_array(ai_reset_period, ai_reset_hour, ai_reset_days, ai_reset_weekday, ai_reset_monthday)
      into v_after from private.app_settings where id;
    if v_after is distinct from v_before then
        -- 'days' counts from today, 'never' keeps today's key for good
        update private.app_settings
           set ai_reset_anchor = ((now() at time zone 'Europe/Istanbul') - make_interval(hours => ai_reset_hour))::date
         where id;
        v_new := private.ai_period_start();
        if v_new <> v_old then
            -- the new key may hold old counts (e.g. Monday's, when daily becomes weekly): the running period wins
            delete from private.ai_usage where day = v_new;
            update private.ai_usage set day = v_new where day = v_old;
            delete from private.ai_device_usage where day = v_new;
            update private.ai_device_usage set day = v_new where day = v_old;
            update private.ai_quota_ticket set period = v_new where period = v_old;
        end if;
    end if;

    return public.admin_settings();
end;
$$;

do $$
declare f text;
begin
    foreach f in array array['public.ai_model_settings()', 'public.admin_settings()', 'public.admin_set_settings(jsonb)',
                             'public.admin_reset_ai_today(uuid)', 'public.admin_reset_ai_all()'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

notify pgrst, 'reload schema';
