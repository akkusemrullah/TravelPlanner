-- TravelPlanner - G2: the whole Supabase schema in one file (generated from migrations/, same content).
-- Paste into Supabase Dashboard -> SQL Editor -> New query -> Run. Safe to run again.

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000001_core_schema.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 1: core schema
-- Tables mirror the app's SQLite models (one row = one local row, identified by its SyncId = id here).
-- Every synced table carries the same bookkeeping columns:
--   created_by / updated_by  auth uid of who created / last changed the row
--   created_at / updated_at  server time
--   version                  +1 on every write (the app's BaseVersion is compared with it)
--   change_seq               global, ever-increasing - the app pulls "everything after my last change_seq"
--   deleted_at               soft delete; purged for good after 30 days (see 000006_cron.sql)
-- Clients never write these tables directly: all writes go through public.apply_changes (000003).
-- =====================================================================================================

create schema if not exists private;

-- One sequence for every synced table, so a single cursor covers the whole database.
create sequence if not exists public.sync_change_seq;

-- ------------------------------------------------------------------------------------------- profiles
create table if not exists public.profiles (
    id           uuid primary key references auth.users (id) on delete cascade,
    email        text,
    display_name text check (char_length(display_name) <= 80),
    avatar_url   text,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);

-- ------------------------------------------------------------------------------------------- trips
create table if not exists public.trips (
    id                   uuid primary key,
    owner_id             uuid not null references auth.users (id) on delete cascade,
    name                 text not null check (char_length(name) <= 100),
    country              text not null default '' check (char_length(country) <= 100),
    city                 text not null default '' check (char_length(city) <= 100),
    start_date           date not null,
    end_date             date not null,
    planned_budget_minor bigint not null default 0 check (planned_budget_minor >= 0),
    description          text check (char_length(description) <= 500),
    currency_code        text check (char_length(currency_code) = 3),
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    constraint trips_dates_in_order check (end_date >= start_date)
);
create index if not exists trips_owner_idx on public.trips (owner_id);
create index if not exists trips_change_seq_idx on public.trips (change_seq);

-- ------------------------------------------------------------------------------------------- members / invites
create table if not exists public.trip_members (
    trip_id    uuid not null references public.trips (id) on delete cascade,
    user_id    uuid not null references auth.users (id) on delete cascade,
    role       text not null check (role in ('owner', 'editor', 'viewer')),
    invited_by uuid references auth.users (id) on delete set null,
    joined_at  timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    change_seq bigint not null default 0,
    primary key (trip_id, user_id)
);
create index if not exists trip_members_user_idx on public.trip_members (user_id);
-- Exactly one owner per trip.
create unique index if not exists trip_members_one_owner on public.trip_members (trip_id) where role = 'owner';

create table if not exists public.trip_invites (
    id         uuid primary key default gen_random_uuid(),
    trip_id    uuid not null references public.trips (id) on delete cascade,
    code       text not null unique,
    role       text not null check (role in ('editor', 'viewer')),
    created_by uuid references auth.users (id) on delete set null,
    created_at timestamptz not null default now(),
    expires_at timestamptz not null,
    max_uses   int check (max_uses is null or max_uses > 0),
    used_count int not null default 0,
    revoked_at timestamptz
);
create index if not exists trip_invites_trip_idx on public.trip_invites (trip_id);

-- ------------------------------------------------------------------------------------------- trip content
create table if not exists public.participants (
    id           uuid primary key,
    trip_id      uuid not null references public.trips (id) on delete cascade,
    name         text not null check (char_length(name) <= 60),
    user_id      uuid references auth.users (id) on delete set null,
    created_date timestamp,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    unique (trip_id, id)
);

create table if not exists public.places (
    id          uuid primary key,
    trip_id     uuid not null references public.trips (id) on delete cascade,
    name        text not null check (char_length(name) <= 150),
    description text check (char_length(description) <= 500),
    latitude    double precision not null default 0 check (latitude between -90 and 90),
    longitude   double precision not null default 0 check (longitude between -180 and 180),
    is_visited  boolean not null default false,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    unique (trip_id, id)
);

create table if not exists public.expenses (
    id                     uuid primary key,
    trip_id                uuid not null references public.trips (id) on delete cascade,
    category               int not null default 0,
    amount_minor           bigint not null default 0 check (amount_minor >= 0),
    original_amount_minor  bigint not null default 0 check (original_amount_minor >= 0),
    original_currency_code text check (char_length(original_currency_code) = 3),
    exchange_rate          double precision not null default 1 check (exchange_rate > 0),
    date                   date not null,
    paid_by_participant_id uuid,
    description            text check (char_length(description) <= 250),
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    unique (trip_id, id),
    -- the payer must be a participant of the SAME trip
    constraint expenses_payer_fk foreign key (trip_id, paid_by_participant_id)
        references public.participants (trip_id, id) on delete set null (paid_by_participant_id)
);

create table if not exists public.expense_shares (
    id             uuid primary key,
    trip_id        uuid not null references public.trips (id) on delete cascade,
    expense_id     uuid not null,
    participant_id uuid not null,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    constraint expense_shares_expense_fk foreign key (trip_id, expense_id)
        references public.expenses (trip_id, id) on delete cascade,
    constraint expense_shares_participant_fk foreign key (trip_id, participant_id)
        references public.participants (trip_id, id) on delete cascade
);
create unique index if not exists expense_shares_unique_live
    on public.expense_shares (expense_id, participant_id) where deleted_at is null;

create table if not exists public.todos (
    id                  uuid primary key,
    trip_id             uuid not null references public.trips (id) on delete cascade,
    title               text not null check (char_length(title) <= 150),
    is_completed        boolean not null default false,
    assigned_to_user_id uuid references auth.users (id) on delete set null,
    created_date        timestamp,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz
);

create table if not exists public.activities (
    id         uuid primary key,
    trip_id    uuid not null references public.trips (id) on delete cascade,
    date       date not null,
    start_time time,
    end_time   time,
    title      text not null check (char_length(title) <= 150),
    note       text check (char_length(note) <= 500),
    place_id   uuid,
    sort_order int not null default 0,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz,
    constraint activities_place_fk foreign key (trip_id, place_id)
        references public.places (trip_id, id) on delete set null (place_id)
);

create table if not exists public.attachments (
    id           uuid primary key,
    trip_id      uuid not null references public.trips (id) on delete cascade,
    display_name text not null check (char_length(display_name) <= 150),
    content_type text check (char_length(content_type) <= 100),
    kind         int not null default 0,
    size_bytes   bigint not null default 0 check (size_bytes >= 0),
    -- object key in the "trip-files" bucket: trips/<trip_id>/<attachment_id>.<ext>; set once uploaded (G5)
    storage_path text check (char_length(storage_path) <= 300),
    created_date timestamp,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz
);

-- ------------------------------------------------------------------------------------------- personal data
create table if not exists public.packing_templates (
    id           uuid primary key,
    owner_id     uuid not null references auth.users (id) on delete cascade,
    name         text not null check (char_length(name) <= 80),
    items        text not null default '',
    created_date timestamp,
    created_by uuid, updated_by uuid,
    created_at timestamptz not null default now(), updated_at timestamptz not null default now(),
    version bigint not null default 1, change_seq bigint not null default 0, deleted_at timestamptz
);
create index if not exists packing_templates_owner_idx on public.packing_templates (owner_id);

-- Indexes every trip-scoped table needs: by trip (RLS, pulls) and by change_seq (pulls).
do $$
declare t text;
begin
    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format('create index if not exists %I on public.%I (trip_id)', t || '_trip_idx', t);
        execute format('create index if not exists %I on public.%I (change_seq)', t || '_change_seq_idx', t);
    end loop;
    execute 'create index if not exists packing_templates_change_seq_idx on public.packing_templates (change_seq)';
end $$;

-- ------------------------------------------------------------------------------------------- bookkeeping triggers
-- Server owns version / change_seq / timestamps / identity: whatever a write sends for them is overwritten.
create or replace function private.touch_synced_row() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.change_seq := nextval('public.sync_change_seq');
    new.updated_at := now();
    if tg_op = 'INSERT' then
        new.version := 1;
        new.created_at := now();
    else
        new.id := old.id;
        new.version := old.version + 1;
        new.created_at := old.created_at;
        new.created_by := old.created_by;
    end if;
    return new;
end;
$$;

create or replace function private.touch_member_row() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.change_seq := nextval('public.sync_change_seq');
    new.updated_at := now();
    return new;
end;
$$;

do $$
declare t text;
begin
    foreach t in array array['trips', 'participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments', 'packing_templates'] loop
        execute format('drop trigger if exists touch_synced_row on public.%I', t);
        execute format('create trigger touch_synced_row before insert or update on public.%I for each row execute function private.touch_synced_row()', t);
    end loop;
end $$;

drop trigger if exists touch_member_row on public.trip_members;
create trigger touch_member_row before insert or update on public.trip_members
    for each row execute function private.touch_member_row();

-- Ownership never moves through an ordinary update.
create or replace function private.keep_owner() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.owner_id := old.owner_id;
    return new;
end;
$$;

drop trigger if exists keep_owner on public.trips;
create trigger keep_owner before update on public.trips for each row execute function private.keep_owner();
drop trigger if exists keep_owner on public.packing_templates;
create trigger keep_owner before update on public.packing_templates for each row execute function private.keep_owner();

-- A row never moves to another trip.
create or replace function private.keep_trip() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.trip_id := old.trip_id;
    return new;
end;
$$;

do $$
declare t text;
begin
    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format('drop trigger if exists keep_trip on public.%I', t);
        execute format('create trigger keep_trip before update on public.%I for each row execute function private.keep_trip()', t);
    end loop;
end $$;

-- ------------------------------------------------------------------------------------------- profile per account
create or replace function private.create_profile() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    insert into public.profiles (id, email, display_name, avatar_url)
    values (
        new.id,
        new.email,
        left(coalesce(new.raw_user_meta_data ->> 'full_name', new.raw_user_meta_data ->> 'name', split_part(new.email, '@', 1)), 80),
        new.raw_user_meta_data ->> 'avatar_url')
    on conflict (id) do nothing;
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created after insert on auth.users
    for each row execute function private.create_profile();

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000002_security.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 2: who may see what (Row Level Security) and what the API roles may touch.
-- Reading: any member of a trip (owner / editor / viewer) reads everything in it.
-- Writing: never directly - only through the RPCs in 000003/000004, which check the role themselves:
--   viewer  - reads only
--   editor  - adds / edits / deletes trip content
--   owner   - all of that + members, invites, deleting (and restoring) the trip
-- =====================================================================================================

-- Role of the current user in a trip, as a check. SECURITY DEFINER so policies on trip_members itself
-- don't recurse.
create or replace function private.has_trip_role(p_trip_id uuid, p_roles text[])
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members m
        where m.trip_id = p_trip_id
          and m.user_id = (select auth.uid())
          and m.role = any (p_roles));
$$;

create or replace function private.shares_trip_with(p_user_id uuid)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members mine
        join public.trip_members theirs on theirs.trip_id = mine.trip_id
        where mine.user_id = (select auth.uid())
          and theirs.user_id = p_user_id);
$$;

-- ------------------------------------------------------------------------------------------- RLS
alter table public.profiles          enable row level security;
alter table public.trips             enable row level security;
alter table public.trip_members      enable row level security;
alter table public.trip_invites      enable row level security;
alter table public.participants      enable row level security;
alter table public.places            enable row level security;
alter table public.expenses          enable row level security;
alter table public.expense_shares    enable row level security;
alter table public.todos             enable row level security;
alter table public.activities        enable row level security;
alter table public.attachments       enable row level security;
alter table public.packing_templates enable row level security;

drop policy if exists "profiles: self and trip mates read" on public.profiles;
create policy "profiles: self and trip mates read" on public.profiles
    for select to authenticated
    using (id = (select auth.uid()) or private.shares_trip_with(id));

drop policy if exists "profiles: self update" on public.profiles;
create policy "profiles: self update" on public.profiles
    for update to authenticated
    using (id = (select auth.uid()))
    with check (id = (select auth.uid()));

drop policy if exists "trips: members read" on public.trips;
create policy "trips: members read" on public.trips
    for select to authenticated
    using (private.has_trip_role(id, array['owner', 'editor', 'viewer']));

drop policy if exists "trip_members: members read" on public.trip_members;
create policy "trip_members: members read" on public.trip_members
    for select to authenticated
    using (private.has_trip_role(trip_id, array['owner', 'editor', 'viewer']));

drop policy if exists "trip_invites: owner reads" on public.trip_invites;
create policy "trip_invites: owner reads" on public.trip_invites
    for select to authenticated
    using (private.has_trip_role(trip_id, array['owner']));

do $$
declare t text;
begin
    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format('drop policy if exists "%s: members read" on public.%I', t, t);
        execute format(
            'create policy "%s: members read" on public.%I for select to authenticated '
            'using (private.has_trip_role(trip_id, array[''owner'', ''editor'', ''viewer'']))', t, t);
    end loop;
end $$;

drop policy if exists "packing_templates: owner reads" on public.packing_templates;
create policy "packing_templates: owner reads" on public.packing_templates
    for select to authenticated
    using (owner_id = (select auth.uid()));

-- ------------------------------------------------------------------------------------------- grants
-- The project was created with "automatically expose new tables" OFF; be explicit anyway.
revoke all on all tables in schema public from anon, authenticated;
revoke all on all sequences in schema public from anon, authenticated;

grant select on
    public.trips, public.trip_members, public.trip_invites,
    public.participants, public.places, public.expenses, public.expense_shares,
    public.todos, public.activities, public.attachments, public.packing_templates
to authenticated;

grant select on public.profiles to authenticated;
grant update (display_name, avatar_url) on public.profiles to authenticated;

-- Policies call these, so the API role needs to reach them (they only answer yes/no).
grant usage on schema private to authenticated;
revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000003_sync_rpc.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 3: the two sync calls the app makes.
--
-- public.apply_changes(p_changes jsonb) -> jsonb
--   The app's outbox (PendingChange rows), in order. Each element:
--     { "entity": "Expense",              -- the app's table name (Trip, Todo, Expense, Place, Activity,
--                                         --   Attachment, Participant, ExpenseShare, PackingTemplate)
--       "op": "upsert" | "delete",
--       "id": "<SyncId>",
--       "trip_id": "<trip SyncId>" | null,
--       "base_version": 3,                -- version the edit was based on (0 = never synced)
--       "full": false,                    -- true when "fields" holds the whole row (new / restored row)
--       "fields": { "amount_minor": 1250, "description": "Taksi" } }   -- snake_case column names
--   Every element is applied on its own (one bad change never blocks the rest) and answered with
--     { "id", "entity", "status": applied | deleted | ignored | rejected,
--       "reason": forbidden | trip_missing | trip_deleted | row_deleted | trip_mismatch | invalid | unknown_entity,
--       "version", "change_seq", "conflict": true when someone else changed the row since base_version,
--       "error": database message for "invalid" }
--   Rules: only the changed fields are written (two people editing different fields both keep their edit;
--   on the same field the later write wins); a delete beats an offline edit (row_deleted) but a "full"
--   upsert - restoring from the trash - brings a deleted row back; deleting an unknown row is ignored.
--
-- public.pull_changes(p_since bigint) -> jsonb
--   Everything the user can see that changed after p_since, plus everything in trips they joined after it:
--     { "cursor": <next p_since>, "memberships": [{trip_id, role, change_seq}],
--       "trips": [...], "participants": [...], "places": [...], "expenses": [...], "expense_shares": [...],
--       "todos": [...], "activities": [...], "attachments": [...], "packing_templates": [...] }
--   Rows with deleted_at set mean "delete locally". A trip missing from "memberships" (that the app had
--   synced before) means the user was removed from it. Rows changed in the last few seconds are sent but
--   the cursor doesn't move past them, so a slow concurrent write is never skipped (they may arrive twice -
--   applying a row is idempotent).
-- =====================================================================================================

create or replace function private.sync_table(p_entity text) returns text
language sql
immutable
set search_path = ''
as $$
    select case p_entity
        when 'Trip' then 'trips'                    when 'trips' then 'trips'
        when 'Participant' then 'participants'      when 'participants' then 'participants'
        when 'Place' then 'places'                  when 'places' then 'places'
        when 'Expense' then 'expenses'              when 'expenses' then 'expenses'
        when 'ExpenseShare' then 'expense_shares'   when 'expense_shares' then 'expense_shares'
        when 'Todo' then 'todos'                    when 'todos' then 'todos'
        when 'Activity' then 'activities'           when 'activities' then 'activities'
        when 'Attachment' then 'attachments'        when 'attachments' then 'attachments'
        when 'PackingTemplate' then 'packing_templates' when 'packing_templates' then 'packing_templates'
    end;
$$;

-- Columns a client may set: everything except identity, scope and bookkeeping.
create or replace function private.writable_columns(p_table text) returns text[]
language sql
stable
set search_path = ''
as $$
    select coalesce(array_agg(c.column_name::text), '{}')
    from information_schema.columns c
    where c.table_schema = 'public'
      and c.table_name = p_table
      and c.column_name not in ('id', 'trip_id', 'owner_id', 'created_by', 'created_at', 'updated_by',
                                'updated_at', 'version', 'change_seq', 'deleted_at');
$$;

create or replace function private.change_result(
    p_change jsonb, p_status text, p_reason text default null,
    p_version bigint default null, p_change_seq bigint default null,
    p_conflict boolean default false, p_error text default null)
returns jsonb
language sql
immutable
set search_path = ''
as $$
    select jsonb_strip_nulls(jsonb_build_object(
        'id', p_change ->> 'id',
        'entity', p_change ->> 'entity',
        'status', p_status,
        'reason', p_reason,
        'version', p_version,
        'change_seq', p_change_seq,
        'conflict', p_conflict,
        'error', p_error));
$$;

-- One change. Runs inside apply_changes' per-change exception block.
create or replace function private.apply_change(p_change jsonb, p_uid uuid) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_table    text := private.sync_table(p_change ->> 'entity');
    v_op       text := coalesce(p_change ->> 'op', 'upsert');
    v_id       uuid := (p_change ->> 'id')::uuid;
    v_trip     uuid := nullif(p_change ->> 'trip_id', '')::uuid;
    v_base     bigint := coalesce((p_change ->> 'base_version')::bigint, 0);
    v_full     boolean := coalesce((p_change ->> 'full')::boolean, false);
    v_fields   jsonb := coalesce(p_change -> 'fields', '{}'::jsonb);
    v_row      jsonb;
    v_deleted  boolean;
    v_keys     text[];
    v_cols     text;
    v_vals     text;
    v_set      text;
    v_version  bigint;
    v_seq      bigint;
    v_conflict boolean := false;
begin
    if v_table is null then
        return private.change_result(p_change, 'rejected', 'unknown_entity');
    end if;
    if v_id is null or v_op not in ('upsert', 'delete') or jsonb_typeof(v_fields) <> 'object' then
        return private.change_result(p_change, 'rejected', 'invalid');
    end if;

    execute format('select to_jsonb(t) from public.%I t where t.id = $1 for update', v_table) into v_row using v_id;
    v_deleted := v_row is not null and v_row ->> 'deleted_at' is not null;

    -- ---------------------------------------------------------------- who may do this
    if v_table = 'trips' then
        v_trip := v_id;
        if v_row is not null then
            if v_op = 'delete' or v_deleted then
                -- deleting a trip, or bringing a deleted one back, is the owner's call
                if not private.has_trip_role(v_id, array['owner']) then
                    return private.change_result(p_change, 'rejected', 'forbidden');
                end if;
            elsif not private.has_trip_role(v_id, array['owner', 'editor']) then
                return private.change_result(p_change, 'rejected', 'forbidden');
            end if;
        end if;
        -- (a new trip may be created by anyone signed in: they become its owner)
    elsif v_table = 'packing_templates' then
        if v_row is not null and (v_row ->> 'owner_id')::uuid is distinct from p_uid then
            return private.change_result(p_change, 'rejected', 'forbidden');
        end if;
    else
        if v_row is not null then
            if v_trip is null then
                v_trip := (v_row ->> 'trip_id')::uuid;
            elsif v_trip <> (v_row ->> 'trip_id')::uuid then
                return private.change_result(p_change, 'rejected', 'trip_mismatch');
            end if;
        end if;
        if v_trip is null or not exists (select 1 from public.trips t where t.id = v_trip) then
            return private.change_result(p_change, 'rejected', 'trip_missing');
        end if;
        if not private.has_trip_role(v_trip, array['owner', 'editor']) then
            return private.change_result(p_change, 'rejected', 'forbidden');
        end if;
        if exists (select 1 from public.trips t where t.id = v_trip and t.deleted_at is not null) then
            return private.change_result(p_change, 'rejected', 'trip_deleted');
        end if;
    end if;

    -- ---------------------------------------------------------------- delete (soft)
    if v_op = 'delete' then
        if v_row is null or v_deleted then
            return private.change_result(p_change, 'ignored');
        end if;
        execute format('update public.%I set deleted_at = now(), updated_by = $2 where id = $1 returning version, change_seq', v_table)
            into v_version, v_seq using v_id, p_uid;
        return private.change_result(p_change, 'deleted', null, v_version, v_seq);
    end if;

    -- ---------------------------------------------------------------- upsert
    select array_agg(k) into v_keys
    from jsonb_object_keys(v_fields) k
    where k = any (private.writable_columns(v_table));
    v_keys := coalesce(v_keys, '{}');

    if v_row is null then
        -- new row: the server fills in identity, scope and author
        v_fields := v_fields || jsonb_build_object('id', v_id, 'created_by', p_uid, 'updated_by', p_uid);
        v_keys := v_keys || array['id', 'created_by', 'updated_by'];
        if v_table in ('trips', 'packing_templates') then
            v_fields := v_fields || jsonb_build_object('owner_id', p_uid);
            v_keys := v_keys || array['owner_id'];
        else
            v_fields := v_fields || jsonb_build_object('trip_id', v_trip);
            v_keys := v_keys || array['trip_id'];
        end if;

        select string_agg(format('%I', k), ', '), string_agg(format('r.%I', k), ', ')
        into v_cols, v_vals
        from unnest(v_keys) k;

        execute format('insert into public.%I (%s) select %s from jsonb_populate_record(null::public.%I, $1) r returning version, change_seq',
                       v_table, v_cols, v_vals, v_table)
            into v_version, v_seq using v_fields;

        if v_table = 'trips' then
            insert into public.trip_members (trip_id, user_id, role, invited_by)
            values (v_id, p_uid, 'owner', p_uid);
        end if;

        return private.change_result(p_change, 'applied', null, v_version, v_seq);
    end if;

    -- existing row
    if v_deleted and not v_full then
        -- deleted by someone else while this edit waited offline: the delete wins
        return private.change_result(p_change, 'rejected', 'row_deleted');
    end if;

    v_conflict := v_base > 0 and v_base < (v_row ->> 'version')::bigint;

    if cardinality(v_keys) = 0 and not v_deleted then
        return private.change_result(p_change, 'applied', null, (v_row ->> 'version')::bigint, (v_row ->> 'change_seq')::bigint, v_conflict);
    end if;

    select string_agg(format('%I = r.%I', k, k), ', ') into v_set from unnest(v_keys) k;
    v_set := coalesce(v_set || ', ', '') || 'updated_by = $3, deleted_at = null';

    execute format('update public.%I t set %s from jsonb_populate_record(null::public.%I, $1) r where t.id = $2 returning t.version, t.change_seq',
                   v_table, v_set, v_table)
        into v_version, v_seq using v_fields, v_id, p_uid;

    return private.change_result(p_change, 'applied', null, v_version, v_seq, v_conflict);
end;
$$;

create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_change  jsonb;
    v_results jsonb := '[]'::jsonb;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    -- G7: a banned or deleted account is refused at once, even with a still-valid token (migration 8)
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    if jsonb_typeof(p_changes) <> 'array' then
        raise exception 'p_changes must be a JSON array' using errcode = '22023';
    end if;
    if jsonb_array_length(p_changes) > 500 then
        raise exception 'at most 500 changes per call' using errcode = '22023';
    end if;

    for v_change in select value from jsonb_array_elements(p_changes) loop
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception when others then
            -- constraint violations, bad values, a parent not uploaded yet ... - this change only
            v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => sqlerrm);
        end;
    end loop;

    return v_results;
end;
$$;

create or replace function public.pull_changes(p_since bigint default 0) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := (select auth.uid());
    v_since  bigint := greatest(coalesce(p_since, 0), 0);
    v_safe   timestamptz := now() - interval '10 seconds';
    v_result jsonb;
    v_cursor bigint := v_since;
    v_rows   jsonb;
    v_max    bigint;
    t        text;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    -- G7: a banned or deleted account is refused at once, even with a still-valid token (migration 8)
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;

    select coalesce(jsonb_agg(jsonb_build_object('trip_id', m.trip_id, 'role', m.role, 'change_seq', m.change_seq)), '[]'),
           max(m.change_seq) filter (where m.change_seq > v_since and m.updated_at < v_safe)
    into v_rows, v_max
    from public.trip_members m
    where m.user_id = v_uid;
    v_result := jsonb_build_object('memberships', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    -- trips: changed since the cursor, or joined since the cursor (then the whole live trip)
    select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), '[]'),
           max(x.change_seq) filter (where x.updated_at < v_safe)
    into v_rows, v_max
    from public.trips x
    join public.trip_members m on m.trip_id = x.id and m.user_id = v_uid
    where x.change_seq > v_since or (m.change_seq > v_since and x.deleted_at is null);
    v_result := v_result || jsonb_build_object('trips', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format(
            'select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), ''[]''), '
            '       max(x.change_seq) filter (where x.updated_at < $3) '
            'from public.%I x '
            'join public.trip_members m on m.trip_id = x.trip_id and m.user_id = $1 '
            'where x.change_seq > $2 or (m.change_seq > $2 and x.deleted_at is null)', t)
        into v_rows, v_max
        using v_uid, v_since, v_safe;
        v_result := v_result || jsonb_build_object(t, v_rows);
        v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));
    end loop;

    select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), '[]'),
           max(x.change_seq) filter (where x.updated_at < v_safe)
    into v_rows, v_max
    from public.packing_templates x
    where x.owner_id = v_uid and x.change_seq > v_since;
    v_result := v_result || jsonb_build_object('packing_templates', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    return v_result || jsonb_build_object('cursor', v_cursor);
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
revoke all on function public.pull_changes(bigint) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;
grant execute on function public.pull_changes(bigint) to authenticated;
revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000004_sharing_rpc.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 4: sharing a trip and managing the account. All of these need a connection
-- (the app greys them out offline).
--   create_invite(trip, role, valid_hours, max_uses) -> code      owner
--   revoke_invite(code)                                           owner
--   accept_invite(code) -> trip_id, role, trip_name               anyone signed in
--   get_trip_members(trip) -> user_id, role, display_name, ...    any member
--   set_member_role(trip, user, role)                             owner (editor <-> viewer)
--   remove_member(trip, user)                                     owner
--   leave_trip(trip)                                              editor / viewer
--   delete_account()                                              the user themself
-- =====================================================================================================

create or replace function private.require_uid() returns uuid
language plpgsql
stable
set search_path = ''
as $$
declare v_uid uuid := (select auth.uid());
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    return v_uid;
end;
$$;

create or replace function private.require_owner(p_trip_id uuid) returns uuid
language plpgsql
stable
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    if not exists (select 1 from public.trip_members m
                   where m.trip_id = p_trip_id and m.user_id = v_uid and m.role = 'owner') then
        raise exception 'only the trip owner can do this' using errcode = '42501';
    end if;
    return v_uid;
end;
$$;

-- 8 characters from an alphabet without look-alikes (0/O, 1/I/L): 31^8 ≈ 8.5 * 10^11 codes.
create or replace function private.new_invite_code() returns text
language plpgsql
volatile
set search_path = ''
as $$
declare
    v_alphabet constant text := 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';  -- 31 letters/digits
    v_bytes bytea := uuid_send(gen_random_uuid()) || uuid_send(gen_random_uuid());
    v_code  text := '';
    i int;
begin
    for i in 0..7 loop
        v_code := v_code || substr(v_alphabet, (get_byte(v_bytes, i) % length(v_alphabet)) + 1, 1);
    end loop;
    return v_code;
end;
$$;

create or replace function public.create_invite(
    p_trip_id uuid, p_role text default 'editor', p_valid_hours int default 168, p_max_uses int default null)
returns table (code text, expires_at timestamptz)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid  uuid := private.require_owner(p_trip_id);
    v_code text;
begin
    if p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    if p_valid_hours is null or p_valid_hours < 1 or p_valid_hours > 24 * 30 then
        raise exception 'valid_hours must be between 1 and 720' using errcode = '22023';
    end if;
    if exists (select 1 from public.trips t where t.id = p_trip_id and t.deleted_at is not null) then
        raise exception 'trip is deleted' using errcode = '22023';
    end if;

    loop
        v_code := private.new_invite_code();
        exit when not exists (select 1 from public.trip_invites i where i.code = v_code);
    end loop;

    return query
    insert into public.trip_invites (trip_id, code, role, created_by, expires_at, max_uses)
    values (p_trip_id, v_code, p_role, v_uid, now() + make_interval(hours => p_valid_hours), p_max_uses)
    returning trip_invites.code, trip_invites.expires_at;
end;
$$;

create or replace function public.revoke_invite(p_code text) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_trip uuid;
begin
    select i.trip_id into v_trip from public.trip_invites i where i.code = upper(trim(p_code));
    if v_trip is null then
        return;
    end if;
    perform private.require_owner(v_trip);
    update public.trip_invites set revoked_at = now() where code = upper(trim(p_code)) and revoked_at is null;
end;
$$;

create or replace function public.accept_invite(p_code text)
returns table (trip_id uuid, role text, trip_name text)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid    uuid := private.require_uid();
    v_invite public.trip_invites;
    v_role   text;
begin
    select * into v_invite from public.trip_invites i where i.code = upper(trim(p_code)) for update;

    if v_invite.id is null or v_invite.revoked_at is not null or v_invite.expires_at < now()
       or (v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses)
       or exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        raise exception 'invite is invalid or expired' using errcode = 'P0002';
    end if;

    -- Already a member: keep the current role (an invite never demotes the owner or changes a role).
    select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
    if v_role is null then
        insert into public.trip_members (trip_id, user_id, role, invited_by)
        values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
        update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;
        v_role := v_invite.role;
    end if;

    return query select v_invite.trip_id, v_role, t.name from public.trips t where t.id = v_invite.trip_id;
end;
$$;

create or replace function public.get_trip_members(p_trip_id uuid)
returns table (user_id uuid, role text, display_name text, email text, avatar_url text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
begin
    perform private.require_uid();
    if not private.has_trip_role(p_trip_id, array['owner', 'editor', 'viewer']) then
        raise exception 'not a member of this trip' using errcode = '42501';
    end if;

    return query
    select m.user_id, m.role, p.display_name, p.email, p.avatar_url, m.joined_at
    from public.trip_members m
    left join public.profiles p on p.id = m.user_id
    where m.trip_id = p_trip_id
    order by case m.role when 'owner' then 0 when 'editor' then 1 else 2 end, m.joined_at;
end;
$$;

create or replace function public.set_member_role(p_trip_id uuid, p_user_id uuid, p_role text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.require_owner(p_trip_id);
    if p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    update public.trip_members
    set role = p_role
    where trip_id = p_trip_id and user_id = p_user_id and role <> 'owner';
    if not found then
        raise exception 'no such member (or it is the owner)' using errcode = 'P0002';
    end if;
end;
$$;

create or replace function public.remove_member(p_trip_id uuid, p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.require_owner(p_trip_id);
    delete from public.trip_members
    where trip_id = p_trip_id and user_id = p_user_id and role <> 'owner';
    if not found then
        raise exception 'no such member (or it is the owner)' using errcode = 'P0002';
    end if;
end;
$$;

create or replace function public.leave_trip(p_trip_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    delete from public.trip_members
    where trip_id = p_trip_id and user_id = v_uid and role <> 'owner';
    if not found then
        raise exception 'not a member, or the owner (the owner deletes the trip instead)' using errcode = 'P0002';
    end if;
end;
$$;

-- Deletes the account and everything it owns (owned trips with all their content, memberships, packing
-- templates, profile). The app deletes the account's files from Storage first (G7).
create or replace function public.delete_account() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    delete from public.trips where owner_id = v_uid;   -- cascades to content, members, invites
    delete from auth.users where id = v_uid;           -- cascades to profile, memberships, templates
end;
$$;

do $$
declare f text;
begin
    foreach f in array array[
        'public.create_invite(uuid, text, int, int)',
        'public.revoke_invite(text)',
        'public.accept_invite(text)',
        'public.get_trip_members(uuid)',
        'public.set_member_role(uuid, uuid, text)',
        'public.remove_member(uuid, uuid)',
        'public.leave_trip(uuid)',
        'public.delete_account()'] loop
        execute format('revoke all on function %s from public, anon', f);
        execute format('grant execute on function %s to authenticated', f);
    end loop;
end $$;

revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000005_storage.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 5: attachment files.
-- Private bucket "trip-files"; object keys: trips/<trip_id>/<attachment_id>.<ext>
-- Members of the trip read; owner/editor upload, replace and delete. Max 50 MB per file (the app's limit;
-- also the Free plan's cap).
-- =====================================================================================================

insert into storage.buckets (id, name, public, file_size_limit)
values ('trip-files', 'trip-files', false, 52428800)
on conflict (id) do update set public = false, file_size_limit = excluded.file_size_limit;

-- "trips/<uuid>/..." -> <uuid>; anything else -> null (no access).
create or replace function private.trip_id_from_path(p_name text) returns uuid
language plpgsql
immutable
set search_path = ''
as $$
declare v_parts text[] := string_to_array(p_name, '/');
begin
    if array_length(v_parts, 1) >= 3
       and v_parts[1] = 'trips'
       and v_parts[2] ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
        return v_parts[2]::uuid;
    end if;
    return null;
end;
$$;

grant execute on function private.trip_id_from_path(text) to authenticated;

drop policy if exists "trip-files: members read" on storage.objects;
create policy "trip-files: members read" on storage.objects
    for select to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor', 'viewer']));

drop policy if exists "trip-files: editors upload" on storage.objects;
create policy "trip-files: editors upload" on storage.objects
    for insert to authenticated
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));

drop policy if exists "trip-files: editors replace" on storage.objects;
create policy "trip-files: editors replace" on storage.objects
    for update to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']))
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));

drop policy if exists "trip-files: editors delete" on storage.objects;
create policy "trip-files: editors delete" on storage.objects
    for delete to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000006_cron.sql
-- =====================================================================================================
-- TravelPlanner - G2 / 6: nightly clean-up.
-- Rows soft-deleted more than 30 days ago are removed for good (the app's trash keeps them restorable for
-- the same 30 days); expired/revoked invites go after 30 days too.
-- Needs the pg_cron extension. If enabling it here fails, turn it on in the dashboard
-- (Database -> Extensions -> pg_cron) and run this file again.
-- =====================================================================================================

create or replace function private.purge_deleted_rows() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_cutoff timestamptz := now() - interval '30 days';
    t text;
begin
    -- children first; trips last (their delete cascades to whatever is left)
    foreach t in array array['expense_shares', 'expenses', 'todos', 'activities', 'places', 'participants', 'attachments', 'packing_templates', 'trips'] loop
        execute format('delete from public.%I where deleted_at < $1', t) using v_cutoff;
    end loop;

    delete from public.trip_invites
    where coalesce(revoked_at, expires_at) < v_cutoff;
end;
$$;

revoke all on function private.purge_deleted_rows() from public, anon, authenticated;

do $$
begin
    create extension if not exists pg_cron;
exception when others then
    raise notice 'pg_cron could not be enabled here (%). Enable it in Database -> Extensions and re-run this file.', sqlerrm;
end $$;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-deleted';
        perform cron.schedule('travelplanner-purge-deleted', '17 3 * * *', 'select private.purge_deleted_rows()');
    end if;
end $$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000007_account_deletion.sql
-- =====================================================================================================
-- TravelPlanner - G7 / 7: deleting an account from the app.
-- Supabase refuses to delete an auth user who still owns Storage objects. The app deletes the files of the
-- trips the account owns first (Storage API); files the account uploaded to OTHER people's trips stay,
-- because those trips still use them - here they only lose their owner, so the user can be deleted.
-- Safe to run again.
-- =====================================================================================================

create or replace function public.delete_account() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    begin
        update storage.objects set owner = null, owner_id = null
        where owner = v_uid or owner_id = v_uid::text;
    exception when undefined_column then
        -- older Storage schema without owner_id
        update storage.objects set owner = null where owner = v_uid;
    end;

    delete from public.trips where owner_id = v_uid;   -- cascades to content, members, invites
    delete from auth.users where id = v_uid;           -- cascades to profile, memberships, templates
end;
$$;

revoke all on function public.delete_account() from public, anon;
grant execute on function public.delete_account() to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261010000008_account_status.sql
-- =====================================================================================================
-- TravelPlanner - G7 / 8: banned and deleted accounts.
-- * The app asks account_status(<its user id>) at start-up and when it comes back to the foreground: if the
--   account was deleted or banned (Dashboard -> Authentication -> Users, or private.ban_user below) it signs
--   out and tells the user why. Works with the publishable key alone, because by then the session may be gone.
-- * Every RPC, table policy and Storage policy refuses a deleted or banned account at once (require_uid,
--   has_trip_role; apply_changes / pull_changes in migration 3), even while an access token issued before
--   the ban is still valid (up to an hour).
-- * Supabase Auth itself refuses to sign a banned user in again (e-mail or Google: error "user_banned").
-- Admin helpers, run in the SQL Editor:
--   select private.ban_user('someone@example.com');                 -- banned "for good" (100 years)
--   select private.ban_user('someone@example.com', interval '7 days');
--   select private.unban_user('someone@example.com');
-- Safe to run again.
-- =====================================================================================================

-- 'active' | 'banned' | 'deleted'
create or replace function private.account_state(p_uid uuid) returns text
language sql
stable
security definer
set search_path = ''
as $$
    select case
               when u.id is null then 'deleted'
               when u.banned_until is not null and u.banned_until > now() then 'banned'
               else 'active'
           end
    from (select 1) as one
    left join auth.users u on u.id = p_uid
$$;

revoke all on function private.account_state(uuid) from public, anon;
grant execute on function private.account_state(uuid) to authenticated;

-- Only the state is revealed, nothing else about the account.
create or replace function public.account_status(p_user_id uuid) returns text
language sql
stable
security definer
set search_path = ''
as $$
    select private.account_state(p_user_id)
$$;

revoke all on function public.account_status(uuid) from public;
grant execute on function public.account_status(uuid) to anon, authenticated;

create or replace function private.require_uid() returns uuid
language plpgsql
stable
set search_path = ''
as $$
declare v_uid uuid := (select auth.uid());
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    return v_uid;
end;
$$;

-- Roles count only for an active account: RLS and Storage refuse a banned one at once too.
create or replace function private.has_trip_role(p_trip_id uuid, p_roles text[])
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members m
        where m.trip_id = p_trip_id
          and m.user_id = (select auth.uid())
          and m.role = any (p_roles))
       and private.account_state((select auth.uid())) = 'active';
$$;

-- ---------------------------------------------------------------------------------- admin helpers
create or replace function private.ban_user(p_email text, p_duration interval default interval '100 years') returns text
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid uuid;
    v_until timestamptz := now() + p_duration;
begin
    select id into v_uid from auth.users where lower(email) = lower(trim(p_email));
    if v_uid is null then
        raise exception 'no account with e-mail %', p_email;
    end if;

    update auth.users set banned_until = v_until where id = v_uid;
    -- sign out every device: their refresh tokens stop working at once
    delete from auth.sessions where user_id = v_uid;
    delete from auth.refresh_tokens where user_id = v_uid::text;

    return format('%s banned until %s', p_email, v_until);
end;
$$;

create or replace function private.unban_user(p_email text) returns text
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where lower(email) = lower(trim(p_email));
    if not found then
        raise exception 'no account with e-mail %', p_email;
    end if;
    return format('%s can sign in again', p_email);
end;
$$;

revoke all on function private.ban_user(text, interval) from public, anon, authenticated;
revoke all on function private.unban_user(text) from public, anon, authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261012000009_ai_planner.sql
-- =====================================================================================================
-- TravelPlanner - AI trip planner (migration 9): a daily quota per account AND per device.
-- The Edge Function "plan-trip" calls ai_plan_quota(<device>) with the user's own token before it asks the
-- AI, so only signed-in, active (not banned) accounts can use it - at most ai_daily_limit() plans a day per
-- account, and the same per device: signing in with another account on the same phone doesn't give new plans.
-- <device> is a SHA-256 hash the app makes from the device's id; the raw id never leaves the phone.
-- The OpenAI key never leaves Supabase (Edge Function secret OPENAI_API_KEY).
-- Change the daily limit in private.ai_daily_limit() (and run this file again). Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_usage (
    user_id uuid not null references auth.users (id) on delete cascade,
    day     date not null default current_date,
    count   int  not null default 0,
    primary key (user_id, day)
);

create table if not exists private.ai_device_usage (
    device text not null check (device ~ '^[0-9a-f]{64}$'),
    day    date not null default current_date,
    count  int  not null default 0,
    primary key (device, day)
);

create or replace function private.ai_daily_limit() returns int
language sql immutable as $$ select 5 $$;

-- the first version had no device parameter
drop function if exists public.ai_plan_quota();
drop function if exists public.ai_plan_quota_status();

create or replace function private.ai_check_device(p_device text) returns text
language plpgsql immutable as $$
begin
    if p_device is null or p_device !~ '^[0-9a-f]{64}$' then
        raise exception 'invalid device id' using errcode = '22023';
    end if;
    return p_device;
end;
$$;

-- How many plans are left today for this account on this device (does not use one).
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_daily_limit();
    v_user   int;
    v_dev    int;
begin
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    return jsonb_build_object('limit', v_limit,
                              'remaining', greatest(v_limit - greatest(coalesce(v_user, 0), coalesce(v_dev, 0)), 0));
end;
$$;

-- Uses one plan of today's quota for both the account and the device; HTTP 429 (SQLSTATE PT429) when either
-- has none left.
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

    -- keep the tables small: forget usage older than a month
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;

    return jsonb_build_object('limit', v_limit, 'remaining', v_limit - greatest(v_user, v_dev) - 1);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261012000010_ai_refund.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000011_value_limits.sql
-- =====================================================================================================
-- TravelPlanner - SEC-01 / 11: value limits on every synced column (same limits as the app's InputLimits).
--
-- apply_changes writes whatever a member of the trip sends, and every other member's device stores it.
-- Without these limits one editor calling the REST API directly could write e.g. amount_minor =
-- 9223372036854775807, exchange_rate = 1e300 or date = '20000-01-01' (PostgreSQL accepts it) and break the
-- sync (and the totals) of everyone in the trip. With them such a change is refused by apply_changes as
-- "invalid" (a check violation), and the rest of the batch still goes through.
--
-- The limits mirror Helpers/InputLimits.cs:
--   money (minor units)  0 .. 999 999 999 999        (InputLimits.MaxMoneyMinor = 9 999 999 999.99)
--   exchange rate        0.000001 .. 1 000 000       (InputLimits.MinExchangeRate / MaxExchangeRate)
--   dates                1900-01-01 .. 2100-12-31    (InputLimits.MinDate / MaxDate)
--   expense category     0 .. 5                      (Models/ExpenseCategory.cs)
--   attachment kind      0 .. 1                      (Models/TripAttachment.cs, AttachmentKind)
--   activity sort order  0 .. 100 000               (position within a day)
--   activity end time    after the start time (and only together with a start time)
--
-- Constraints are added NOT VALID first, so a row stored before this migration never blocks it; then each one
-- is validated. If old data breaks a limit, a NOTICE names the constraint and it stays NOT VALID: new writes
-- (and edits of those old rows) are still checked. Safe to run again.
-- =====================================================================================================

create or replace function pg_temp.add_limit(p_table text, p_name text, p_check text) returns void
language plpgsql
as $$
begin
    if not exists (select 1 from pg_constraint
                   where conname = p_name and conrelid = format('public.%I', p_table)::regclass) then
        execute format('alter table public.%I add constraint %I check (%s) not valid', p_table, p_name, p_check);
    end if;
    begin
        execute format('alter table public.%I validate constraint %I', p_table, p_name);
    exception when check_violation then
        raise notice 'SEC-01: existing rows of public.% break %, it stays NOT VALID (new writes are still checked)', p_table, p_name;
    end;
end;
$$;

-- ------------------------------------------------------------------------------------------- trips
select pg_temp.add_limit('trips', 'trips_planned_budget_limit', 'planned_budget_minor between 0 and 999999999999');
select pg_temp.add_limit('trips', 'trips_start_date_range', 'start_date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('trips', 'trips_end_date_range', 'end_date between date ''1900-01-01'' and date ''2100-12-31''');

-- ------------------------------------------------------------------------------------------- expenses
select pg_temp.add_limit('expenses', 'expenses_amount_limit', 'amount_minor between 0 and 999999999999');
select pg_temp.add_limit('expenses', 'expenses_original_amount_limit', 'original_amount_minor between 0 and 999999999999');
-- (NaN and Infinity fail "between" too)
select pg_temp.add_limit('expenses', 'expenses_exchange_rate_range', 'exchange_rate between 0.000001 and 1000000');
select pg_temp.add_limit('expenses', 'expenses_date_range', 'date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('expenses', 'expenses_category_range', 'category between 0 and 5');

-- ------------------------------------------------------------------------------------------- activities
select pg_temp.add_limit('activities', 'activities_date_range', 'date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('activities', 'activities_sort_order_range', 'sort_order between 0 and 100000');
select pg_temp.add_limit('activities', 'activities_end_after_start',
                         'end_time is null or (start_time is not null and end_time > start_time)');

-- ------------------------------------------------------------------------------------------- attachments
select pg_temp.add_limit('attachments', 'attachments_kind_range', 'kind between 0 and 1');

-- ------------------------------------------------------------------------------------------- created_date (timestamp)
-- The app parses these back into DateTime as well, so they get the same calendar range.
select pg_temp.add_limit('participants', 'participants_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('todos', 'todos_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('attachments', 'attachments_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('packing_templates', 'packing_templates_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000012_ai_cost_limits.sql
-- =====================================================================================================
-- TravelPlanner - SEC-02 / 12: the AI planner's cost can't run away.
--
-- Migration 9 counts plans per account and per device, migration 10 gives a plan back when the AI fails.
-- Neither is a real limit on its own: the device id is just a value the app sends (any 64-character hex string
-- passes) and new accounts cost nothing. So on top of them:
--
--   1. A global daily ceiling for ALL users together (private.ai_global_daily_limit, default 200 plans):
--      past it everyone gets HTTP 429 until midnight (UTC) - the OpenAI bill of a day has an upper bound.
--   2. New accounts get fewer plans in their first 24 hours (private.ai_new_account_limit, default 2), so
--      opening throw-away accounts multiplies far less.
--   3. At most private.ai_daily_refund_limit() (default 2) plans are given back per account and day. The Edge
--      Function now refunds only when OpenAI gave no answer at all (network error, HTTP error) - an answer that
--      was cut off or couldn't be parsed is billed by OpenAI and is not refunded.
--
-- The device id stays a helper signal (one phone can't collect plans with many accounts); the real protection
-- is the account quota + the new-account quota + the global ceiling (+ a monthly hard limit in the OpenAI panel).
-- Change the numbers in the private.ai_* functions below and run this file again. Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_global_usage (
    day   date primary key default current_date,
    count int  not null default 0
);
alter table private.ai_global_usage enable row level security;

create or replace function private.ai_global_daily_limit() returns int
language sql immutable as $$ select 200 $$;

create or replace function private.ai_new_account_limit() returns int
language sql immutable as $$ select 2 $$;

create or replace function private.ai_daily_refund_limit() returns int
language sql immutable as $$ select 2 $$;

-- Today's plan limit of an account: lower in its first 24 hours.
create or replace function private.ai_limit_for(p_uid uuid) returns int
language sql
stable
security definer
set search_path = ''
as $$
    select case
               when u.created_at is not null and u.created_at > now() - interval '24 hours'
                   then least(private.ai_new_account_limit(), private.ai_daily_limit())
               else private.ai_daily_limit()
           end
      from (select 1) one
      left join auth.users u on u.id = p_uid
$$;
revoke all on function private.ai_limit_for(uuid) from public, anon, authenticated;

-- How many plans are left today for this account on this device (does not use one). 0 also when the global
-- ceiling is reached.
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    return jsonb_build_object(
        'limit', v_limit,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(coalesce(v_user, 0), coalesce(v_dev, 0)), 0) end);
end;
$$;

-- Uses one plan of today's quota for the account, the device and the global ceiling; HTTP 429 (SQLSTATE PT429)
-- when any of them has none left. Returns the ticket for ai_plan_refund (migration 10).
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    insert into private.ai_usage (user_id, day, count) values (v_uid, current_date, 0)
    on conflict (user_id, day) do nothing;
    insert into private.ai_device_usage (device, day, count) values (v_device, current_date, 0)
    on conflict (device, day) do nothing;
    insert into private.ai_global_usage (day, count) values (current_date, 0)
    on conflict (day) do nothing;

    -- lock the rows (always in this order: account, device, global), so two requests at once can't both take
    -- the last plan
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date for update;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date for update;
    select count into v_global from private.ai_global_usage where day = current_date for update;

    if v_user >= v_limit or v_dev >= v_limit then
        raise exception 'daily AI planning limit reached' using errcode = 'PT429';
    end if;
    if v_global >= private.ai_global_daily_limit() then
        raise exception 'global daily AI planning limit reached' using errcode = 'PT429';
    end if;

    update private.ai_usage set count = count + 1 where user_id = v_uid and day = current_date;
    update private.ai_device_usage set count = count + 1 where device = v_device and day = current_date;
    update private.ai_global_usage set count = count + 1 where day = current_date;

    insert into private.ai_quota_ticket (user_id, device, day) values (v_uid, v_device, current_date)
    returning id into v_ticket;

    -- keep the tables small
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;
    delete from private.ai_global_usage where day < current_date - 366;
    delete from private.ai_quota_ticket where created_at < now() - interval '2 days';

    return jsonb_build_object('limit', v_limit, 'remaining', v_limit - greatest(v_user, v_dev) - 1, 'ticket', v_ticket);
end;
$$;

-- Gives back the plan of a ticket whose AI request got no answer. True when something was given back.
-- A ticket works once, for its own account, within 10 minutes - and at most ai_daily_refund_limit() a day.
create or replace function public.ai_plan_refund(p_ticket uuid) returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid      uuid := private.require_uid();
    v_refunded int;
    t          private.ai_quota_ticket;
begin
    select * into t
      from private.ai_quota_ticket
     where id = p_ticket
       and user_id = v_uid
       and not refunded
       and created_at > now() - interval '10 minutes';
    if t.id is null then
        return false;
    end if;

    -- the account's usage row of that day serializes refunds of one account (same lock order as ai_plan_quota)
    perform 1 from private.ai_usage where user_id = t.user_id and day = t.day for update;

    select count(*) into v_refunded
      from private.ai_quota_ticket
     where user_id = v_uid and day = t.day and refunded;
    if v_refunded >= private.ai_daily_refund_limit() then
        return false;
    end if;

    update private.ai_quota_ticket set refunded = true where id = t.id and not refunded;
    if not found then
        return false;
    end if;

    update private.ai_usage set count = count - 1 where user_id = t.user_id and day = t.day and count > 0;
    update private.ai_device_usage set count = count - 1 where device = t.device and day = t.day and count > 0;
    update private.ai_global_usage set count = count - 1 where day = t.day and count > 0;
    return true;
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
revoke all on function public.ai_plan_refund(uuid) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;
grant execute on function public.ai_plan_refund(uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000013_quota_limits.sql
-- =====================================================================================================
-- TravelPlanner - TP-3 / 13: usage limits, so one account can't fill the Free plan for everyone
-- (500 MB database / 1 GB Storage; once full the project turns read-only and the app stops working).
--
-- 1. Row limits (trigger on every synced table - covers apply_changes and any future write path):
--      per account: 200 trips it owns, 100 packing templates
--      per trip:    2 000 expenses, 500 places, 500 activities, 200 attachments, 200 participants,
--                   2 000 to-dos, 50 000 expense shares
--    The limit counts live rows; rows in the trash count as well, up to twice the limit (so creating and
--    deleting in a loop can't grow the table either - the trash is purged after 30 days, migration 6).
--    Restoring a row from the trash is checked against the live limit too.
--    On top: at most 10 000 NEW rows per account per day (UTC), whatever the trips.
--    A change over a limit is answered by apply_changes as  status "rejected", reason "quota"  (the "error"
--    field says which limit); the rest of the batch still goes through. The app keeps the change and retries
--    later (after the user deleted something, or the next day).
-- 2. apply_changes per account: at most 60 calls and 3 000 changes per minute, else HTTP 429 (PT429).
--    (pull_changes only reads, so it isn't counted.)
-- 3. Storage, bucket "trip-files":
--      only image/jpeg, image/png, image/heic, image/heif, image/webp, application/pdf
--      per trip: 250 MB in total and 250 files; per account (all trips it owns): 500 MB.
--    Files count against the trip's OWNER, also when an editor uploaded them.
-- 4. Text limits: packing_templates.items <= 32 000 characters (the app's 200 items x 150 characters plus
--    line breaks fit), profiles.avatar_url <= 500 characters and https:// only.
--
-- Every number lives in private.quota_limit() - change it there and run this file again. Safe to run again.
-- =====================================================================================================

create or replace function private.quota_limit(p_key text) returns bigint
language sql
stable
set search_path = ''
as $$
    select case p_key
        -- rows per account
        when 'trips'                 then 200
        when 'packing_templates'     then 100
        -- rows per trip
        when 'expenses'              then 2000
        when 'places'                then 500
        when 'activities'            then 500
        when 'attachments'           then 200
        when 'participants'          then 200
        when 'todos'                 then 2000
        when 'expense_shares'        then 50000
        -- new rows per account per day
        when 'new_rows_per_day'      then 10000
        -- apply_changes per account per minute
        when 'calls_per_minute'      then 60
        when 'changes_per_minute'    then 3000
        -- Storage (bytes / files)
        when 'storage_bytes_per_trip'    then 250::bigint * 1024 * 1024
        when 'storage_files_per_trip'    then 250
        when 'storage_bytes_per_account' then 500::bigint * 1024 * 1024
    end;
$$;

-- ------------------------------------------------------------------------------------------- usage counters
-- One row per account: the current minute's apply_changes calls/changes and today's new rows.
create table if not exists private.sync_usage (
    user_id        uuid primary key references auth.users (id) on delete cascade,
    minute_start   timestamptz not null default date_trunc('minute', now()),
    minute_calls   int not null default 0,
    minute_changes int not null default 0,
    day            date not null default current_date,
    day_rows       int not null default 0
);
alter table private.sync_usage enable row level security;
revoke all on table private.sync_usage from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- 1. row limits
create or replace function private.enforce_row_quota() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_by      text;
    v_scope   uuid;
    v_limit   bigint := private.quota_limit(tg_table_name);
    v_live    bigint;
    v_total   bigint;
    v_today   int;
begin
    -- only new rows and rows coming back from the trash
    if tg_op = 'UPDATE' and not (old.deleted_at is not null and new.deleted_at is null) then
        return new;
    end if;

    if tg_table_name in ('trips', 'packing_templates') then
        v_by := 'owner_id';
        v_scope := new.owner_id;
    else
        v_by := 'trip_id';
        v_scope := new.trip_id;
    end if;

    if v_limit is not null and v_scope is not null then
        -- two calls of the same account can't both slip in under the limit
        perform pg_advisory_xact_lock(hashtextextended('tp-quota:' || tg_table_name || ':' || v_scope::text, 0));
        execute format('select count(*) filter (where deleted_at is null), count(*) from public.%I where %I = $1',
                       tg_table_name, v_by)
            into v_live, v_total using v_scope;
        if v_live >= v_limit or (tg_op = 'INSERT' and v_total >= 2 * v_limit) then
            raise exception 'quota: at most % % per %', v_limit, tg_table_name,
                            case v_by when 'owner_id' then 'account' else 'trip' end
                using errcode = 'TPQ01';
        end if;
    end if;

    -- new rows per account per day (server jobs have no uid and aren't counted)
    if tg_op = 'INSERT' and v_uid is not null then
        insert into private.sync_usage as u (user_id, day, day_rows)
        values (v_uid, current_date, 1)
        on conflict (user_id) do update
            set day_rows = case when u.day = current_date then u.day_rows + 1 else 1 end,
                day = current_date
        returning u.day_rows into v_today;
        if v_today > private.quota_limit('new_rows_per_day') then
            raise exception 'quota: at most % new rows per day', private.quota_limit('new_rows_per_day')
                using errcode = 'TPQ01';
        end if;
    end if;

    return new;
end;
$$;

do $$
declare t text;
begin
    foreach t in array array['trips', 'participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments', 'packing_templates'] loop
        execute format('drop trigger if exists enforce_row_quota on public.%I', t);
        execute format('create trigger enforce_row_quota before insert or update of deleted_at on public.%I '
                       'for each row execute function private.enforce_row_quota()', t);
    end loop;
end $$;

-- ------------------------------------------------------------------------------------------- 2. apply_changes
-- Same as migration 3, plus: the per-minute counter, and a limit breach answered as "quota" (not "invalid",
-- which the app retries at once).
create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_change  jsonb;
    v_results jsonb := '[]'::jsonb;
    v_minute  timestamptz := date_trunc('minute', now());
    v_count   int;
    v_calls   int;
    v_changes int;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    -- G7: a banned or deleted account is refused at once, even with a still-valid token (migration 8)
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    if jsonb_typeof(p_changes) <> 'array' then
        raise exception 'p_changes must be a JSON array' using errcode = '22023';
    end if;
    v_count := jsonb_array_length(p_changes);
    if v_count > 500 then
        raise exception 'at most 500 changes per call' using errcode = '22023';
    end if;

    -- TP-3: calls and changes per minute (a refused call rolls back and isn't counted)
    insert into private.sync_usage as u (user_id, minute_start, minute_calls, minute_changes)
    values (v_uid, v_minute, 1, v_count)
    on conflict (user_id) do update
        set minute_calls   = case when u.minute_start = v_minute then u.minute_calls + 1 else 1 end,
            minute_changes = case when u.minute_start = v_minute then u.minute_changes + v_count else v_count end,
            minute_start   = v_minute
    returning u.minute_calls, u.minute_changes into v_calls, v_changes;
    if v_calls > private.quota_limit('calls_per_minute') or v_changes > private.quota_limit('changes_per_minute') then
        raise exception 'too many sync requests, try again in a minute' using errcode = 'PT429';
    end if;

    for v_change in select value from jsonb_array_elements(p_changes) loop
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception
            when sqlstate 'TPQ01' then
                v_results := v_results || private.change_result(v_change, 'rejected', 'quota', p_error => sqlerrm);
            when others then
                -- constraint violations, bad values, a parent not uploaded yet ... - this change only
                v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => sqlerrm);
        end;
    end loop;

    return v_results;
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;

-- ------------------------------------------------------------------------------------------- 3. Storage
update storage.buckets
set allowed_mime_types = array['image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf']
where id = 'trip-files';

-- Size of a Storage object from its metadata (0 while unknown).
create or replace function private.object_size(p_metadata jsonb) returns bigint
language sql
immutable
set search_path = ''
as $$
    select case when p_metadata ->> 'size' ~ '^[0-9]{1,18}$' then (p_metadata ->> 'size')::bigint else 0 end;
$$;

-- May an object of this size be stored at p_name? (p_name itself is left out of the totals, so replacing a
-- file only counts its new size.) Storage checks the policy before the upload - the size isn't known then,
-- so a trip or account that is already full is refused at once - and again with the size when it stores it.
create or replace function private.storage_quota_allows(p_name text, p_metadata jsonb) returns boolean
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_trip  uuid := private.trip_id_from_path(p_name);
    v_size  bigint := greatest(private.object_size(p_metadata), 1);  -- unknown size: there must be room left
    v_owner uuid;
    v_bytes bigint;
    v_files bigint;
begin
    if v_trip is null then
        return false;
    end if;
    select t.owner_id into v_owner from public.trips t where t.id = v_trip;
    if v_owner is null then
        return false;
    end if;

    select coalesce(sum(private.object_size(o.metadata)), 0), count(*)
    into v_bytes, v_files
    from storage.objects o
    where o.bucket_id = 'trip-files'
      and private.trip_id_from_path(o.name) = v_trip
      and o.name <> p_name;
    if v_bytes + v_size > private.quota_limit('storage_bytes_per_trip')
       or v_files + 1 > private.quota_limit('storage_files_per_trip') then
        return false;
    end if;

    select coalesce(sum(private.object_size(o.metadata)), 0)
    into v_bytes
    from storage.objects o
    join public.trips t on t.id = private.trip_id_from_path(o.name)
    where o.bucket_id = 'trip-files'
      and t.owner_id = v_owner
      and o.name <> p_name;
    return v_bytes + v_size <= private.quota_limit('storage_bytes_per_account');
end;
$$;

revoke all on function private.object_size(jsonb) from public, anon;
revoke all on function private.storage_quota_allows(text, jsonb) from public, anon;
grant execute on function private.object_size(jsonb) to authenticated;
grant execute on function private.storage_quota_allows(text, jsonb) to authenticated;

drop policy if exists "trip-files: editors upload" on storage.objects;
create policy "trip-files: editors upload" on storage.objects
    for insert to authenticated
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor'])
                and private.storage_quota_allows(name, metadata));

drop policy if exists "trip-files: editors replace" on storage.objects;
create policy "trip-files: editors replace" on storage.objects
    for update to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']))
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor'])
                and private.storage_quota_allows(name, metadata));

-- ------------------------------------------------------------------------------------------- 4. text limits
create or replace function pg_temp.add_limit(p_table text, p_name text, p_check text) returns void
language plpgsql
as $$
begin
    if not exists (select 1 from pg_constraint
                   where conname = p_name and conrelid = format('public.%I', p_table)::regclass) then
        execute format('alter table public.%I add constraint %I check (%s) not valid', p_table, p_name, p_check);
    end if;
    begin
        execute format('alter table public.%I validate constraint %I', p_table, p_name);
    exception when check_violation then
        raise notice 'TP-3: existing rows of public.% break %, it stays NOT VALID (new writes are still checked)', p_table, p_name;
    end;
end;
$$;

select pg_temp.add_limit('packing_templates', 'packing_templates_items_length', 'char_length(items) <= 32000');
select pg_temp.add_limit('profiles', 'profiles_avatar_url_https',
                         'avatar_url is null or (char_length(avatar_url) <= 500 and avatar_url like ''https://%'')');

-- The profile made at sign-up takes the provider's picture only when it passes that check, so an odd
-- avatar URL from a provider can never block creating the account.
create or replace function private.create_profile() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare v_avatar text := new.raw_user_meta_data ->> 'avatar_url';
begin
    if v_avatar is not null and (char_length(v_avatar) > 500 or v_avatar not like 'https://%') then
        v_avatar := null;
    end if;
    insert into public.profiles (id, email, display_name, avatar_url)
    values (
        new.id,
        new.email,
        left(coalesce(new.raw_user_meta_data ->> 'full_name', new.raw_user_meta_data ->> 'name', split_part(new.email, '@', 1)), 80),
        v_avatar)
    on conflict (id) do nothing;
    return new;
end;
$$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000014_invite_hardening.sql
-- =====================================================================================================
-- TravelPlanner - TP-4 / 14: invite codes can no longer be passed around, and members' e-mails stay private.
--
-- 1. create_invite: an invite is single-use and valid for 48 hours by default. A code for a group has to be
--    asked for on purpose (p_max_uses up to 10, still 48 hours). Whoever joins becomes a VIEWER; the owner
--    makes them an editor afterwards (set_member_role). Older app versions still call
--    create_invite(trip, 'editor', 168, null): that now gives the same single-use 48-hour viewer invite.
--    Invites that were already out are cut down to the same rules.
-- 2. accept_invite: at most 10 wrong codes per account per hour, then that account is blocked for an hour
--    (HTTP 429, PT429). A wrong code returns no rows instead of an error - an error would roll the counter
--    back. (Old and new apps both read "no rows" as "invalid code".) Someone who is already a member gets
--    their trip back without using the code up (e.g. a retry after the first answer was lost).
-- 3. E-mail addresses: get_trip_members returns them only to the trip's owner (and each member's own);
--    editors and viewers see names and photos. Reading public.profiles directly no longer includes the
--    email column for anyone (column privileges); the app takes the user's own address from the session.
-- 4. get_recent_joins(since): the owner's app asks who joined their trips, to show "Ayşe joined Roma".
--
-- Every number lives in private.invite_limit(). Safe to run again.
-- =====================================================================================================

create or replace function private.invite_limit(p_key text) returns int
language sql
immutable
set search_path = ''
as $$
    select case p_key
        when 'valid_hours'        then 48   -- an invite works for at most this long
        when 'group_max_uses'     then 10   -- the most people one (group) code can bring in
        when 'failed_attempts'    then 10   -- wrong codes per account ...
        when 'attempt_window_min' then 60   -- ... within this many minutes ...
        when 'block_minutes'      then 60   -- ... block the account for this long
        when 'join_notice_days'   then 7    -- get_recent_joins looks back at most this far
    end;
$$;

-- ------------------------------------------------------------------------------------------- 1. create_invite
-- Same signature and answer as before (older app versions keep working); only the rules changed.
create or replace function public.create_invite(
    p_trip_id uuid, p_role text default 'viewer', p_valid_hours int default 48, p_max_uses int default 1)
returns table (code text, expires_at timestamptz)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_owner(p_trip_id);
    v_code  text;
    v_hours int;
    v_uses  int := coalesce(p_max_uses, 1);
begin
    if p_role is null or p_role not in ('editor', 'viewer') then
        raise exception 'role must be editor or viewer' using errcode = '22023';
    end if;
    if p_valid_hours is null or p_valid_hours < 1 then
        raise exception 'valid_hours must be at least 1' using errcode = '22023';
    end if;
    if v_uses < 1 or v_uses > private.invite_limit('group_max_uses') then
        raise exception 'max_uses must be between 1 and %', private.invite_limit('group_max_uses') using errcode = '22023';
    end if;
    if exists (select 1 from public.trips t where t.id = p_trip_id and t.deleted_at is not null) then
        raise exception 'trip is deleted' using errcode = '22023';
    end if;
    -- longer requests (older apps ask for 7 days) are cut down instead of refused
    v_hours := least(p_valid_hours, private.invite_limit('valid_hours'));

    loop
        v_code := private.new_invite_code();
        exit when not exists (select 1 from public.trip_invites i where i.code = v_code);
    end loop;

    -- everyone joins as a viewer; editing rights are given by the owner afterwards
    return query
    insert into public.trip_invites (trip_id, code, role, created_by, expires_at, max_uses)
    values (p_trip_id, v_code, 'viewer', v_uid, now() + make_interval(hours => v_hours), v_uses)
    returning trip_invites.code, trip_invites.expires_at;
end;
$$;

-- Invites already handed out under the old rules (unlimited, 7 days, editor): one more join at most, as a
-- viewer, within 48 hours from now. Invites that already follow the rules are left alone (safe to re-run).
update public.trip_invites i
set role       = 'viewer',
    max_uses   = least(coalesce(i.max_uses, i.used_count + 1), i.used_count + 1),
    expires_at = least(i.expires_at, now() + make_interval(hours => private.invite_limit('valid_hours')))
where i.revoked_at is null
  and i.expires_at > now()
  and (i.max_uses is null
       or i.max_uses > private.invite_limit('group_max_uses')
       or i.role <> 'viewer'
       or i.expires_at > now() + make_interval(hours => private.invite_limit('valid_hours')));

-- ------------------------------------------------------------------------------------------- 2. accept_invite
-- Wrong codes per account. Not reachable through the API.
create table if not exists private.invite_attempts (
    user_id       uuid primary key references auth.users (id) on delete cascade,
    window_start  timestamptz not null default now(),
    failures      int not null default 0,
    blocked_until timestamptz
);
alter table private.invite_attempts enable row level security;
revoke all on table private.invite_attempts from public, anon, authenticated;

-- Counts one wrong code; the attempt that reaches the limit blocks the account.
create or replace function private.note_failed_invite(p_uid uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_window   interval := make_interval(mins => private.invite_limit('attempt_window_min'));
    v_failures int;
begin
    insert into private.invite_attempts as a (user_id, window_start, failures)
    values (p_uid, now(), 1)
    on conflict (user_id) do update
        set failures     = case when a.window_start > now() - v_window then a.failures + 1 else 1 end,
            window_start = case when a.window_start > now() - v_window then a.window_start else now() end
    returning a.failures into v_failures;

    if v_failures >= private.invite_limit('failed_attempts') then
        update private.invite_attempts
        set blocked_until = now() + make_interval(mins => private.invite_limit('block_minutes')),
            failures      = 0,
            window_start  = now()
        where user_id = p_uid;
    end if;
end;
$$;

create or replace function public.accept_invite(p_code text)
returns table (trip_id uuid, role text, trip_name text)
language plpgsql
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid    uuid := private.require_uid();
    v_code   text := upper(regexp_replace(coalesce(p_code, ''), '[^A-Za-z0-9]', '', 'g'));
    v_invite public.trip_invites;
    v_role   text;
begin
    if exists (select 1 from private.invite_attempts a where a.user_id = v_uid and a.blocked_until > now()) then
        raise exception 'too many wrong invite codes, try again later' using errcode = 'PT429';
    end if;

    select * into v_invite from public.trip_invites i where i.code = v_code for update;

    -- Already a member (the owner typing their own code, or a retry after a lost answer): nothing is used up.
    if v_invite.id is not null and v_invite.revoked_at is null then
        select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
        if v_role is not null then
            return query select v_invite.trip_id, v_role, t.name from public.trips t where t.id = v_invite.trip_id;
            return;
        end if;
    end if;

    if v_invite.id is null or v_invite.revoked_at is not null or v_invite.expires_at < now()
       or (v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses)
       or exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        perform private.note_failed_invite(v_uid);
        return;   -- no rows = invalid / expired / used up (raising here would undo the counter)
    end if;

    insert into public.trip_members (trip_id, user_id, role, invited_by)
    values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
    update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;

    return query select v_invite.trip_id, v_invite.role, t.name from public.trips t where t.id = v_invite.trip_id;
end;
$$;

-- ------------------------------------------------------------------------------------------- 3. e-mails
create or replace function public.get_trip_members(p_trip_id uuid)
returns table (user_id uuid, role text, display_name text, email text, avatar_url text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_uid();
    v_owner boolean;
begin
    if not private.has_trip_role(p_trip_id, array['owner', 'editor', 'viewer']) then
        raise exception 'not a member of this trip' using errcode = '42501';
    end if;
    v_owner := private.has_trip_role(p_trip_id, array['owner']);

    return query
    select m.user_id, m.role, p.display_name,
           case when v_owner or m.user_id = v_uid then p.email end,   -- TP-4: only the owner sees e-mails
           p.avatar_url, m.joined_at
    from public.trip_members m
    left join public.profiles p on p.id = m.user_id
    where m.trip_id = p_trip_id
    order by case m.role when 'owner' then 0 when 'editor' then 1 else 2 end, m.joined_at;
end;
$$;

-- Trip mates still read each other's name and photo, but no longer the e-mail column (nor does anyone
-- else, through the API: the app reads the user's own address from the session).
revoke select on public.profiles from authenticated;
grant select (id, display_name, avatar_url, created_at, updated_at) on public.profiles to authenticated;

-- ------------------------------------------------------------------------------------------- 4. new members
-- People who joined the caller's own trips after p_since (at most join_notice_days back, at most 20).
create or replace function public.get_recent_joins(p_since timestamptz)
returns table (trip_id uuid, trip_name text, user_id uuid, display_name text, role text, joined_at timestamptz)
language plpgsql
stable
security definer
set search_path = ''
as $$
#variable_conflict use_column
declare
    v_uid   uuid := private.require_uid();
    v_floor timestamptz := now() - make_interval(days => private.invite_limit('join_notice_days'));
begin
    return query
    select m.trip_id, t.name, m.user_id, p.display_name, m.role, m.joined_at
    from public.trip_members mine
    join public.trips t on t.id = mine.trip_id and t.deleted_at is null
    join public.trip_members m on m.trip_id = mine.trip_id and m.user_id <> v_uid
    left join public.profiles p on p.id = m.user_id
    where mine.user_id = v_uid
      and mine.role = 'owner'
      and m.joined_at > greatest(coalesce(p_since, v_floor), v_floor)
    order by m.joined_at
    limit 20;
end;
$$;

-- ------------------------------------------------------------------------------------------- grants
do $$
declare f text;
begin
    foreach f in array array[
        'public.create_invite(uuid, text, int, int)',
        'public.accept_invite(text)',
        'public.get_trip_members(uuid)',
        'public.get_recent_joins(timestamptz)'] loop
        execute format('revoke all on function %s from public, anon', f);
        execute format('grant execute on function %s to authenticated', f);
    end loop;
end $$;

revoke all on function private.invite_limit(text) from public, anon, authenticated;
revoke all on function private.note_failed_invite(uuid) from public, anon, authenticated;

-- PostgREST: new function get_recent_joins.
notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000015_attachment_paths.sql
-- =====================================================================================================
-- TravelPlanner - TP-5 / 15: an attachment row can only point at its OWN file, and only as a photo or a PDF.
--
-- storage_path and content_type are columns a client may write (private.writable_columns). Without a check an
-- editor calling the REST API directly could
--   * point the row of someone else's ticket at a fake PDF he uploaded ("storage_path" of another row/trip),
--   * put "..", "?" or "#" into storage_path, so the other members' download request goes somewhere else,
--   * set content_type to application/vnd.android.package-archive, so opening the "ticket" starts the
--     Android package installer.
-- From now on:
--   storage_path   null, or exactly  trips/<trip_id>/<id>.<ext>  (this row's own trip and id; ext = 1-10 of a-z 0-9)
--   content_type   null, or one of   image/jpeg, image/png, image/heic, image/heif, image/webp, application/pdf
--                  (the "trip-files" bucket's allowed_mime_types, migration 13)
-- apply_changes answers a change that breaks either rule with  status "rejected", reason "invalid"  (a check
-- violation); the rest of the batch still goes through.
--
-- Rows stored before this migration are fixed first, so editing them later isn't refused: a content type
-- outside the list becomes null (the app derives the type from the file's extension anyway), a storage_path
-- that isn't the row's own key becomes null (the uploading device puts the right one back). Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- old rows
update public.attachments
set content_type = case lower(btrim(split_part(content_type, ';', 1)))
                       when 'image/jpg'   then 'image/jpeg'
                       when 'image/pjpeg' then 'image/jpeg'
                       else lower(btrim(split_part(content_type, ';', 1)))
                   end
where content_type is not null
  and content_type not in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf');

update public.attachments
set content_type = null
where content_type is not null
  and content_type not in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf');

update public.attachments
set storage_path = null
where storage_path is not null
  and storage_path !~ ('^trips/' || trip_id::text || '/' || id::text || '\.[a-z0-9]{1,10}$');

-- ------------------------------------------------------------------------------------------- the rules
-- (uuid::text is always lower case hex and "-", so it is safe inside the pattern)
alter table public.attachments drop constraint if exists attachments_storage_path_format;
alter table public.attachments add constraint attachments_storage_path_format check (
    storage_path is null
    or storage_path ~ ('^trips/' || trip_id::text || '/' || id::text || '\.[a-z0-9]{1,10}$'));

alter table public.attachments drop constraint if exists attachments_content_type_allowed;
alter table public.attachments add constraint attachments_content_type_allowed check (
    content_type is null
    or content_type in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf'));

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000016_file_cleanup.sql
-- =====================================================================================================
-- TravelPlanner - G7 / 16: files of deleted attachments are removed from Storage.
--
-- Until now an attachment's file stayed in the "trip-files" bucket for good once its row was gone: the nightly
-- purge (migration 6) removes rows soft-deleted 30+ days ago, and a trip's purge cascades to its attachments,
-- but nothing touched their files. The app removes them when its own trash is emptied (TrashService), but only
-- on the device that deleted them, only while signed in and online, and only if the account may still edit
-- the trip. Whatever slipped through kept using the 1 GB Storage quota and the trip owner's file limits
-- (migration 13).
--
-- Rows can't remove files themselves: Supabase refuses DELETE on storage.objects from SQL (and that would leave
-- the file's data behind anyway) - only the Storage API deletes a file. So:
--   1. private.file_cleanup_queue  paths whose file is no longer needed, filled by
--        * a trigger on public.attachments: a row deleted for good (the 30-day purge, a trip's purge or
--          delete_account cascading to it) or a storage_path that changes queues the old path - only the row's
--          OWN key trips/<trip_id>/<id>.<ext>, never another row's file;
--        * private.queue_orphan_files(), run by the nightly purge: files in trips/<trip_id>/<id>.<ext> with no
--          attachment row of that id in that trip (leftovers from before this migration, files of rows that
--          never stayed on the server), at least a day old so an upload in progress is never touched.
--      A soft-deleted attachment keeps its file - the app's trash can restore it for 30 days.
--   2. Edge Function "purge-files" (functions/purge-files), called every night by pg_cron after the purge:
--      takes a batch with public.file_cleanup_claim(), deletes it through the Storage API, confirms it with
--      public.file_cleanup_done(). Both are for the service role only.
-- A path that some attachment row points at again by the time it is claimed (restored from a device's trash) is
-- dropped from the queue and its file kept. Safe to run again.
-- =====================================================================================================

create table if not exists private.file_cleanup_queue (
    path       text primary key,
    queued_at  timestamptz not null default now(),
    claimed_at timestamptz,
    attempts   int not null default 0
);

-- ------------------------------------------------------------------------------------------- filling the queue
-- trips/<trip_id>/<id>.<ext> of this very row (same pattern as attachments_storage_path_format, migration 15)
create or replace function private.is_own_file(p_path text, p_trip uuid, p_id uuid) returns boolean
language sql
immutable
set search_path = ''
as $$
    select p_path is not null and p_path ~ ('^trips/' || p_trip::text || '/' || p_id::text || '\.[a-z0-9]{1,10}$')
$$;

create or replace function private.queue_old_attachment_file() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if (tg_op = 'DELETE' or new.storage_path is distinct from old.storage_path)
       and private.is_own_file(old.storage_path, old.trip_id, old.id) then
        insert into private.file_cleanup_queue (path) values (old.storage_path)
        on conflict (path) do nothing;
    end if;
    return null;
end;
$$;

drop trigger if exists attachments_queue_old_file on public.attachments;
create trigger attachments_queue_old_file
    after delete or update of storage_path on public.attachments
    for each row execute function private.queue_old_attachment_file();

-- Files no attachment row (live or in the trash) belongs to. Returns how many were queued.
create or replace function private.queue_orphan_files() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare v_count int;
begin
    insert into private.file_cleanup_queue (path)
    select o.name
    from storage.objects o
    where o.bucket_id = 'trip-files'
      and o.name ~* '^trips/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[a-z0-9]{1,10}$'
      and o.created_at < now() - interval '1 day'
      and not exists (
          select 1 from public.attachments a
          where a.trip_id = split_part(o.name, '/', 2)::uuid
            and a.id = split_part(split_part(o.name, '/', 3), '.', 1)::uuid)
      and not exists (select 1 from public.attachments a where a.storage_path = o.name)
    on conflict (path) do nothing;
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;

-- Migration 6's nightly purge, now also looking for orphaned files.
create or replace function private.purge_deleted_rows() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_cutoff timestamptz := now() - interval '30 days';
    t text;
begin
    -- children first; trips last (their delete cascades to whatever is left)
    foreach t in array array['expense_shares', 'expenses', 'todos', 'activities', 'places', 'participants', 'attachments', 'packing_templates', 'trips'] loop
        execute format('delete from public.%I where deleted_at < $1', t) using v_cutoff;
    end loop;

    delete from public.trip_invites
    where coalesce(revoked_at, expires_at) < v_cutoff;

    perform private.queue_orphan_files();
end;
$$;

revoke all on function private.is_own_file(text, uuid, uuid) from public, anon, authenticated;
revoke all on function private.queue_old_attachment_file() from public, anon, authenticated;
revoke all on function private.queue_orphan_files() from public, anon, authenticated;
revoke all on function private.purge_deleted_rows() from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- emptying the queue
-- Up to p_limit paths to delete now. A claimed path is not handed out again for 15 minutes (two runs at once
-- don't delete the same batch twice); one that failed 10 times stays in the queue for a look by hand.
create or replace function public.file_cleanup_claim(p_limit int default 500) returns text[]
language plpgsql
security definer
set search_path = ''
as $$
declare v_paths text[];
begin
    -- needed again (a restored row points at it): keep the file
    delete from private.file_cleanup_queue q
    where exists (select 1 from public.attachments a where a.storage_path = q.path);

    with picked as (
        select q.path
        from private.file_cleanup_queue q
        where (q.claimed_at is null or q.claimed_at < now() - interval '15 minutes')
          and q.attempts < 10
        order by q.queued_at, q.path
        limit greatest(1, least(coalesce(p_limit, 500), 1000))
        for update skip locked
    ), claimed as (
        update private.file_cleanup_queue q
        set claimed_at = now(), attempts = q.attempts + 1
        from picked
        where q.path = picked.path
        returning q.path
    )
    select coalesce(array_agg(c.path order by c.path), '{}') into v_paths from claimed c;
    return v_paths;
end;
$$;

-- The Storage API deleted these (or they were already gone).
create or replace function public.file_cleanup_done(p_paths text[]) returns int
language plpgsql
security definer
set search_path = ''
as $$
declare v_count int;
begin
    delete from private.file_cleanup_queue where path = any (coalesce(p_paths, '{}'));
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;

revoke all on function public.file_cleanup_claim(int) from public, anon, authenticated;
revoke all on function public.file_cleanup_done(text[]) from public, anon, authenticated;
do $$
begin
    if exists (select 1 from pg_roles where rolname = 'service_role') then
        grant execute on function public.file_cleanup_claim(int) to service_role;
        grant execute on function public.file_cleanup_done(text[]) to service_role;
    end if;
end $$;

-- ------------------------------------------------------------------------------------------- the nightly call
-- 30 minutes after the purge (03:17, migration 6). Needs pg_cron and pg_net (Database -> Extensions) and the
-- Vault secret "file_cleanup_secret" - the same value as the Edge Function's secret FILE_CLEANUP_SECRET
-- (README: "Silinen dosyaların temizliği"). Without them nothing is scheduled; the queue just waits.
do $$
begin
    create extension if not exists pg_net;
exception when others then
    raise notice 'pg_net could not be enabled here (%). Enable it in Database -> Extensions and re-run this file.', sqlerrm;
end $$;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron')
       and exists (select 1 from pg_extension where extname = 'pg_net') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-files';
        perform cron.schedule('travelplanner-purge-files', '47 3 * * *', $job$
            select net.http_post(
                url := 'https://bobliztrprgqhnudnhrs.supabase.co/functions/v1/purge-files',
                headers := jsonb_build_object(
                    'Content-Type', 'application/json',
                    'Authorization', 'Bearer ' || coalesce(
                        (select decrypted_secret from vault.decrypted_secrets where name = 'file_cleanup_secret'), '')),
                body := '{}'::jsonb,
                timeout_milliseconds := 120000)
        $job$);
    else
        raise notice 'pg_cron / pg_net missing: the nightly file clean-up is not scheduled.';
    end if;
end $$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000017_account_file_cleanup.sql
-- =====================================================================================================
-- TravelPlanner - TP-7 / 17: an account's files are removed from Storage even if the app's clean-up fails.
--
-- Deleting an account (migration 7) was two separate steps: the app listed and deleted the files of the trips
-- the account owns through the Storage API, then called delete_account. A connection lost in between, or a
-- file uploaded while the list was being read, left files nobody could reach any more - a passport photo kept
-- after "your data is deleted" (KVKK, Google Play's account deletion policy). Migration 16's orphan scan only
-- knows the attachment key trips/<trip_id>/<id>.<ext>; any other object of a trip that is gone stayed for good.
-- From now on:
--   * delete_account itself queues every object in the folders of the trips it deletes (private.file_cleanup_queue,
--     migration 16) - whatever its name, however recently uploaded. The Edge Function "purge-files" deletes them
--     the same night. The app still deletes them first; this is the safety net, not a replacement.
--   * the nightly orphan scan also queues objects, more than a day old, under trips/<trip_id>/ of a trip that
--     no longer exists at all (a trip in the trash still exists and keeps its files).
-- Files the account uploaded to OTHER people's trips stay: those trips still use them (only their owner is cleared).
-- Safe to run again.
-- =====================================================================================================

create or replace function public.delete_account() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    begin
        update storage.objects set owner = null, owner_id = null
        where owner = v_uid or owner_id = v_uid::text;
    exception when undefined_column then
        -- older Storage schema without owner_id
        update storage.objects set owner = null where owner = v_uid;
    end;

    -- every file of the trips about to go, in the same transaction: either the account and its trips are
    -- deleted and the files queued, or nothing happens
    insert into private.file_cleanup_queue (path)
    select o.name
    from storage.objects o
    join public.trips t on t.id = private.trip_id_from_path(o.name)
    where o.bucket_id = 'trip-files'
      and t.owner_id = v_uid
    on conflict (path) do nothing;

    delete from public.trips where owner_id = v_uid;   -- cascades to content, members, invites (attachments queue their files, migration 16)
    delete from auth.users where id = v_uid;           -- cascades to profile, memberships, templates
end;
$$;

revoke all on function public.delete_account() from public, anon;
grant execute on function public.delete_account() to authenticated;

-- Migration 16's scan, plus: everything in the folder of a trip that no longer exists.
create or replace function private.queue_orphan_files() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare v_count int;
begin
    insert into private.file_cleanup_queue (path)
    select o.name
    from storage.objects o
    where o.bucket_id = 'trip-files'
      and o.created_at < now() - interval '1 day'
      and not exists (select 1 from public.attachments a where a.storage_path = o.name)
      and (
          -- an attachment key whose row is gone
          (o.name ~* '^trips/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[a-z0-9]{1,10}$'
           and not exists (
               select 1 from public.attachments a
               where a.trip_id = split_part(o.name, '/', 2)::uuid
                 and a.id = split_part(split_part(o.name, '/', 3), '.', 1)::uuid))
          -- anything in the folder of a trip that is gone (purged, or its owner's account deleted)
          or (private.trip_id_from_path(o.name) is not null
              and not exists (select 1 from public.trips t where t.id = private.trip_id_from_path(o.name)))
      )
    on conflict (path) do nothing;
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;

revoke all on function private.queue_orphan_files() from public, anon, authenticated;
