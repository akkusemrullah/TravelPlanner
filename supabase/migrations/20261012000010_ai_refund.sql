-- =====================================================================================================
-- TravelPlanner - AI planner: a failed plan gives its quota back.
--
-- public.ai_plan_quota (migration 9) takes one plan of today's quota BEFORE the AI is asked, so two requests
-- at once can never get past the limit. When the AI then fails, the plan-trip Edge Function gives that plan
-- back with public.ai_plan_refund(ticket).
--
-- The ticket is a one-time receipt of that quota use. ai_plan_quota returns it to its caller - the Edge Function,
-- which never passes it on to the app - so a user can't give back a plan they really got: they could only
-- "refund" a quota use they made themselves by calling ai_plan_quota directly, which nets to nothing.
-- A ticket works once, for its own account, within 10 minutes. Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_quota_ticket (
    id         uuid primary key default gen_random_uuid(),
    user_id    uuid not null,
    device     text not null,
    day        date not null,
    created_at timestamptz not null default now(),
    refunded   boolean not null default false
);

alter table private.ai_quota_ticket enable row level security;

-- Same as migration 9, plus the ticket.
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
    v_ticket uuid;
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

    insert into private.ai_quota_ticket (user_id, device, day) values (v_uid, v_device, current_date)
    returning id into v_ticket;

    -- keep the tables small
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;
    delete from private.ai_quota_ticket where created_at < now() - interval '1 day';

    return jsonb_build_object('limit', v_limit, 'remaining', v_limit - greatest(v_user, v_dev) - 1, 'ticket', v_ticket);
end;
$$;

-- Gives back the plan of a ticket whose AI request failed. True when something was given back.
create or replace function public.ai_plan_refund(p_ticket uuid) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid := private.require_uid();
    t     private.ai_quota_ticket;
begin
    update private.ai_quota_ticket
       set refunded = true
     where id = p_ticket
       and user_id = v_uid
       and not refunded
       and created_at > now() - interval '10 minutes'
    returning * into t;

    if t.id is null then
        return false;
    end if;

    update private.ai_usage set count = count - 1 where user_id = t.user_id and day = t.day and count > 0;
    update private.ai_device_usage set count = count - 1 where device = t.device and day = t.day and count > 0;
    return true;
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_refund(uuid) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_refund(uuid) to authenticated;
