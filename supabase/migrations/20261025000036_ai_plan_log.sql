-- =====================================================================================================
-- TravelPlanner - log / 36: AI plan log (TP-54).
--
-- plan-trip writes one audit log row per request (migration 34, through public.audit_log_write with the service key):
--   action "ai.plan", actor = the user, target_type "ai_plan", target_id = the request id it sends back to the app,
--   detail {status: ok | error | refunded | quota_exceeded | paused | ..., model, reasoning, duration_s,
--           tokens {prompt, completion, reasoning}, error_kind, error, days, places, replaced, dropped}.
-- Summary only: the plan, the destination and the user's notes are never logged.
--
--   public.ai_plan_saved(request, seconds)   the app: "I saved this plan n seconds after it came" - adds
--                                            detail.saved_after_s to the user's own row of the last day, once
--   public.admin_ai_plans(user, limit)       the panel's user page: that account's latest AI plan requests
--
-- Safe to run again.
-- =====================================================================================================

create index if not exists audit_log_actor_id_idx on private.audit_log (actor_id, action, id desc);

create or replace function public.ai_plan_saved(p_request_id text, p_seconds int) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid := private.require_uid();
begin
    update private.audit_log
    set detail = detail || jsonb_build_object('saved_after_s', greatest(0, least(coalesce(p_seconds, 0), 86400)))
    where action = 'ai.plan'
      and target_type = 'ai_plan'
      and target_id = left(btrim(coalesce(p_request_id, '')), 200)
      and actor_id = v_uid
      and created_at > now() - interval '1 day'
      and not detail ? 'saved_after_s';
    return found;
end;
$$;
revoke all on function public.ai_plan_saved(text, int) from public, anon;
grant execute on function public.ai_plan_saved(text, int) to authenticated;

-- Newest first: [{id, created_at, result, detail}, ...]
create or replace function public.admin_ai_plans(p_user_id uuid, p_limit int default 20) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select coalesce(jsonb_agg(jsonb_build_object('id', l.id, 'created_at', l.created_at, 'result', l.result,
                                                 'detail', l.detail) order by l.id desc), '[]'::jsonb)
    from (select * from private.audit_log
          where actor_id = p_user_id and action = 'ai.plan'
          order by id desc
          limit greatest(1, least(coalesce(p_limit, 20), 100))) l
$$;
revoke all on function public.admin_ai_plans(uuid, int) from public, anon, authenticated;
grant execute on function public.admin_ai_plans(uuid, int) to service_role;

notify pgrst, 'reload schema';
