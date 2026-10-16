-- =====================================================================================================
-- TravelPlanner - AI trip planner (migration 9): a daily quota per account AND per device.
-- The Edge Function "plan-trip" calls ai_plan_quota(<device>) with the user's own token before it asks the
-- AI, so only signed-in, active (not banned) accounts can use it - at most ai_daily_limit() plans a day per
-- account, and the same per device: signing in with another account on the same phone doesn't give new plans.
-- <device> is a SHA-256 hash the app makes from the device's id; the raw id never leaves the phone.
-- The OpenAI key never leaves Supabase (Edge Function secret OPENAI_API_KEY).
-- Change the daily limit in private.ai_daily_limit() (and run this file again). Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_usage (
    user_id uuid not null references auth.users (id) on delete cascade,
    day     date not null default current_date,
    count   int  not null default 0,
    primary key (user_id, day)
);

create table if not exists private.ai_device_usage (
    device text not null check (device ~ '^[0-9a-f]{64}$'),
    day    date not null default current_date,
    count  int  not null default 0,
    primary key (device, day)
);

create or replace function private.ai_daily_limit() returns int
language sql immutable as $$ select 5 $$;

-- the first version had no device parameter
drop function if exists public.ai_plan_quota();
drop function if exists public.ai_plan_quota_status();

create or replace function private.ai_check_device(p_device text) returns text
language plpgsql immutable as $$
begin
    if p_device is null or p_device !~ '^[0-9a-f]{64}$' then
        raise exception 'invalid device id' using errcode = '22023';
    end if;
    return p_device;
end;
$$;

-- How many plans are left today for this account on this device (does not use one).
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_daily_limit();
    v_user   int;
    v_dev    int;
begin
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    return jsonb_build_object('limit', v_limit,
                              'remaining', greatest(v_limit - greatest(coalesce(v_user, 0), coalesce(v_dev, 0)), 0));
end;
$$;

-- Uses one plan of today's quota for both the account and the device; HTTP 429 (SQLSTATE PT429) when either
-- has none left.
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_daily_limit();
    v_user   int;
    v_dev    int;
begin
    insert into private.ai_usage (user_id, day, count) values (v_uid, current_date, 0)
    on conflict (user_id, day) do nothing;
    insert into private.ai_device_usage (device, day, count) values (v_device, current_date, 0)
    on conflict (device, day) do nothing;

    -- lock both rows, so two requests at once can't both take the last plan
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date for update;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date for update;

    if v_user >= v_limit or v_dev >= v_limit then
        raise exception 'daily AI planning limit reached' using errcode = 'PT429';
    end if;

    update private.ai_usage set count = count + 1 where user_id = v_uid and day = current_date;
    update private.ai_device_usage set count = count + 1 where device = v_device and day = current_date;

    -- keep the tables small: forget usage older than a month
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;

    return jsonb_build_object('limit', v_limit, 'remaining', v_limit - greatest(v_user, v_dev) - 1);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;
