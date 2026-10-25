-- =====================================================================================================
-- TravelPlanner - log / 38: the panel's log screen (TP-56).
--
-- public.admin_audit_log (migration 34) gets the screen's filters: a date range (p_from inclusive, p_to exclusive),
-- who did it (p_actor_type: admin / user / system), the result (ok / error) and one account (p_user_id: the rows it
-- did or that were done to it - "bu kullanıcının logları"). The old arguments stay as they were.
-- The old signature is dropped (PostgREST picks a function by its argument names). Safe to run again.
-- =====================================================================================================

drop function if exists public.admin_audit_log(text, text, text, text, bigint, int);

create or replace function public.admin_audit_log(
    p_query       text        default null,
    p_action      text        default null,
    p_target_type text        default null,
    p_target_id   text        default null,
    p_before_id   bigint      default null,
    p_limit       int         default 50,
    p_from        timestamptz default null,
    p_to          timestamptz default null,
    p_actor_type  text        default null,
    p_result      text        default null,
    p_user_id     uuid        default null
) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    with args as (
        select nullif(btrim(coalesce(p_query, '')), '')                    as q,
               nullif(lower(btrim(coalesce(p_action, ''))), '')            as action,
               nullif(lower(btrim(coalesce(p_target_type, ''))), '')       as target_type,
               nullif(btrim(coalesce(p_target_id, '')), '')                as target_id,
               nullif(lower(btrim(coalesce(p_actor_type, ''))), '')        as actor_type,
               nullif(lower(btrim(coalesce(p_result, ''))), '')            as result,
               greatest(1, least(coalesce(p_limit, 50), 200))               as n
    ), page as (
        select l.*
        from private.audit_log l, args a
        where (p_before_id is null or l.id < p_before_id)
          and (p_from is null or l.created_at >= p_from)
          and (p_to is null or l.created_at < p_to)
          and (a.action is null or l.action = a.action or l.action like a.action || '.%')
          and (a.target_type is null or l.target_type = a.target_type)
          and (a.target_id is null or l.target_id = a.target_id)
          and (a.actor_type is null or l.actor_type = a.actor_type)
          and (a.result is null or l.result = a.result)
          and (p_user_id is null or l.actor_id = p_user_id
               or (l.target_type = 'user' and l.target_id = p_user_id::text))
          and (a.q is null
               or strpos(lower(coalesce(l.actor_email, '')), lower(a.q)) > 0
               or strpos(lower(coalesce(l.target_label, '')), lower(a.q)) > 0
               or strpos(lower(coalesce(l.target_id, '')), lower(a.q)) > 0)
        order by l.id desc
        limit (select n + 1 from args)
    ), shown as (
        select * from page order by id desc limit (select n from args)
    )
    select jsonb_build_object(
        'items', coalesce((select jsonb_agg(to_jsonb(s) order by s.id desc) from shown s), '[]'::jsonb),
        'next_before_id', case when (select count(*) from page) > (select n from args)
                               then (select min(id) from shown) end)
$$;
revoke all on function public.admin_audit_log(text, text, text, text, bigint, int, timestamptz, timestamptz, text, text, uuid)
    from public, anon, authenticated;
grant execute on function public.admin_audit_log(text, text, text, text, bigint, int, timestamptz, timestamptz, text, text, uuid)
    to service_role;

notify pgrst, 'reload schema';
