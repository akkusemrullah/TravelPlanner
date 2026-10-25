-- =====================================================================================================
-- TravelPlanner - TP-63 / 40: the AI quota day follows Türkiye's clock.
--
-- The daily AI counters (per account, per device, global) are kept by current_date, which is the date in the
-- session's time zone - UTC on Supabase. So "her gün yenilenir" happened at 03:00 in Türkiye. Every function that
-- reads or writes those counters now runs with TimeZone = Europe/Istanbul, so current_date inside it is Türkiye's
-- date and the quota renews at midnight there. now() and every timestamptz stay the same instant; only the
-- calendar day changes. Rows already written keep their (UTC) day; between 00:00 and 03:00 on the day this runs
-- the new day simply starts a little earlier.
--
-- NOTE for later migrations: CREATE OR REPLACE of one of these functions drops this setting - add
--   set timezone = 'Europe/Istanbul'   to the new definition (or re-run this file).
-- Safe to run again.
-- =====================================================================================================

do $$
declare f text;
begin
    foreach f in array array[
        'public.ai_plan_quota_status(text)',
        'public.ai_plan_quota(text)',
        'public.admin_overview()',
        'private.admin_user_row(uuid)',
        'public.admin_user(uuid)',
        'public.admin_reset_ai_today(uuid)',
        'private.ai_note_device(uuid,text)',
        'public.admin_settings()',
        'public.admin_ai_usage_daily(integer)'
    ] loop
        if to_regprocedure(f) is not null then
            execute format('alter function %s set timezone = %L', f, 'Europe/Istanbul');
        else
            raise notice 'TP-63: % not found (older migrations not run?), skipped', f;
        end if;
    end loop;
end $$;
