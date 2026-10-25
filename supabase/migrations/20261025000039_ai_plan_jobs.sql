-- =====================================================================================================
-- TravelPlanner - AI / 39: AI plans made in the background (TP-57).
--
-- The app used to wait for plan-trip's answer on the planner page: leaving the page (or Android stopping the app)
-- lost the plan while the quota was spent. Now the app gives each request an id (a uuid it keeps on the phone),
-- plan-trip answers at once and makes the plan in the background, writing the result here; the app asks for it
-- until it is ready - also after being closed and opened again.
--
--   private.ai_plan_jobs                     one row per request: running → done (the answer) | failed (the error code)
--   public.ai_plan_job_begin(id)             plan-trip, with the USER's token, before the quota: 'ok', 'busy' (this
--                                            account already has a plan being made) or 'exists' (the id is taken)
--   public.ai_plan_job_finish(id, ...)       plan-trip, with the service key: the answer or the error
--   public.ai_plan_job(id)                   the app: {status: running | done | failed, result, error, remaining} or null
--   public.ai_plan_job_clear(id)             the app: "I have it": the plan is deleted from the server
--   private.purge_ai_plan_jobs()             rows older than one day go (pg_cron, every night at 03:57)
--
-- A job that is still "running" after 5 minutes is dead (the function has at most 400 s): it counts as failed
-- ("timeout") and no longer blocks a new plan. Deleting the account deletes its rows (foreign key).
--
-- Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_plan_jobs (
    id          uuid primary key,
    user_id     uuid not null references auth.users (id) on delete cascade,
    status      text not null default 'running' check (status in ('running', 'done', 'failed')),
    result      jsonb,
    error       text,
    remaining   int,
    created_at  timestamptz not null default now(),
    finished_at timestamptz
);
alter table private.ai_plan_jobs enable row level security;
revoke all on private.ai_plan_jobs from public, anon, authenticated;

-- one plan at a time per account
create unique index if not exists ai_plan_jobs_one_running on private.ai_plan_jobs (user_id) where status = 'running';
create index if not exists ai_plan_jobs_created_idx on private.ai_plan_jobs (created_at);

/** Running jobs of an account older than 5 minutes are failed ("timeout"). */
create or replace function private.ai_plan_jobs_expire(p_user_id uuid) returns void
language sql
security definer
set search_path = ''
as $$
    update private.ai_plan_jobs
    set status = 'failed', error = 'timeout', finished_at = now()
    where user_id = p_user_id and status = 'running' and created_at < now() - interval '5 minutes'
$$;
revoke all on function private.ai_plan_jobs_expire(uuid) from public, anon, authenticated;

create or replace function public.ai_plan_job_begin(p_id uuid) returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid := private.require_uid();
begin
    if p_id is null then
        raise exception 'job id required' using errcode = '22023';
    end if;
    perform private.ai_plan_jobs_expire(v_uid);
    if exists (select 1 from private.ai_plan_jobs where id = p_id) then
        return 'exists';
    end if;
    begin
        insert into private.ai_plan_jobs (id, user_id) values (p_id, v_uid);
    exception when unique_violation then
        -- the other running plan of this account (or the same id sent twice at once)
        return case when exists (select 1 from private.ai_plan_jobs where id = p_id and user_id <> v_uid) then 'exists' else 'busy' end;
    end;
    return 'ok';
end;
$$;
revoke all on function public.ai_plan_job_begin(uuid) from public, anon;
grant execute on function public.ai_plan_job_begin(uuid) to authenticated;

create or replace function public.ai_plan_job_finish(p_id uuid, p_status text, p_result jsonb default null,
                                                     p_error text default null, p_remaining int default null) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
begin
    if p_status not in ('done', 'failed') then
        raise exception 'status must be done or failed' using errcode = '22023';
    end if;
    update private.ai_plan_jobs
    set status = p_status,
        result = case when p_status = 'done' then p_result end,
        error = case when p_status = 'failed' then left(coalesce(nullif(btrim(p_error), ''), 'ai_failed'), 60) end,
        remaining = p_remaining,
        finished_at = now()
    where id = p_id and status = 'running';
    return found;
end;
$$;
revoke all on function public.ai_plan_job_finish(uuid, text, jsonb, text, int) from public, anon, authenticated;
grant execute on function public.ai_plan_job_finish(uuid, text, jsonb, text, int) to service_role;

create or replace function public.ai_plan_job(p_id uuid) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid := private.require_uid();
    v_job private.ai_plan_jobs;
begin
    perform private.ai_plan_jobs_expire(v_uid);
    select * into v_job from private.ai_plan_jobs where id = p_id and user_id = v_uid;
    if not found then
        return null;
    end if;
    return jsonb_strip_nulls(jsonb_build_object('status', v_job.status, 'result', v_job.result, 'error', v_job.error,
                                                'remaining', v_job.remaining));
end;
$$;
revoke all on function public.ai_plan_job(uuid) from public, anon;
grant execute on function public.ai_plan_job(uuid) to authenticated;

create or replace function public.ai_plan_job_clear(p_id uuid) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid := private.require_uid();
begin
    delete from private.ai_plan_jobs where id = p_id and user_id = v_uid and status <> 'running';
    return found;
end;
$$;
revoke all on function public.ai_plan_job_clear(uuid) from public, anon;
grant execute on function public.ai_plan_job_clear(uuid) to authenticated;

create or replace function private.purge_ai_plan_jobs() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_count int;
begin
    delete from private.ai_plan_jobs where created_at < now() - interval '1 day';
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;
revoke all on function private.purge_ai_plan_jobs() from public, anon, authenticated;

-- Every night at 03:57, after the file clean-up (03:47, migration 16).
-- Needs pg_cron (Database -> Extensions); without it the purge is not scheduled and this says so.
do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-ai-plan-jobs';
        perform cron.schedule('travelplanner-purge-ai-plan-jobs', '57 3 * * *', 'select private.purge_ai_plan_jobs()');
    else
        raise notice 'pg_cron missing: old AI plan jobs are not purged. Enable it in Database -> Extensions and re-run this file.';
    end if;
end $$;

notify pgrst, 'reload schema';
