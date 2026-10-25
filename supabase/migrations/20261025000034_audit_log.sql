-- =====================================================================================================
-- TravelPlanner - log / 34: one shared audit log (TP-52).
--
--   private.audit_log                 one row per action: when, who (admin / user / system, id + e-mail as plain
--                                     values), the action code, its target, details (jsonb, e.g. old/new values),
--                                     the device and address it came from, and whether it went through.
--   private.audit_write(...)          writes a row from SQL (later: account deletion, AI plans - TP-53, TP-54)
--   public.audit_log_write(json)      the same for the admin panel and Edge Functions (service_role only)
--   public.admin_audit_log(...)       newest first, with filters and paging, for the panel's log screen (TP-56)
--   private.purge_audit_log()         drops rows older than one year; pg_cron runs it every night at 03:27
--
-- No foreign key to auth.users: deleting an account keeps its log rows (the e-mail stays as text).
-- Not in the API's schemas, RLS on and no grants: only the service key (through the functions above) reads it.
-- Action codes: "<area>.<verb>", lower case, e.g. user.ban, settings.update, explore.delete, admin.sign_in.
-- Safe to run again.
-- =====================================================================================================

create schema if not exists private;

create table if not exists private.audit_log (
    id           bigint generated always as identity primary key,
    created_at   timestamptz not null default now(),
    actor_type   text not null check (actor_type in ('admin', 'user', 'system')),
    actor_id     uuid,
    actor_email  text check (char_length(actor_email) <= 320),
    action       text not null check (char_length(action) <= 64 and action ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'),
    result       text not null default 'ok' check (result in ('ok', 'error')),
    target_type  text check (char_length(target_type) <= 32 and target_type ~ '^[a-z][a-z0-9_]*$'),
    target_id    text check (char_length(target_id) <= 200),
    target_label text check (char_length(target_label) <= 300),
    detail       jsonb not null default '{}'::jsonb check (jsonb_typeof(detail) = 'object' and octet_length(detail::text) <= 16384),
    device       text check (char_length(device) <= 300),
    ip           text check (char_length(ip) <= 64)
);
alter table private.audit_log enable row level security;
revoke all on table private.audit_log from public, anon, authenticated;

create index if not exists audit_log_created_at_idx on private.audit_log (created_at);
create index if not exists audit_log_target_idx on private.audit_log (target_type, target_id, id desc);
create index if not exists audit_log_actor_idx on private.audit_log (actor_email, id desc);
create index if not exists audit_log_action_idx on private.audit_log (action, id desc);

-- ------------------------------------------------------------------------------------------- writing
-- Writes one row and returns its id. Long texts are cut instead of refused, so an action is never left unlogged
-- because of its size; a detail over 16 KB is replaced by {"truncated": true, "bytes": n}. The e-mail is taken from
-- the account when only its id is given. A bad action code or actor type is refused (22023).
create or replace function private.audit_write(
    p_actor_type   text,
    p_action       text,
    p_actor_id     uuid  default null,
    p_actor_email  text  default null,
    p_target_type  text  default null,
    p_target_id    text  default null,
    p_target_label text  default null,
    p_detail       jsonb default null,
    p_result       text  default 'ok',
    p_device       text  default null,
    p_ip           text  default null
) returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_action text := lower(btrim(coalesce(p_action, '')));
    v_actor  text := lower(btrim(coalesce(p_actor_type, '')));
    v_type   text := nullif(lower(btrim(coalesce(p_target_type, ''))), '');
    v_email  text := nullif(lower(btrim(coalesce(p_actor_email, ''))), '');
    v_detail jsonb := coalesce(p_detail, '{}'::jsonb);
    v_id     bigint;
begin
    if v_actor not in ('admin', 'user', 'system') then
        raise exception 'audit: unknown actor type "%"', p_actor_type using errcode = '22023';
    end if;
    if char_length(v_action) > 64 or v_action !~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$' then
        raise exception 'audit: bad action code "%"', p_action using errcode = '22023';
    end if;
    if v_type is not null and (char_length(v_type) > 32 or v_type !~ '^[a-z][a-z0-9_]*$') then
        raise exception 'audit: bad target type "%"', p_target_type using errcode = '22023';
    end if;
    if jsonb_typeof(v_detail) <> 'object' then
        v_detail := jsonb_build_object('value', v_detail);
    end if;
    if octet_length(v_detail::text) > 16384 then
        v_detail := jsonb_build_object('truncated', true, 'bytes', octet_length(v_detail::text));
    end if;
    if v_email is null and p_actor_id is not null then
        select lower(u.email) into v_email from auth.users u where u.id = p_actor_id;
    end if;

    insert into private.audit_log (actor_type, actor_id, actor_email, action, result, target_type, target_id,
                                   target_label, detail, device, ip)
    values (v_actor, p_actor_id, left(v_email, 320), v_action,
            case when lower(btrim(coalesce(p_result, 'ok'))) = 'error' then 'error' else 'ok' end,
            v_type,
            left(nullif(btrim(coalesce(p_target_id, '')), ''), 200),
            left(nullif(btrim(coalesce(p_target_label, '')), ''), 300),
            v_detail,
            left(nullif(btrim(coalesce(p_device, '')), ''), 300),
            left(nullif(btrim(coalesce(p_ip, '')), ''), 64))
    returning id into v_id;
    return v_id;
end;
$$;
revoke all on function private.audit_write(text, text, uuid, text, text, text, text, jsonb, text, text, text)
    from public, anon, authenticated;

-- The panel / Edge Function door: p = {"actor_type": "admin" (default), "actor_id", "actor_email", "action",
-- "result": "ok" | "error", "target_type", "target_id", "target_label", "detail": {...}, "device", "ip"}.
-- An actor_id that is not a uuid is dropped. Returns the row id.
create or replace function public.audit_log_write(p jsonb) returns bigint
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_actor_id uuid;
begin
    if p is null or jsonb_typeof(p) <> 'object' then
        raise exception 'audit: a JSON object is needed' using errcode = '22023';
    end if;
    if p ->> 'actor_id' ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
        v_actor_id := (p ->> 'actor_id')::uuid;
    end if;
    return private.audit_write(
        p_actor_type   => coalesce(p ->> 'actor_type', 'admin'),
        p_action       => p ->> 'action',
        p_actor_id     => v_actor_id,
        p_actor_email  => p ->> 'actor_email',
        p_target_type  => p ->> 'target_type',
        p_target_id    => p ->> 'target_id',
        p_target_label => p ->> 'target_label',
        p_detail       => case when jsonb_typeof(p -> 'detail') = 'object' then p -> 'detail' end,
        p_result       => coalesce(p ->> 'result', 'ok'),
        p_device       => p ->> 'device',
        p_ip           => p ->> 'ip');
end;
$$;

-- ------------------------------------------------------------------------------------------- reading
-- Newest first. p_query: part of the actor's e-mail, the target's id or label; p_action: a code ("user.ban") or an
-- area ("user"); p_target_type / p_target_id: exact; p_before_id: the "next_before_id" of the previous page.
-- { "items": [{id, created_at, actor_type, actor_id, actor_email, action, result, target_type, target_id,
--              target_label, detail, device, ip}, ...], "next_before_id": 123 | null }
create or replace function public.admin_audit_log(
    p_query       text   default null,
    p_action      text   default null,
    p_target_type text   default null,
    p_target_id   text   default null,
    p_before_id   bigint default null,
    p_limit       int    default 50
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
               greatest(1, least(coalesce(p_limit, 50), 200))               as n
    ), page as (
        select l.*
        from private.audit_log l, args a
        where (p_before_id is null or l.id < p_before_id)
          and (a.action is null or l.action = a.action or l.action like a.action || '.%')
          and (a.target_type is null or l.target_type = a.target_type)
          and (a.target_id is null or l.target_id = a.target_id)
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

do $$
declare f text;
begin
    foreach f in array array['public.audit_log_write(jsonb)',
                             'public.admin_audit_log(text, text, text, text, bigint, int)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

-- ------------------------------------------------------------------------------------------- retention
-- Rows are kept for one year (docs/COMPLIANCE.md). Returns how many were dropped.
create or replace function private.purge_audit_log() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_count int;
begin
    delete from private.audit_log where created_at < now() - interval '1 year';
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;
revoke all on function private.purge_audit_log() from public, anon, authenticated;

-- Every night at 03:27, between the trash purge (03:17, migration 6) and the file clean-up (03:47, migration 16).
-- Needs pg_cron (Database -> Extensions); without it the purge is not scheduled and this says so.
do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-audit-log';
        perform cron.schedule('travelplanner-purge-audit-log', '27 3 * * *', 'select private.purge_audit_log()');
    else
        raise notice 'pg_cron missing: old audit log rows are not purged. Enable it in Database -> Extensions and re-run this file.';
    end if;
end $$;

notify pgrst, 'reload schema';
