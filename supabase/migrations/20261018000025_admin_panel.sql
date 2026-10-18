-- =====================================================================================================
-- TravelPlanner - TP admin / 25: functions for the admin web panel.
--
-- The panel (private repository TravelPlanner.Admin) is a server-side ASP.NET Core app that calls these with the
-- service role key (never in a browser, never in the app). Every admin_* function below is executable by service_role
-- ONLY - anon and signed-in users get "permission denied". They wrap what was SQL-Editor-only so far:
--
--   admin_overview()                       counts for the start page
--   admin_explore_list()                   every sample trip, whatever its status
--   admin_explore_get(slug)                one sample trip in the private.explore_upsert JSON format
--   admin_explore_upsert(json)             = private.explore_upsert (migration 23)
--   admin_explore_set_status(slug, s)      = private.explore_set_status (migration 23)
--   admin_users(query, limit)              accounts by e-mail / name, with today's AI use
--   admin_user(id)                         one account, its AI limit and the last 14 days of AI use
--   admin_set_ai_limit(id, limit|null)     a daily AI plan limit of its own for one account (null = default)
--   admin_reset_ai_today(id)               today's AI plans of the account (and its devices) start again
--   admin_trips(query, owner, limit)       users' trips (read only), admin_trip(id) one trip with all its rows
--
-- private.ai_user_limits holds the per-account limits; private.ai_limit_for (migration 12) now reads it first,
-- so ai_plan_quota / ai_plan_quota_status use it without any change. The global daily ceiling still applies.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- AI limits
create table if not exists private.ai_user_limits (
    user_id     uuid primary key references auth.users (id) on delete cascade,
    daily_limit int not null check (daily_limit between 0 and 100),
    updated_at  timestamptz not null default now()
);
alter table private.ai_user_limits enable row level security;
revoke all on table private.ai_user_limits from public, anon, authenticated;

-- Today's plan limit of an account: its own limit when the admin set one, else lower in its first 24 hours.
create or replace function private.ai_limit_for(p_uid uuid) returns int
language sql
stable
security definer
set search_path = ''
as $$
    select coalesce(
               l.daily_limit,
               case
                   when u.created_at is not null and u.created_at > now() - interval '24 hours'
                       then least(private.ai_new_account_limit(), private.ai_daily_limit())
                   else private.ai_daily_limit()
               end)
      from (select 1) one
      left join auth.users u on u.id = p_uid
      left join private.ai_user_limits l on l.user_id = p_uid
$$;
revoke all on function private.ai_limit_for(uuid) from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- overview
create or replace function public.admin_overview() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'users',            (select count(*) from auth.users),
        'users_7d',         (select count(*) from auth.users where created_at > now() - interval '7 days'),
        'trips',            (select count(*) from public.trips where deleted_at is null),
        'ai_today',         coalesce((select count from private.ai_global_usage where day = current_date), 0),
        'ai_global_limit',  private.ai_global_daily_limit(),
        'ai_daily_limit',   private.ai_daily_limit(),
        'explore_published', (select count(*) from public.explore_trips where status = 'published'),
        'explore_draft',    (select count(*) from public.explore_trips where status = 'draft'),
        'explore_archived', (select count(*) from public.explore_trips where status = 'archived'))
$$;

-- ------------------------------------------------------------------------------------------- Keşfet
create or replace function public.admin_explore_list() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select coalesce(jsonb_agg(jsonb_build_object(
               'id', t.id, 'slug', t.slug, 'title', t.title, 'country', t.country, 'city', t.city,
               'duration_days', t.duration_days, 'status', t.status, 'is_featured', t.is_featured,
               'sort_rank', t.sort_rank, 'thumb_path', t.thumb_path, 'publish_from', t.publish_from,
               'publish_until', t.publish_until, 'updated_at', t.updated_at, 'content_version', t.content_version)
           order by case t.status when 'published' then 0 when 'draft' then 1 else 2 end, t.sort_rank, t.title), '[]'::jsonb)
    from public.explore_trips t
$$;

-- The whole sample trip in the format private.explore_upsert takes (docs/EXPLORE_CONTENT.md), so the panel can
-- edit it and send it back. Place keys are made up (p1, p2 ...); null when there is no such slug.
create or replace function public.admin_explore_get(p_slug text) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    with t as (
        select * from public.explore_trips where slug = p_slug
    ), pl as (
        select p.*, 'p' || row_number() over (order by p.sort_order, p.id) as key
        from public.explore_trip_places p join t on p.explore_trip_id = t.id
    )
    select jsonb_strip_nulls(jsonb_build_object(
               'slug', t.slug, 'title', t.title, 'summary', t.summary, 'description', t.description,
               'country', t.country, 'city', t.city, 'duration_days', t.duration_days, 'budget_level', t.budget_level,
               'est_budget_minor', t.est_budget_minor, 'currency_code', t.currency_code, 'tags', to_jsonb(t.tags),
               'months', to_jsonb(t.months), 'tips', to_jsonb(t.tips), 'cover_path', t.cover_path,
               'thumb_path', t.thumb_path, 'image_credit', t.image_credit, 'lang', t.lang, 'sort_rank', t.sort_rank,
               'is_featured', t.is_featured, 'status', t.status, 'publish_from', t.publish_from,
               'publish_until', t.publish_until))
           || jsonb_build_object(
               'places', coalesce((
                   select jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
                              'key', pl.key, 'name', pl.name, 'description', pl.description,
                              'lat', pl.latitude, 'lon', pl.longitude)) order by pl.sort_order, pl.id)
                   from pl), '[]'::jsonb),
               'days', coalesce((
                   select jsonb_agg(jsonb_build_object('day', d.day_index, 'activities', d.acts) order by d.day_index)
                   from (select a.day_index,
                                jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
                                    'start', to_char(a.start_time, 'HH24:MI'), 'end', to_char(a.end_time, 'HH24:MI'),
                                    'title', a.title, 'note', a.note, 'place', pl.key)) order by a.sort_order) as acts
                         from public.explore_trip_activities a
                         join t on a.explore_trip_id = t.id
                         left join pl on pl.id = a.place_id
                         group by a.day_index) d), '[]'::jsonb),
               'todos', coalesce((
                   select jsonb_agg(jsonb_build_object('title', d.title, 'kind', d.kind) order by d.sort_order)
                   from public.explore_trip_todos d join t on d.explore_trip_id = t.id), '[]'::jsonb))
    from t
$$;

create or replace function public.admin_explore_upsert(p jsonb) returns uuid
language sql
security definer
set search_path = ''
as $$
    select private.explore_upsert(p)
$$;

create or replace function public.admin_explore_set_status(p_slug text, p_status text) returns void
language sql
security definer
set search_path = ''
as $$
    select private.explore_set_status(p_slug, p_status)
$$;

-- ------------------------------------------------------------------------------------------- accounts
-- One account as the panel shows it.
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id),
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

-- Accounts whose e-mail or name contains p_query (case-insensitive), newest first; all accounts without a query.
create or replace function public.admin_users(p_query text default null, p_limit int default 50) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_query   text := nullif(btrim(coalesce(p_query, '')), '');
    v_pattern text;
begin
    if p_limit is null or p_limit not between 1 and 200 then
        raise exception 'p_limit must be between 1 and 200' using errcode = '22023';
    end if;
    if v_query is not null then
        if char_length(v_query) > 100 then
            raise exception 'p_query is too long' using errcode = '22023';
        end if;
        v_pattern := '%' || replace(replace(replace(v_query, '\', '\\'), '%', '\%'), '_', '\_') || '%';
    end if;

    return coalesce((
        select jsonb_agg(private.admin_user_row(x.id) order by x.created_at desc nulls last)
        from (select u.id, u.created_at
              from auth.users u
              left join public.profiles p on p.id = u.id
              where v_pattern is null or u.email ilike v_pattern or p.display_name ilike v_pattern
              order by u.created_at desc nulls last
              limit p_limit) x), '[]'::jsonb);
end;
$$;

-- One account and its AI plans of the last 14 days; null when there is no such account.
create or replace function public.admin_user(p_user_id uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select private.admin_user_row(p_user_id)
           || jsonb_build_object('ai_history', coalesce((
                  select jsonb_agg(jsonb_build_object('day', a.day, 'count', a.count) order by a.day desc)
                  from private.ai_usage a
                  where a.user_id = p_user_id and a.day > current_date - 14), '[]'::jsonb))
    where exists (select 1 from auth.users where id = p_user_id)
$$;

-- Gives the account a daily AI plan limit of its own (0-100), or back the default with null.
create or replace function public.admin_set_ai_limit(p_user_id uuid, p_daily_limit int) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    if p_daily_limit is null then
        delete from private.ai_user_limits where user_id = p_user_id;
        return;
    end if;
    if p_daily_limit not between 0 and 100 then
        raise exception 'the daily limit must be between 0 and 100' using errcode = '22023';
    end if;
    insert into private.ai_user_limits (user_id, daily_limit) values (p_user_id, p_daily_limit)
    on conflict (user_id) do update set daily_limit = excluded.daily_limit, updated_at = now();
end;
$$;

-- Today's AI plans of the account start again - and of the devices it planned on today, since the device count
-- is checked against the same limit. The global ceiling is left as it is.
create or replace function public.admin_reset_ai_today(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    -- same lock order as ai_plan_quota: account, then device
    update private.ai_usage set count = 0 where user_id = p_user_id and day = current_date;
    update private.ai_device_usage d set count = 0
     where d.day = current_date
       and d.device in (select t.device from private.ai_quota_ticket t where t.user_id = p_user_id and t.day = current_date);
end;
$$;

-- ------------------------------------------------------------------------------------------- trips (read only)
-- Live trips, newest first: of one owner (p_owner) and/or whose name, city, country or owner e-mail contains p_query.
create or replace function public.admin_trips(p_query text default null, p_owner uuid default null, p_limit int default 50)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_query   text := nullif(btrim(coalesce(p_query, '')), '');
    v_pattern text;
begin
    if p_limit is null or p_limit not between 1 and 200 then
        raise exception 'p_limit must be between 1 and 200' using errcode = '22023';
    end if;
    if v_query is not null then
        if char_length(v_query) > 100 then
            raise exception 'p_query is too long' using errcode = '22023';
        end if;
        v_pattern := '%' || replace(replace(replace(v_query, '\', '\\'), '%', '\%'), '_', '\_') || '%';
    end if;

    return coalesce((
        select jsonb_agg(jsonb_build_object(
                   'id', t.id, 'name', t.name, 'country', t.country, 'city', t.city,
                   'start_date', t.start_date, 'end_date', t.end_date, 'currency_code', t.currency_code,
                   'owner_id', t.owner_id, 'owner_email', u.email, 'updated_at', t.updated_at,
                   'members', (select count(*) from public.trip_members m where m.trip_id = t.id))
               order by t.created_at desc)
        from (select t.*
              from public.trips t
              left join auth.users u on u.id = t.owner_id
              where t.deleted_at is null
                and (p_owner is null or t.owner_id = p_owner)
                and (v_pattern is null or t.name ilike v_pattern or t.city ilike v_pattern
                     or t.country ilike v_pattern or u.email ilike v_pattern)
              order by t.created_at desc
              limit p_limit) t
        left join auth.users u on u.id = t.owner_id), '[]'::jsonb);
end;
$$;

-- One trip with everything in it (live rows only); null when there is no such trip.
create or replace function public.admin_trip(p_id uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select to_jsonb(t) - 'change_seq' - 'version' - 'created_by' - 'updated_by'
        || jsonb_build_object(
            'owner_email', (select u.email from auth.users u where u.id = t.owner_id),
            'members', coalesce((
                select jsonb_agg(jsonb_build_object('user_id', m.user_id, 'email', u.email, 'role', m.role,
                                                    'joined_at', m.joined_at) order by m.joined_at)
                from public.trip_members m left join auth.users u on u.id = m.user_id
                where m.trip_id = t.id), '[]'::jsonb),
            'participants', coalesce((
                select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name) order by p.name)
                from public.participants p where p.trip_id = t.id and p.deleted_at is null), '[]'::jsonb),
            'expenses', coalesce((
                select jsonb_agg(jsonb_build_object(
                           'date', e.date, 'description', e.description, 'category', e.category,
                           'amount_minor', e.amount_minor, 'amount_currency_code', coalesce(e.amount_currency_code, t.currency_code),
                           'original_amount_minor', e.original_amount_minor,
                           'original_currency_code', e.original_currency_code,
                           'paid_by', (select p.name from public.participants p where p.id = e.paid_by_participant_id))
                       order by e.date, e.created_at)
                from public.expenses e where e.trip_id = t.id and e.deleted_at is null), '[]'::jsonb),
            'places', coalesce((
                select jsonb_agg(jsonb_build_object('name', p.name, 'description', p.description, 'latitude', p.latitude,
                                                    'longitude', p.longitude, 'is_visited', p.is_visited) order by p.name)
                from public.places p where p.trip_id = t.id and p.deleted_at is null), '[]'::jsonb),
            'activities', coalesce((
                select jsonb_agg(jsonb_build_object('date', a.date, 'start_time', to_char(a.start_time, 'HH24:MI'),
                                                    'end_time', to_char(a.end_time, 'HH24:MI'), 'title', a.title, 'note', a.note,
                                                    'place', (select p.name from public.places p where p.id = a.place_id))
                       order by a.date, a.sort_order, a.start_time nulls last)
                from public.activities a where a.trip_id = t.id and a.deleted_at is null), '[]'::jsonb),
            'todos', coalesce((
                select jsonb_agg(jsonb_build_object('title', d.title, 'is_completed', d.is_completed) order by d.created_at)
                from public.todos d where d.trip_id = t.id and d.deleted_at is null), '[]'::jsonb),
            'attachments', coalesce((
                select jsonb_agg(jsonb_build_object('display_name', f.display_name, 'content_type', f.content_type,
                                                    'size_bytes', f.size_bytes, 'created_at', f.created_at) order by f.created_at)
                from public.attachments f where f.trip_id = t.id and f.deleted_at is null), '[]'::jsonb))
    from public.trips t
    where t.id = p_id
$$;

-- ------------------------------------------------------------------------------------------- grants
-- service_role only (Supabase grants new public functions to anon and authenticated by default).
do $$
declare f text;
begin
    foreach f in array array[
        'public.admin_overview()', 'public.admin_explore_list()', 'public.admin_explore_get(text)',
        'public.admin_explore_upsert(jsonb)', 'public.admin_explore_set_status(text, text)',
        'public.admin_users(text, int)', 'public.admin_user(uuid)', 'public.admin_set_ai_limit(uuid, int)',
        'public.admin_reset_ai_today(uuid)', 'public.admin_trips(text, uuid, int)', 'public.admin_trip(uuid)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;
