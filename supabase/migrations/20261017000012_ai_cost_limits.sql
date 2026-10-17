-- =====================================================================================================
-- TravelPlanner - SEC-02 / 12: the AI planner's cost can't run away.
--
-- Migration 9 counts plans per account and per device, migration 10 gives a plan back when the AI fails.
-- Neither is a real limit on its own: the device id is just a value the app sends (any 64-character hex string
-- passes) and new accounts cost nothing. So on top of them:
--
--   1. A global daily ceiling for ALL users together (private.ai_global_daily_limit, default 200 plans):
--      past it everyone gets HTTP 429 until midnight (UTC) - the OpenAI bill of a day has an upper bound.
--   2. New accounts get fewer plans in their first 24 hours (private.ai_new_account_limit, default 2), so
--      opening throw-away accounts multiplies far less.
--   3. At most private.ai_daily_refund_limit() (default 2) plans are given back per account and day. The Edge
--      Function now refunds only when OpenAI gave no answer at all (network error, HTTP error) - an answer that
--      was cut off or couldn't be parsed is billed by OpenAI and is not refunded.
--
-- The device id stays a helper signal (one phone can't collect plans with many accounts); the real protection
-- is the account quota + the new-account quota + the global ceiling (+ a monthly hard limit in the OpenAI panel).
-- Change the numbers in the private.ai_* functions below and run this file again. Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_global_usage (
    day   date primary key default current_date,
    count int  not null default 0
);
alter table private.ai_global_usage enable row level security;

create or replace function private.ai_global_daily_limit() returns int
language sql immutable as $$ select 200 $$;

create or replace function private.ai_new_account_limit() returns int
language sql immutable as $$ select 2 $$;

create or replace function private.ai_daily_refund_limit() returns int
language sql immutable as $$ select 2 $$;

-- Today's plan limit of an account: lower in its first 24 hours.
create or replace function private.ai_limit_for(p_uid uuid) returns int
language sql
stable
security definer
set search_path = ''
as $$
    select case
               when u.created_at is not null and u.created_at > now() - interval '24 hours'
                   then least(private.ai_new_account_limit(), private.ai_daily_limit())
               else private.ai_daily_limit()
           end
      from (select 1) one
      left join auth.users u on u.id = p_uid
$$;
revoke all on function private.ai_limit_for(uuid) from public, anon, authenticated;

-- How many plans are left today for this account on this device (does not use one). 0 also when the global
-- ceiling is reached.
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    return jsonb_build_object(
        'limit', v_limit,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(coalesce(v_user, 0), coalesce(v_dev, 0)), 0) end);
end;
$$;

-- Uses one plan of today's quota for the account, the device and the global ceiling; HTTP 429 (SQLSTATE PT429)
-- when any of them has none left. Returns the ticket for ai_plan_refund (migration 10).
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
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

    if v_user >= v_limit or v_dev >= v_limit then
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

    return jsonb_build_object('limit', v_limit, 'remaining', v_limit - greatest(v_user, v_dev) - 1, 'ticket', v_ticket);
end;
$$;

-- Gives back the plan of a ticket whose AI request got no answer. True when something was given back.
-- A ticket works once, for its own account, within 10 minutes - and at most ai_daily_refund_limit() a day.
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

    -- the account's usage row of that day serializes refunds of one account (same lock order as ai_plan_quota)
    perform 1 from private.ai_usage where user_id = t.user_id and day = t.day for update;

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

    update private.ai_usage set count = count - 1 where user_id = t.user_id and day = t.day and count > 0;
    update private.ai_device_usage set count = count - 1 where device = t.device and day = t.day and count > 0;
    update private.ai_global_usage set count = count - 1 where day = t.day and count > 0;
    return true;
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
revoke all on function public.ai_plan_refund(uuid) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;
grant execute on function public.ai_plan_refund(uuid) to authenticated;
