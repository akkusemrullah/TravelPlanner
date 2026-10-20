-- =====================================================================================================
-- TravelPlanner - admin / 31: the AI model is chosen in the admin panel.
--
-- Until now plan-trip read the model from the Edge Function secret AI_MODEL (default gpt-5-mini) and the
-- reasoning effort from AI_REASONING, so a change meant a trip to the Supabase dashboard. Both now live in
-- private.app_settings (migration 29) next to the AI limits:
--
--   ai_model       e.g. gpt-5-mini, gpt-5-nano, gpt-5; empty = the AI_MODEL secret, else gpt-5-mini
--   ai_reasoning   minimal | low | medium | high; empty = the AI_REASONING secret, else automatic
--                  (minimal for nano models, low for the others)
--
--   public.ai_model_settings()   what plan-trip reads with the service role key (not the app, not users)
--
-- admin_settings returns the new columns by itself (to_jsonb of the row); admin_set_settings takes them as two
-- more keys. Safe to run again.
-- =====================================================================================================

alter table private.app_settings add column if not exists ai_model text;
alter table private.app_settings add column if not exists ai_reasoning text;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'app_settings_ai_model_check'
                   and conrelid = 'private.app_settings'::regclass) then
        alter table private.app_settings add constraint app_settings_ai_model_check
            check (ai_model ~ '^[a-z0-9][a-z0-9._:-]{0,63}$');
    end if;
    if not exists (select 1 from pg_constraint where conname = 'app_settings_ai_reasoning_check'
                   and conrelid = 'private.app_settings'::regclass) then
        alter table private.app_settings add constraint app_settings_ai_reasoning_check
            check (ai_reasoning in ('minimal', 'low', 'medium', 'high'));
    end if;
end $$;

-- plan-trip asks this once per plan, with the service role key; null values = use the secrets / defaults.
create or replace function public.ai_model_settings() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object('model', s.ai_model, 'reasoning', s.ai_reasoning)
    from (select 1) one
    left join private.app_settings s on s.id
$$;

-- Same as migration 29, plus ai_model and ai_reasoning (lower-cased; empty = back to the default).
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
        ai_model              = case when p ? 'ai_model' then nullif(lower(btrim(p ->> 'ai_model')), '') else ai_model end,
        ai_reasoning          = case when p ? 'ai_reasoning' then nullif(lower(btrim(p ->> 'ai_reasoning')), '') else ai_reasoning end,
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
    foreach f in array array['public.ai_model_settings()', 'public.admin_set_settings(jsonb)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

notify pgrst, 'reload schema';
