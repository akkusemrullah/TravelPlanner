-- =====================================================================================================
-- TravelPlanner - TP-33 / 23: "Keşfet" - a read-only catalog of sample trips prepared by the admin.
--
-- Four tables (explore_trips + its places, day plan and to-dos) that are NOT part of the sync system: no
-- apply_changes / pull_changes, no sync bookkeeping columns. Everyone (anon included, the publishable key is
-- enough) may read the rows that are published and inside their publish window; nobody may write through the
-- API - content is managed from the SQL Editor with private.explore_upsert / private.explore_set_status
-- (docs/EXPLORE_CONTENT.md). A user who adds a sample trip gets an independent copy in their own trips, so
-- later changes to (or archiving of) the sample never touch it.
--
--   public.explore_list(...)   cards only, keyset (cursor) pages of at most 30, search + filters + sort
--   public.explore_detail(id)  the whole sample trip as one JSON (places, day plan, to-dos)
--   Storage: public bucket "explore-images" (covers/<slug>.webp, thumbs/<slug>.webp), written only by the admin.
--
-- Safe to run again.
-- =====================================================================================================

create schema if not exists extensions;
create extension if not exists unaccent with schema extensions;
create extension if not exists pg_trgm with schema extensions;

-- ------------------------------------------------------------------------------------------- folding
-- Search text in one form: lower case, Turkish letters folded (İ/I/ı → i, ş → s, ğ → g, ü → u, ö → o, ç → c),
-- other accents removed ("Café" → "cafe"). "istanbul", "İSTANBUL" and "Istanbul" all become "istanbul".
-- Built with the schema unaccent really lives in (a project may have enabled it in "public" earlier), and with the
-- dictionary named explicitly - the one-argument unaccent() looks it up via search_path, which is empty here.
do $$
declare v_schema text;
begin
    select n.nspname into v_schema
    from pg_extension e join pg_namespace n on n.oid = e.extnamespace
    where e.extname = 'unaccent';

    execute format($f$
        create or replace function private.explore_fold(p_text text) returns text
        language sql
        stable
        parallel safe
        set search_path = ''
        as $body$
            select regexp_replace(
                lower(%1$I.unaccent(%2$L::regdictionary,
                      translate(coalesce(p_text, ''), 'İIıŞşĞğÜüÖöÇçÂâÎîÛû', 'iiissgguuooccaaiiuu'))),
                '\s+', ' ', 'g');
        $body$
    $f$, v_schema, v_schema || '.unaccent');
end $$;

revoke all on function private.explore_fold(text) from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- tables
create table if not exists public.explore_trips (
    id               uuid primary key default gen_random_uuid(),
    slug             text not null unique check (slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$' and char_length(slug) <= 80),
    title            text not null check (char_length(title) between 1 and 100),
    summary          text check (char_length(summary) <= 500),
    description      text check (char_length(description) <= 2000),
    country          text not null check (char_length(country) between 1 and 100),
    city             text not null check (char_length(city) between 1 and 100),
    duration_days    smallint not null check (duration_days between 1 and 90),
    budget_level     smallint not null default 2 check (budget_level between 1 and 3),
    est_budget_minor bigint check (est_budget_minor between 0 and 999999999999),
    currency_code    text not null default 'TRY' check (currency_code ~ '^[A-Z]{3}$'),
    tags             text[] not null default '{}'
                     check (cardinality(tags) <= 10
                            and tags <@ array['culture', 'nature', 'sea', 'food', 'family', 'romantic', 'adventure']),
    months           smallint[] not null default '{}'
                     check (months <@ array[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]::smallint[]),
    tips             text[] not null default '{}' check (cardinality(tips) <= 10),
    cover_path       text check (cover_path ~ '^covers/[a-z0-9-]+\.(webp|jpg)$'),
    thumb_path       text check (thumb_path ~ '^thumbs/[a-z0-9-]+\.(webp|jpg)$'),
    image_credit     text check (char_length(image_credit) <= 200),
    lang             text not null default 'tr' check (lang in ('tr', 'en')),
    sort_rank        integer not null default 1000 check (sort_rank between 0 and 1000000),
    is_featured      boolean not null default false,
    status           text not null default 'draft' check (status in ('draft', 'published', 'archived')),
    publish_from     timestamptz,
    publish_until    timestamptz,
    published_at     timestamptz,
    created_at       timestamptz not null default now(),
    updated_at       timestamptz not null default now(),
    content_version  integer not null default 1,
    search_text      text not null default '',
    check (publish_until is null or publish_from is null or publish_until > publish_from),
    check (status <> 'published' or published_at is not null)
);

create table if not exists public.explore_trip_places (
    id              uuid primary key default gen_random_uuid(),
    explore_trip_id uuid not null references public.explore_trips (id) on delete cascade,
    name            text not null check (char_length(name) between 1 and 150),
    description     text check (char_length(description) <= 500),
    latitude        double precision not null check (latitude between -90 and 90),
    longitude       double precision not null check (longitude between -180 and 180),
    sort_order      integer not null default 0
);

create table if not exists public.explore_trip_activities (
    id              uuid primary key default gen_random_uuid(),
    explore_trip_id uuid not null references public.explore_trips (id) on delete cascade,
    -- 1 = the trip's first day; the copy's date is start date + (day_index - 1)
    day_index       smallint not null check (day_index between 1 and 90),
    start_time      time,
    end_time        time,
    title           text not null check (char_length(title) between 1 and 150),
    note            text check (char_length(note) <= 500),
    place_id        uuid references public.explore_trip_places (id) on delete set null,
    sort_order      integer not null default 0,
    check (end_time is null or (start_time is not null and end_time > start_time))
);

create table if not exists public.explore_trip_todos (
    id              uuid primary key default gen_random_uuid(),
    explore_trip_id uuid not null references public.explore_trips (id) on delete cascade,
    title           text not null check (char_length(title) between 1 and 150),
    kind            text not null default 'todo' check (kind in ('todo', 'packing')),
    sort_order      integer not null default 0
);

-- ------------------------------------------------------------------------------------------- indexes
-- keyset pages: one index per sort order (see explore_list)
create index if not exists explore_trips_recommended_idx on public.explore_trips (sort_rank, published_at desc, id);
create index if not exists explore_trips_newest_idx on public.explore_trips (published_at desc, id desc);
create index if not exists explore_trips_shortest_idx on public.explore_trips (duration_days, sort_rank, id);
-- filters
create index if not exists explore_trips_country_idx on public.explore_trips (country);
create index if not exists explore_trips_budget_idx on public.explore_trips (budget_level);
create index if not exists explore_trips_tags_idx on public.explore_trips using gin (tags);
create index if not exists explore_trips_months_idx on public.explore_trips using gin (months);
-- Turkish-insensitive search over title, city, country and tags
do $$
declare v_schema text;
begin
    select n.nspname into v_schema
    from pg_extension e join pg_namespace n on n.oid = e.extnamespace
    where e.extname = 'pg_trgm';
    execute format('create index if not exists explore_trips_search_idx on public.explore_trips using gin (search_text %I.gin_trgm_ops)',
                   v_schema);
end $$;
-- children by parent
create index if not exists explore_trip_places_trip_idx on public.explore_trip_places (explore_trip_id, sort_order);
create index if not exists explore_trip_activities_trip_idx on public.explore_trip_activities (explore_trip_id, day_index, sort_order);
create index if not exists explore_trip_todos_trip_idx on public.explore_trip_todos (explore_trip_id, sort_order);

-- search_text and updated_at are kept by the database, whoever writes the row
create or replace function private.explore_trips_before_write() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    new.search_text := private.explore_fold(concat_ws(' ', new.title, new.city, new.country, array_to_string(new.tags, ' ')));
    new.updated_at := now();
    if new.status = 'published' and new.published_at is null then
        new.published_at := now();
    end if;
    return new;
end;
$$;

drop trigger if exists explore_trips_before_write on public.explore_trips;
create trigger explore_trips_before_write before insert or update on public.explore_trips
    for each row execute function private.explore_trips_before_write();

-- ------------------------------------------------------------------------------------------- RLS
-- Visible = published and inside the publish window. Seasonal content opens and closes by itself (no cron).
-- Written out in every policy (no helper function): anon never needs the private schema.
alter table public.explore_trips enable row level security;
alter table public.explore_trip_places enable row level security;
alter table public.explore_trip_activities enable row level security;
alter table public.explore_trip_todos enable row level security;

drop policy if exists "explore_trips: published are public" on public.explore_trips;
create policy "explore_trips: published are public" on public.explore_trips
    for select to anon, authenticated
    using (status = 'published' and (publish_from is null or publish_from <= now())
           and (publish_until is null or publish_until > now()));

drop policy if exists "explore_trip_places: of published trips" on public.explore_trip_places;
create policy "explore_trip_places: of published trips" on public.explore_trip_places
    for select to anon, authenticated
    using (exists (select 1 from public.explore_trips t
                   where t.id = explore_trip_id and t.status = 'published'
                     and (t.publish_from is null or t.publish_from <= now())
                     and (t.publish_until is null or t.publish_until > now())));

drop policy if exists "explore_trip_activities: of published trips" on public.explore_trip_activities;
create policy "explore_trip_activities: of published trips" on public.explore_trip_activities
    for select to anon, authenticated
    using (exists (select 1 from public.explore_trips t
                   where t.id = explore_trip_id and t.status = 'published'
                     and (t.publish_from is null or t.publish_from <= now())
                     and (t.publish_until is null or t.publish_until > now())));

drop policy if exists "explore_trip_todos: of published trips" on public.explore_trip_todos;
create policy "explore_trip_todos: of published trips" on public.explore_trip_todos
    for select to anon, authenticated
    using (exists (select 1 from public.explore_trips t
                   where t.id = explore_trip_id and t.status = 'published'
                     and (t.publish_from is null or t.publish_from <= now())
                     and (t.publish_until is null or t.publish_until > now())));

-- read only: no insert/update/delete policy and no write grant for the API roles
revoke all on public.explore_trips, public.explore_trip_places, public.explore_trip_activities, public.explore_trip_todos
    from public, anon, authenticated;
grant select on public.explore_trips, public.explore_trip_places, public.explore_trip_activities, public.explore_trip_todos
    to anon, authenticated;

-- ------------------------------------------------------------------------------------------- explore_list
-- One page of cards. Only published trips inside their publish window (the same rule as RLS, checked here
-- too because the function runs as its owner - it folds text with private.explore_fold).
--   p_query          words searched in title / city / country / tags (Turkish- and case-insensitive), <= 100 chars
--   p_country        country OR city, folded equality ("turkiye" = "Türkiye")
--   p_min/max_days   1..90
--   p_budget_levels  any of 1..3
--   p_tags           any of the travel styles (culture, nature, sea, food, family, romantic, adventure)
--   p_month          1..12 - trips good in that month (a trip with no months is good all year)
--   p_sort           'recommended' (admin order, default) | 'newest' | 'shortest'
--   p_cursor         next_cursor of the previous page (opaque), null for the first page
--   p_limit          1..30 (default 20)
-- Returns {"items": [...cards...], "next_cursor": text|null}. Invalid input is refused (errcode 22023).
create or replace function public.explore_list(
    p_query         text default null,
    p_country       text default null,
    p_min_days      int default null,
    p_max_days      int default null,
    p_budget_levels int[] default null,
    p_tags          text[] default null,
    p_month         int default null,
    p_sort          text default 'recommended',
    p_cursor        text default null,
    p_limit         int default 20)
returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_limit    int := coalesce(p_limit, 20);
    v_sort     text := coalesce(nullif(p_sort, ''), 'recommended');
    v_query    text := nullif(btrim(coalesce(p_query, '')), '');
    v_patterns text[];
    v_country  text := nullif(private.explore_fold(btrim(coalesce(p_country, ''))), '');
    v_cursor   jsonb;
    v_keyset   text := 'true';
    v_order    text;
    v_rows     jsonb;
    v_count    int;
    v_last     jsonb;
    v_next     text;
begin
    -- ---- input checks ----
    if v_limit < 1 or v_limit > 30 then
        raise exception 'p_limit must be between 1 and 30' using errcode = '22023';
    end if;
    if v_query is not null and char_length(v_query) > 100 then
        raise exception 'p_query is too long' using errcode = '22023';
    end if;
    if p_country is not null and char_length(p_country) > 100 then
        raise exception 'p_country is too long' using errcode = '22023';
    end if;
    if (p_min_days is not null and p_min_days not between 1 and 90)
       or (p_max_days is not null and p_max_days not between 1 and 90)
       or (p_min_days is not null and p_max_days is not null and p_min_days > p_max_days) then
        raise exception 'invalid day range' using errcode = '22023';
    end if;
    if p_budget_levels is not null and not (p_budget_levels <@ array[1, 2, 3]) then
        raise exception 'invalid budget level' using errcode = '22023';
    end if;
    if p_tags is not null and (cardinality(p_tags) > 7
       or not (p_tags <@ array['culture', 'nature', 'sea', 'food', 'family', 'romantic', 'adventure'])) then
        raise exception 'invalid tag' using errcode = '22023';
    end if;
    if p_month is not null and p_month not between 1 and 12 then
        raise exception 'invalid month' using errcode = '22023';
    end if;
    if v_sort not in ('recommended', 'newest', 'shortest') then
        raise exception 'invalid sort' using errcode = '22023';
    end if;

    -- every word must appear (LIKE metacharacters are matched literally)
    if v_query is not null then
        select array_agg('%' || replace(replace(replace(w, '\', '\\'), '%', '\%'), '_', '\_') || '%')
          into v_patterns
          from unnest(string_to_array(private.explore_fold(v_query), ' ')) as w
         where w <> '';
    end if;

    -- ---- keyset: the cursor holds the sort keys of the previous page's last card ----
    if nullif(p_cursor, '') is not null then
        begin
            v_cursor := p_cursor::jsonb;
            if (v_cursor ->> 's') is distinct from v_sort or (v_cursor ->> 'i')::uuid is null then
                raise exception 'cursor';
            end if;
        exception when others then
            raise exception 'invalid cursor' using errcode = '22023';
        end;

        v_keyset := case v_sort
            when 'recommended' then
                '(t.sort_rank > $8 or (t.sort_rank = $8 and (t.published_at < $9 or (t.published_at = $9 and t.id > $10))))'
            when 'newest' then
                '(t.published_at < $9 or (t.published_at = $9 and t.id < $10))'
            else
                '((t.duration_days, t.sort_rank, t.id) > ($11, $8, $10))'
        end;
    end if;

    v_order := case v_sort
        when 'recommended' then 't.sort_rank, t.published_at desc, t.id'
        when 'newest' then 't.published_at desc, t.id desc'
        else 't.duration_days, t.sort_rank, t.id'
    end;

    -- one row more than asked: tells whether there is a next page
    execute format($q$
        select coalesce(jsonb_agg(c order by n), '[]'::jsonb), count(*)
        from (
            select row_number() over (order by %2$s) as n,
                   jsonb_build_object(
                       'id', t.id, 'slug', t.slug, 'title', t.title, 'country', t.country, 'city', t.city,
                       'duration_days', t.duration_days, 'budget_level', t.budget_level, 'tags', t.tags,
                       'thumb_path', t.thumb_path, 'is_featured', t.is_featured, 'published_at', t.published_at,
                       'sort_rank', t.sort_rank) as c
            from public.explore_trips t
            where %1$s
              and t.status = 'published'
              and (t.publish_from is null or t.publish_from <= now())
              and (t.publish_until is null or t.publish_until > now())
              and ($1::text[] is null or t.search_text like all ($1))
              and ($2::text is null or private.explore_fold(t.country) = $2 or private.explore_fold(t.city) = $2)
              and ($3::int is null or t.duration_days >= $3)
              and ($4::int is null or t.duration_days <= $4)
              and ($5::int[] is null or cardinality($5) = 0 or t.budget_level = any ($5))
              and ($6::text[] is null or cardinality($6) = 0 or t.tags && $6)
              and ($7::int is null or cardinality(t.months) = 0 or $7::smallint = any (t.months))
            order by %2$s
            limit $12
        ) page
    $q$, v_keyset, v_order)
    into v_rows, v_count
    using v_patterns, v_country, p_min_days, p_max_days, p_budget_levels, p_tags, p_month,
          (v_cursor ->> 'r')::int, (v_cursor ->> 'p')::timestamptz, (v_cursor ->> 'i')::uuid,
          (v_cursor ->> 'd')::int, v_limit + 1;

    if v_count > v_limit then
        v_rows := v_rows - v_limit;              -- drop the extra row
        v_last := v_rows -> (v_limit - 1);
        v_next := jsonb_build_object(
            's', v_sort,
            'r', (v_last ->> 'sort_rank')::int,
            'p', v_last ->> 'published_at',
            'd', (v_last ->> 'duration_days')::int,
            'i', v_last ->> 'id')::text;
    end if;

    return jsonb_build_object('items', v_rows, 'next_cursor', v_next);
end;
$$;

revoke all on function public.explore_list(text, text, int, int, int[], text[], int, text, text, int) from public;
grant execute on function public.explore_list(text, text, int, int, int[], text[], int, text, text, int) to anon, authenticated;

-- ------------------------------------------------------------------------------------------- explore_detail
-- The whole sample trip in one JSON, or null when it doesn't exist / isn't visible (same rule as RLS).
create or replace function public.explore_detail(p_id uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', t.id, 'slug', t.slug, 'title', t.title, 'summary', t.summary, 'description', t.description,
        'country', t.country, 'city', t.city, 'duration_days', t.duration_days, 'budget_level', t.budget_level,
        'est_budget_minor', t.est_budget_minor, 'currency_code', t.currency_code, 'tags', t.tags, 'months', t.months,
        'tips', t.tips, 'cover_path', t.cover_path, 'thumb_path', t.thumb_path, 'image_credit', t.image_credit,
        'is_featured', t.is_featured, 'published_at', t.published_at, 'content_version', t.content_version,
        'places', coalesce((
            select jsonb_agg(jsonb_build_object('id', p.id, 'name', p.name, 'description', p.description,
                                                'latitude', p.latitude, 'longitude', p.longitude)
                             order by p.sort_order, p.name)
            from public.explore_trip_places p where p.explore_trip_id = t.id), '[]'::jsonb),
        'activities', coalesce((
            select jsonb_agg(jsonb_build_object('id', a.id, 'day_index', a.day_index,
                                                'start_time', to_char(a.start_time, 'HH24:MI'),
                                                'end_time', to_char(a.end_time, 'HH24:MI'),
                                                'title', a.title, 'note', a.note, 'place_id', a.place_id)
                             order by a.day_index, a.sort_order, a.start_time nulls last)
            from public.explore_trip_activities a where a.explore_trip_id = t.id), '[]'::jsonb),
        'todos', coalesce((
            select jsonb_agg(jsonb_build_object('id', d.id, 'title', d.title, 'kind', d.kind) order by d.kind desc, d.sort_order)
            from public.explore_trip_todos d where d.explore_trip_id = t.id), '[]'::jsonb))
    from public.explore_trips t
    where t.id = p_id
      and t.status = 'published'
      and (t.publish_from is null or t.publish_from <= now())
      and (t.publish_until is null or t.publish_until > now());
$$;

revoke all on function public.explore_detail(uuid) from public;
grant execute on function public.explore_detail(uuid) to anon, authenticated;

-- ------------------------------------------------------------------------------------------- admin
-- Adds or replaces a sample trip from one JSON (format: docs/EXPLORE_CONTENT.md), matched by "slug". The day
-- plan, places and to-dos are replaced as a whole; content_version goes up on every update. A new trip is a
-- draft unless "status" says otherwise; an update keeps the status unless "status" is given.
-- Run from the SQL Editor:  select private.explore_upsert($json${ ... }$json$::jsonb);
create or replace function private.explore_upsert(p jsonb) returns uuid
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_id       uuid;
    v_days     int := (p ->> 'duration_days')::int;
    v_place    jsonb;
    v_day      jsonb;
    v_act      jsonb;
    v_todo     jsonb;
    v_keys     jsonb := '{}'::jsonb;
    v_new_id   uuid;
    v_order    int;
begin
    if p is null or jsonb_typeof(p) <> 'object' or coalesce(p ->> 'slug', '') = '' then
        raise exception 'explore_upsert: a JSON object with "slug" is required' using errcode = '22023';
    end if;
    if jsonb_array_length(coalesce(p -> 'places', '[]')) > 500
       or jsonb_array_length(coalesce(p -> 'todos', '[]')) > 500 then
        raise exception 'explore_upsert: at most 500 places / to-dos' using errcode = '22023';
    end if;

    select id into v_id from public.explore_trips where slug = p ->> 'slug' for update;

    if v_id is null then
        insert into public.explore_trips (
            slug, title, summary, description, country, city, duration_days, budget_level, est_budget_minor,
            currency_code, tags, months, tips, cover_path, thumb_path, image_credit, lang, sort_rank, is_featured,
            status, publish_from, publish_until)
        values (
            p ->> 'slug', p ->> 'title', p ->> 'summary', p ->> 'description', p ->> 'country', p ->> 'city', v_days,
            coalesce((p ->> 'budget_level')::smallint, 2), (p ->> 'est_budget_minor')::bigint,
            coalesce(p ->> 'currency_code', 'TRY'),
            coalesce((select array_agg(x) from jsonb_array_elements_text(p -> 'tags') x), '{}'),
            coalesce((select array_agg(x::smallint) from jsonb_array_elements_text(p -> 'months') x), '{}'),
            coalesce((select array_agg(x) from jsonb_array_elements_text(p -> 'tips') x), '{}'),
            p ->> 'cover_path', p ->> 'thumb_path', p ->> 'image_credit', coalesce(p ->> 'lang', 'tr'),
            coalesce((p ->> 'sort_rank')::int, 1000), coalesce((p ->> 'is_featured')::boolean, false),
            coalesce(p ->> 'status', 'draft'), (p ->> 'publish_from')::timestamptz, (p ->> 'publish_until')::timestamptz)
        returning id into v_id;
    else
        update public.explore_trips set
            title = p ->> 'title', summary = p ->> 'summary', description = p ->> 'description',
            country = p ->> 'country', city = p ->> 'city', duration_days = v_days,
            budget_level = coalesce((p ->> 'budget_level')::smallint, 2),
            est_budget_minor = (p ->> 'est_budget_minor')::bigint,
            currency_code = coalesce(p ->> 'currency_code', 'TRY'),
            tags = coalesce((select array_agg(x) from jsonb_array_elements_text(p -> 'tags') x), '{}'),
            months = coalesce((select array_agg(x::smallint) from jsonb_array_elements_text(p -> 'months') x), '{}'),
            tips = coalesce((select array_agg(x) from jsonb_array_elements_text(p -> 'tips') x), '{}'),
            cover_path = p ->> 'cover_path', thumb_path = p ->> 'thumb_path', image_credit = p ->> 'image_credit',
            lang = coalesce(p ->> 'lang', 'tr'), sort_rank = coalesce((p ->> 'sort_rank')::int, 1000),
            is_featured = coalesce((p ->> 'is_featured')::boolean, false),
            status = coalesce(p ->> 'status', status),
            publish_from = (p ->> 'publish_from')::timestamptz, publish_until = (p ->> 'publish_until')::timestamptz,
            content_version = content_version + 1
        where id = v_id;

        delete from public.explore_trip_activities where explore_trip_id = v_id;
        delete from public.explore_trip_todos where explore_trip_id = v_id;
        delete from public.explore_trip_places where explore_trip_id = v_id;
    end if;

    -- places first: the day plan points at them by "key"
    v_order := 0;
    for v_place in select * from jsonb_array_elements(coalesce(p -> 'places', '[]')) loop
        insert into public.explore_trip_places (explore_trip_id, name, description, latitude, longitude, sort_order)
        values (v_id, v_place ->> 'name', v_place ->> 'description',
                (v_place ->> 'lat')::double precision, (v_place ->> 'lon')::double precision, v_order)
        returning id into v_new_id;
        if coalesce(v_place ->> 'key', '') <> '' then
            v_keys := v_keys || jsonb_build_object(v_place ->> 'key', v_new_id);
        end if;
        v_order := v_order + 1;
    end loop;

    v_order := 0;
    for v_day in select * from jsonb_array_elements(coalesce(p -> 'days', '[]')) loop
        if (v_day ->> 'day')::int not between 1 and v_days then
            raise exception 'explore_upsert: day % is outside 1..%', v_day ->> 'day', v_days using errcode = '22023';
        end if;
        for v_act in select * from jsonb_array_elements(coalesce(v_day -> 'activities', '[]')) loop
            if coalesce(v_act ->> 'place', '') <> '' and not v_keys ? (v_act ->> 'place') then
                raise exception 'explore_upsert: unknown place key "%"', v_act ->> 'place' using errcode = '22023';
            end if;
            insert into public.explore_trip_activities (explore_trip_id, day_index, start_time, end_time, title, note, place_id, sort_order)
            values (v_id, (v_day ->> 'day')::smallint, (v_act ->> 'start')::time, (v_act ->> 'end')::time,
                    v_act ->> 'title', v_act ->> 'note', (v_keys ->> (v_act ->> 'place'))::uuid, v_order);
            v_order := v_order + 1;
        end loop;
    end loop;
    if v_order > 500 then
        raise exception 'explore_upsert: at most 500 activities' using errcode = '22023';
    end if;

    v_order := 0;
    for v_todo in select * from jsonb_array_elements(coalesce(p -> 'todos', '[]')) loop
        insert into public.explore_trip_todos (explore_trip_id, title, kind, sort_order)
        values (v_id, v_todo ->> 'title', coalesce(v_todo ->> 'kind', 'todo'), v_order);
        v_order := v_order + 1;
    end loop;

    return v_id;
end;
$$;

revoke all on function private.explore_upsert(jsonb) from public, anon, authenticated;

-- Publish / back to draft / archive (instead of deleting: users' copies are independent anyway).
create or replace function private.explore_set_status(p_slug text, p_status text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if p_status not in ('draft', 'published', 'archived') then
        raise exception 'explore_set_status: status must be draft, published or archived' using errcode = '22023';
    end if;
    update public.explore_trips set status = p_status where slug = p_slug;
    if not found then
        raise exception 'explore_set_status: no sample trip "%"', p_slug using errcode = 'P0002';
    end if;
end;
$$;

revoke all on function private.explore_set_status(text, text) from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- images
-- Public bucket: anyone reads covers/<slug>.webp and thumbs/<slug>.webp by URL; there is no insert / update /
-- delete policy, so only the admin (Dashboard -> Storage, service role) can upload.
insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('explore-images', 'explore-images', true, 1048576, array['image/webp', 'image/jpeg'])
on conflict (id) do update set public = true, file_size_limit = excluded.file_size_limit,
                               allowed_mime_types = excluded.allowed_mime_types;
