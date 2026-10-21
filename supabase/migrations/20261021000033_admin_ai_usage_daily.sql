-- =====================================================================================================
-- TravelPlanner - admin / 33: daily AI plan counts for the panel's chart (TP-51).
--
--   public.admin_ai_usage_daily(days)   the last N days (1-366, today included), oldest first, days without a plan
--                                       as 0, plus the current global daily limit (the line on the chart):
--                                       { "days": [{"day": "2026-10-21", "count": 12}, ...], "limit": 200,
--                                         "total": 340, "max": 31 }
--
-- Source: private.ai_global_usage (migration 12), one row per day, refunds already taken off. The limit is today's
-- value from app_settings (migration 29); older limits are not kept. service_role only. Safe to run again.
-- =====================================================================================================

create or replace function public.admin_ai_usage_daily(p_days int default 30) returns jsonb
language sql stable security definer set search_path = '' as $$
    with span as (
        select greatest(1, least(coalesce(p_days, 30), 366)) as n
    ), days as (
        select d::date as day, coalesce(u.count, 0) as count
        from span,
             generate_series(current_date - (span.n - 1), current_date, interval '1 day') d
             left join private.ai_global_usage u on u.day = d::date
    )
    select jsonb_build_object(
        'days',  coalesce((select jsonb_agg(jsonb_build_object('day', to_char(day, 'YYYY-MM-DD'), 'count', count) order by day)
                           from days), '[]'::jsonb),
        'limit', private.ai_global_daily_limit(),
        'total', coalesce((select sum(count) from days), 0),
        'max',   coalesce((select max(count) from days), 0))
$$;

revoke all on function public.admin_ai_usage_daily(int) from public, anon, authenticated;
grant execute on function public.admin_ai_usage_daily(int) to service_role;

notify pgrst, 'reload schema';
