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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261017000018_pull_cursor_safety.sql
-- =====================================================================================================
-- TravelPlanner - TP-8 / 18: the pull cursor never skips a row written by a slow, still-running transaction.
--
-- pull_changes (migration 3) moved the cursor to the largest change_seq among rows whose updated_at was more
-- than 10 seconds old. But updated_at was the writing transaction's START time (now()), while change_seq is
-- drawn when the row is written, and the row stays invisible until the transaction commits. An apply_changes
-- call that waited on row locks for more than 10 seconds could hold a row with a low change_seq and an old
-- updated_at; meanwhile someone else's higher change_seq committed, a reader's cursor moved past it, and when
-- the slow call finally committed, its row was never sent to that reader (until the row changed again).
-- Example: two people sync a lot at once (one restores a backup: 500 rows); a third member misses some edits.
-- From now on:
--   * updated_at is the time the row is written (clock_timestamp(), taken right after its change_seq), so
--     "older than the safe point" really means "its change_seq was drawn before the safe point".
--   * the safe point (private.pull_safe_before) is 10 seconds ago, or the start of the oldest transaction
--     that is still writing, whichever is earlier: no transaction that could still commit a lower change_seq
--     is passed over. Transactions running for more than 10 minutes are ignored, so a stuck session can't
--     freeze everyone's cursor.
--   * apply_changes waits at most 2 seconds for a row lock (that change comes back "invalid" and the app
--     sends it again) and stops a call after 6 seconds (the whole call fails and the app tries again), so even
--     where the transaction list can't be read, no write stays open longer than the 10-second window.
-- The app now sends at most 50 changes per call and reads everything again (p_since = 0) once a day.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. write time
create or replace function private.touch_synced_row() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.change_seq := nextval('public.sync_change_seq');
    -- TP-8: the write time, not the transaction's start - pull_changes relies on it (private.pull_safe_before)
    new.updated_at := clock_timestamp();
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
    new.updated_at := clock_timestamp(); -- TP-8: see touch_synced_row
    return new;
end;
$$;

-- ------------------------------------------------------------------------------------------- 2. safe point
-- Rows written before this moment can be passed by the cursor: every transaction that could still commit a
-- lower change_seq started after it. pg_stat_activity shows other sessions' transactions only to roles allowed
-- to see them; where it shows nothing, the 10 seconds and apply_changes' time limit still cover sync writes.
create or replace function private.pull_safe_before() returns timestamptz
language sql
volatile
security definer
set search_path = ''
as $$
    select least(
        now() - interval '10 seconds',
        (select min(a.xact_start)
         from pg_catalog.pg_stat_activity a
         where a.backend_xid is not null                         -- has written something
           and a.pid <> pg_catalog.pg_backend_pid()
           and a.datname = pg_catalog.current_database()
           and a.xact_start > now() - interval '10 minutes'));   -- a stuck session doesn't freeze every cursor
$$;

revoke all on function private.pull_safe_before() from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- 3. apply_changes
-- Same as migration 13, plus: at most 2 seconds waiting for a row lock, at most 6 seconds per call.
create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
set lock_timeout = '2s'
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
        -- TP-8: no sync write stays open as long as pull_changes' 10-second window; the whole call rolls back
        -- and the app sends it again (query_canceled is not caught by "others" below)
        if clock_timestamp() - now() > interval '6 seconds' then
            raise exception 'sync call took too long, try again' using errcode = '57014';
        end if;
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception
            when sqlstate 'TPQ01' then
                v_results := v_results || private.change_result(v_change, 'rejected', 'quota', p_error => sqlerrm);
            when others then
                -- constraint violations, bad values, a parent not uploaded yet, a row locked for 2 s ... - this change only
                v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => sqlerrm);
        end;
    end loop;

    return v_results;
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;

-- ------------------------------------------------------------------------------------------- 4. pull_changes
-- Same as migration 3, with the safe point from private.pull_safe_before().
create or replace function public.pull_changes(p_since bigint default 0) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := (select auth.uid());
    v_since  bigint := greatest(coalesce(p_since, 0), 0);
    v_safe   timestamptz := private.pull_safe_before();
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

revoke all on function public.pull_changes(bigint) from public, anon;
grant execute on function public.pull_changes(bigint) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000019_trip_currency.sql
-- =====================================================================================================
-- TravelPlanner - TP-11 / 19: changing a shared trip's currency keeps every expense's amount and currency in step.
--
-- Until now the app converted the expenses locally and synced each amount and the trip's currency_code as
-- separate field edits. A member who added an expense offline in the old currency ended up with an amount in
-- the old currency under the new currency's label (1 500 TRY shown as 1 500 EUR), and two members converting at
-- the same time could leave the amounts in one currency and the label in another.
-- From now on:
--   * expenses.amount_currency_code: the currency amount_minor is in (the trip's currency when it was written).
--   * public.convert_trip_currency(trip, from, to, rate): converts the trip and all its expenses in ONE
--     transaction; owner/editor only; refused (TPC01) when the trip is no longer in "from" - someone else
--     converted it first. Every conversion is logged in private.trip_currency_changes.
--   * an expense written in an older currency of its trip (amount_currency_code differs from the trip's, e.g. added
--     offline before the conversion arrived) is converted on arrival with the logged rate(s).
--   * a trip's currency_code can't be changed through apply_changes any more while it has expenses (that
--     change comes back "invalid"); the app calls convert_trip_currency for trips in the cloud.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. column, log
alter table public.expenses add column if not exists amount_currency_code text;
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'expenses_amount_currency_format') then
        alter table public.expenses add constraint expenses_amount_currency_format
            check (amount_currency_code is null or amount_currency_code ~ '^[A-Z]{3}$');
    end if;
end $$;

create table if not exists private.trip_currency_changes (
    id            bigserial primary key,
    trip_id       uuid not null references public.trips (id) on delete cascade,
    from_currency text not null,
    to_currency   text not null,
    rate          numeric not null,
    changed_by    uuid,
    changed_at    timestamptz not null default now()
);
create index if not exists trip_currency_changes_trip on private.trip_currency_changes (trip_id, id);
revoke all on private.trip_currency_changes from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- 2. one step
-- The app's rule (ExpenseService.ConvertTripExpensesAsync): what the user typed stays; the amount is
-- re-expressed in the new currency, or set back to the typed amount when the new currency is the typed one.
create or replace function private.convert_expense_amount(
    p_amount bigint, p_rate_so_far double precision, p_original_code text, p_original_amount bigint,
    p_from text, p_to text, p_rate numeric,
    out amount_minor bigint, out exchange_rate double precision, out original_currency_code text, out original_amount_minor bigint)
language plpgsql
immutable
set search_path = ''
as $$
declare
    v_rate double precision;
begin
    original_currency_code := coalesce(p_original_code, p_from);
    original_amount_minor := case when p_original_code is null then p_amount else p_original_amount end;
    if original_currency_code = p_to then
        amount_minor := original_amount_minor;
        exchange_rate := 1;
    else
        amount_minor := round(p_amount * p_rate);
        if amount_minor > 999999999999 then
            raise exception 'a converted expense exceeds the supported amount' using errcode = '22003';
        end if;
        v_rate := (case when p_rate_so_far is null or p_rate_so_far <= 0 then 1 else p_rate_so_far end) * p_rate::double precision;
        exchange_rate := case when v_rate between 0.000001 and 1000000 then v_rate else p_rate::double precision end;
    end if;
end;
$$;

-- ------------------------------------------------------------------------------------------- 3. the RPC
create or replace function public.convert_trip_currency(p_trip_id uuid, p_from text, p_to text, p_rate numeric)
returns jsonb
language plpgsql
security definer
set search_path = ''
set lock_timeout = '5s'
as $$
declare
    v_uid      uuid := (select auth.uid());
    v_current  text;
    v_count    int := 0;
    v_row      record;
    v_step     record;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    if p_from !~ '^[A-Z]{3}$' or p_to !~ '^[A-Z]{3}$' or p_from = p_to then
        raise exception 'invalid currency' using errcode = '22023';
    end if;
    if p_rate is null or p_rate < 0.000001 or p_rate > 1000000 then
        raise exception 'invalid rate' using errcode = '22023';
    end if;

    -- The trip row lock serializes conversions: a second one waits here and then finds the new currency.
    select t.currency_code into v_current
    from public.trips t
    where t.id = p_trip_id and t.deleted_at is null
    for update;
    if not found then
        raise exception 'trip not found' using errcode = 'P0002';
    end if;
    if not private.has_trip_role(p_trip_id, array['owner', 'editor']) then
        raise exception 'only the owner or an editor can change the currency' using errcode = '42501';
    end if;
    -- (null = a trip from before multi-currency support: it is in whatever the app shows as "from")
    if v_current is not null and v_current <> p_from then
        raise exception 'the trip is in % now', v_current using errcode = 'TPC01';
    end if;

    perform set_config('tp.currency_rpc', 'on', true);

    for v_row in
        select e.id, e.amount_minor, e.exchange_rate, e.original_currency_code, e.original_amount_minor
        from public.expenses e
        where e.trip_id = p_trip_id and e.deleted_at is null and coalesce(e.amount_currency_code, p_from) = p_from
        for update
    loop
        select * into v_step from private.convert_expense_amount(
            v_row.amount_minor, v_row.exchange_rate, v_row.original_currency_code, v_row.original_amount_minor,
            p_from, p_to, p_rate);
        update public.expenses e
        set amount_minor = v_step.amount_minor,
            exchange_rate = v_step.exchange_rate,
            original_currency_code = v_step.original_currency_code,
            original_amount_minor = v_step.original_amount_minor,
            amount_currency_code = p_to,
            updated_by = v_uid
        where e.id = v_row.id;
        v_count := v_count + 1;
    end loop;

    update public.trips t set currency_code = p_to, updated_by = v_uid where t.id = p_trip_id;
    insert into private.trip_currency_changes (trip_id, from_currency, to_currency, rate, changed_by)
    values (p_trip_id, p_from, p_to, p_rate, v_uid);

    perform set_config('tp.currency_rpc', 'off', true);
    return jsonb_build_object('converted', v_count, 'currency', p_to);
end;
$$;

revoke all on function public.convert_trip_currency(uuid, text, text, numeric) from public, anon;
grant execute on function public.convert_trip_currency(uuid, text, text, numeric) to authenticated;

-- ------------------------------------------------------------------------------------------- 4. late expenses
-- An expense whose amount is in an older currency of its trip is brought to the trip's currency with the
-- logged rates: from the latest conversion out of its currency, then every conversion after that one.
create or replace function private.normalize_expense_currency() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_trip_currency text;
    v_first         record;
    v_step          record;
    v_result        record;
begin
    if new.amount_currency_code is null or coalesce(current_setting('tp.currency_rpc', true), '') = 'on' then
        return new;
    end if;

    select t.currency_code into v_trip_currency from public.trips t where t.id = new.trip_id;
    if v_trip_currency is null or v_trip_currency = new.amount_currency_code then
        return new;
    end if;

    select c.id, c.from_currency, c.to_currency, c.rate into v_first
    from private.trip_currency_changes c
    where c.trip_id = new.trip_id and c.from_currency = new.amount_currency_code
    order by c.id desc
    limit 1;
    if not found then
        return new; -- never a currency of this trip: leave it, the app shows it with its own currency
    end if;

    for v_step in
        select c.from_currency, c.to_currency, c.rate
        from private.trip_currency_changes c
        where c.trip_id = new.trip_id and c.id >= v_first.id
        order by c.id
    loop
        select * into v_result from private.convert_expense_amount(
            new.amount_minor, new.exchange_rate, new.original_currency_code, new.original_amount_minor,
            v_step.from_currency, v_step.to_currency, v_step.rate);
        new.amount_minor := v_result.amount_minor;
        new.exchange_rate := v_result.exchange_rate;
        new.original_currency_code := v_result.original_currency_code;
        new.original_amount_minor := v_result.original_amount_minor;
        new.amount_currency_code := v_step.to_currency;
    end loop;

    return new;
end;
$$;

drop trigger if exists expenses_normalize_currency on public.expenses;
create trigger expenses_normalize_currency
    before insert or update on public.expenses
    for each row execute function private.normalize_expense_currency();

-- ------------------------------------------------------------------------------------------- 5. guard
-- A trip with expenses changes currency only through convert_trip_currency. (Setting the code of a trip from
-- before multi-currency support - null - is no conversion and stays allowed.)
create or replace function private.guard_trip_currency() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if old.currency_code is not null
       and new.currency_code is distinct from old.currency_code
       and coalesce(current_setting('tp.currency_rpc', true), '') <> 'on'
       and exists (select 1 from public.expenses e where e.trip_id = new.id and e.deleted_at is null) then
        raise exception 'change the currency of a trip with expenses with convert_trip_currency' using errcode = '22023';
    end if;
    return new;
end;
$$;

drop trigger if exists trips_guard_currency on public.trips;
create trigger trips_guard_currency
    before update on public.trips
    for each row execute function private.guard_trip_currency();

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000020_ownership_transfer.sql
-- =====================================================================================================
-- TravelPlanner - TP-12 / 20: closing an account no longer silently deletes the trips other people share.
--
-- delete_account deletes every trip the account owns, with everything in it - for all members, without warning.
-- From now on the app asks, for each owned trip that has other members, whether to hand it over or to delete it
-- for everyone, and shows how many people it affects:
--   * public.get_owned_shared_trips(): the caller's own (not deleted) trips that have other members, with how
--     many.
--   * public.transfer_trip_ownership(trip, new_owner): owner only; the chosen member becomes the owner, the old
--     owner stays on as an editor. Trips handed over this way survive delete_account.
--   * keep_owner (migration 1) lets owner_id change only inside transfer_trip_ownership.
-- delete_account itself is unchanged: whatever the account still owns is deleted.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. keep_owner
create or replace function private.keep_owner() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    -- TP-12: ownership moves only through transfer_trip_ownership
    if coalesce(current_setting('tp.ownership_transfer', true), '') <> 'on' then
        new.owner_id := old.owner_id;
    end if;
    return new;
end;
$$;

-- ------------------------------------------------------------------------------------------- 2. the list
create or replace function public.get_owned_shared_trips()
returns table (trip_id uuid, name text, other_members int)
language plpgsql
stable
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_uid();
begin
    return query
    select t.id, t.name, count(m.user_id)::int
    from public.trips t
    join public.trip_members m on m.trip_id = t.id and m.user_id <> v_uid
    where t.owner_id = v_uid and t.deleted_at is null
    group by t.id, t.name
    order by t.name;
end;
$$;

revoke all on function public.get_owned_shared_trips() from public, anon;
grant execute on function public.get_owned_shared_trips() to authenticated;

-- ------------------------------------------------------------------------------------------- 3. the transfer
create or replace function public.transfer_trip_ownership(p_trip_id uuid, p_new_owner uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
declare v_uid uuid := private.require_owner(p_trip_id);
begin
    if p_new_owner is null or p_new_owner = v_uid then
        raise exception 'choose another member' using errcode = '22023';
    end if;

    perform 1 from public.trips t where t.id = p_trip_id and t.deleted_at is null for update;
    if not found then
        raise exception 'trip not found' using errcode = 'P0002';
    end if;
    if not exists (select 1 from public.trip_members m
                   where m.trip_id = p_trip_id and m.user_id = p_new_owner and m.role in ('editor', 'viewer')) then
        raise exception 'no such member' using errcode = 'P0002';
    end if;
    if private.account_state(p_new_owner) <> 'active' then
        raise exception 'that account can''t take over the trip' using errcode = '22023';
    end if;

    -- old owner first: there is exactly one owner per trip (trip_members_one_owner)
    update public.trip_members set role = 'editor' where trip_id = p_trip_id and user_id = v_uid;
    update public.trip_members set role = 'owner' where trip_id = p_trip_id and user_id = p_new_owner;

    perform set_config('tp.ownership_transfer', 'on', true);
    update public.trips set owner_id = p_new_owner, updated_by = v_uid where id = p_trip_id;
    perform set_config('tp.ownership_transfer', 'off', true);
end;
$$;

revoke all on function public.transfer_trip_ownership(uuid, uuid) from public, anon;
grant execute on function public.transfer_trip_ownership(uuid, uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000021_error_details.sql
-- =====================================================================================================
-- TravelPlanner - TP-14 / 21: less detail for clients - database errors and other people's account status.
--
--   * apply_changes answered a refused change with PostgreSQL's own message (table, column and constraint
--     names). It now answers with a short code (private.error_code) and writes the full message to the server
--     log (RAISE LOG: Dashboard -> Logs -> Postgres). Usage-limit messages (quota) are our own text and stay.
--       duplicate    unique / primary key violation           missing_parent  foreign key violation
--       out_of_range check constraint (InputLimits)           missing_value   not-null violation
--       bad_value    wrong type, format or number range       busy            row locked / statement timeout
--       error        anything else
--   * account_status is no longer open to anon: a signed-in user can ask only about their own account. Anyone
--     who knew a user id (trip members see them) could find out whether that account was deleted or banned.
--     An access token issued before a deletion or ban still works for up to an hour, which is enough for the
--     app's check; after that the token can't be refreshed and the app treats it as "session ended".
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. error codes
create or replace function private.error_code(p_sqlstate text) returns text
language sql
immutable
set search_path = ''
as $$
    select case
        when p_sqlstate = '23505' then 'duplicate'
        when p_sqlstate = '23503' then 'missing_parent'
        when p_sqlstate = '23514' then 'out_of_range'
        when p_sqlstate = '23502' then 'missing_value'
        when p_sqlstate in ('55P03', '57014', '40P01', '40001') then 'busy'
        when p_sqlstate like '22%' then 'bad_value'
        else 'error'
    end;
$$;

revoke all on function private.error_code(text) from public, anon, authenticated;
-- ------------------------------------------------------------------------------------------- 2. apply_changes
-- Same as migration 18; a refused change now carries a short code instead of the database message (TP-14).
create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
set lock_timeout = '2s'
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_change  jsonb;
    v_results jsonb := '[]'::jsonb;
    v_minute  timestamptz := date_trunc('minute', now());
    v_count   int;
    v_calls   int;
    v_changes int;
    v_state   text;
    v_message text;
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
        -- TP-8: no sync write stays open as long as pull_changes' 10-second window; the whole call rolls back
        -- and the app sends it again (query_canceled is not caught by "others" below)
        if clock_timestamp() - now() > interval '6 seconds' then
            raise exception 'sync call took too long, try again' using errcode = '57014';
        end if;
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception
            when sqlstate 'TPQ01' then
                v_results := v_results || private.change_result(v_change, 'rejected', 'quota', p_error => sqlerrm);
            when others then
                -- constraint violations, bad values, a parent not uploaded yet, a row locked for 2 s ... - this change only
                -- TP-14: the client gets a short code; the database message goes to the server log only
                v_state := sqlstate;
                v_message := sqlerrm;
                raise log 'apply_changes: % % rejected (%): %', v_change ->> 'entity', v_change ->> 'id', v_state, v_message;
                v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => private.error_code(v_state));
        end;
    end loop;

    return v_results;
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;

-- ------------------------------------------------------------------------------------------- 3. account_status
create or replace function public.account_status(p_user_id uuid) returns text
language plpgsql
stable
security definer
set search_path = ''
as $$
declare v_uid uuid := (select auth.uid());
begin
    -- (not require_uid: a deleted or banned account must still be able to learn that it is)
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if p_user_id is distinct from v_uid then
        raise exception 'only your own account' using errcode = '42501';
    end if;
    return private.account_state(v_uid);
end;
$$;

revoke all on function public.account_status(uuid) from public, anon;
grant execute on function public.account_status(uuid) to authenticated;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000022_signup_protection.sql
-- =====================================================================================================
-- TravelPlanner - TP-15 / 22: no accounts with throw-away e-mail addresses.
--
-- Bots could open hundreds of accounts with disposable addresses and use up the AI quota and the storage.
-- public.hook_before_user_created is a Supabase Auth "Before User Created" hook: it refuses a new account whose
-- e-mail domain (or a parent domain) is in private.blocked_email_domains. The message carries the marker
-- "disposable_email", which the app turns into its own explanation.
--
-- AFTER RUNNING THIS FILE (Dashboard, once):
--   * Authentication -> Hooks -> "Before User Created" -> Postgres -> public.hook_before_user_created -> Enable.
--   * Authentication -> Providers -> Email: "Confirm email" on; Password requirements: minimum length 8,
--     "Letters and digits" (the app asks for the same since TP-15).
--   * Authentication -> Attack Protection: CAPTCHA on, provider Cloudflare Turnstile, with the widget's secret key
--     (the app already sends Turnstile tokens - SupabaseConfig.CaptchaSiteKey).
-- Add a domain later:  insert into private.blocked_email_domains (domain) values ('example.org') on conflict do nothing;
-- Safe to run again.
-- =====================================================================================================

create table if not exists private.blocked_email_domains (
    domain text primary key check (domain = lower(domain) and domain !~ '\s')
);
revoke all on private.blocked_email_domains from public, anon, authenticated;

insert into private.blocked_email_domains (domain) values
    ('10minutemail.com'), ('10minutemail.net'), ('20minutemail.com'), ('33mail.com'), ('anonbox.net'),
    ('burnermail.io'), ('discard.email'), ('dispostable.com'), ('dropmail.me'), ('emailondeck.com'),
    ('fakeinbox.com'), ('fakemail.net'), ('getairmail.com'), ('getnada.com'), ('guerrillamail.biz'),
    ('guerrillamail.com'), ('guerrillamail.de'), ('guerrillamail.info'), ('guerrillamail.net'), ('guerrillamail.org'),
    ('guerrillamailblock.com'), ('harakirimail.com'), ('inboxkitten.com'), ('incognitomail.org'), ('jetable.org'),
    ('mail.tm'), ('mail-temp.com'), ('mailcatch.com'), ('maildrop.cc'), ('mailinator.com'),
    ('mailinator.net'), ('mailnesia.com'), ('mailsac.com'), ('mintemail.com'), ('mohmal.com'),
    ('moakt.com'), ('mytemp.email'), ('mytrashmail.com'), ('nada.email'), ('sharklasers.com'),
    ('spam4.me'), ('spambox.us'), ('spamgourmet.com'), ('temp-mail.io'), ('temp-mail.org'),
    ('tempail.com'), ('tempmail.com'), ('tempmail.net'), ('tempmail.plus'), ('tempmailo.com'),
    ('tempr.email'), ('throwawaymail.com'), ('trashmail.com'), ('trashmail.de'), ('trashmail.net'),
    ('yopmail.com'), ('yopmail.fr'), ('yopmail.net'), ('emailfake.com'), ('minuteinbox.com')
on conflict (domain) do nothing;

-- true when the address's domain or one of its parent domains is blocked (x.mailinator.com too)
create or replace function private.is_disposable_email(p_email text) returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    with d as (select lower(trim(split_part(coalesce(p_email, ''), '@', 2))) as domain)
    select exists (
        select 1
        from d, private.blocked_email_domains b
        where d.domain <> '' and (d.domain = b.domain or d.domain like '%.' || b.domain));
$$;

revoke all on function private.is_disposable_email(text) from public, anon, authenticated;

create or replace function public.hook_before_user_created(event jsonb) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
begin
    if private.is_disposable_email(event -> 'user' ->> 'email') then
        return jsonb_build_object('error', jsonb_build_object(
            'http_code', 400,
            'message', 'disposable_email: accounts cannot be created with throw-away e-mail addresses'));
    end if;
    return '{}'::jsonb;
end;
$$;

revoke all on function public.hook_before_user_created(jsonb) from public, anon, authenticated;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'supabase_auth_admin') then
        execute 'grant execute on function public.hook_before_user_created(jsonb) to supabase_auth_admin';
    end if;
end $$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000023_explore_catalog.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000024_explore_image_urls.sql
-- =====================================================================================================
-- TravelPlanner - TP-41 / 24: Keşfet images may also be external https URLs.
--
-- cover_path / thumb_path accepted only a file in the explore-images bucket (covers/<slug>.webp). They now also
-- accept a full https URL (e.g. a Wikimedia Commons thumbnail), so images do not have to be uploaded. The app uses
-- such a value as is; a bucket path still becomes the bucket's public URL. Content is written only by the admin.
-- Safe to run again.
-- =====================================================================================================

alter table public.explore_trips drop constraint if exists explore_trips_cover_path_check;
alter table public.explore_trips drop constraint if exists explore_trips_thumb_path_check;

alter table public.explore_trips add constraint explore_trips_cover_path_check
    check (cover_path ~ '^covers/[a-z0-9-]+\.(webp|jpg)$'
           or (cover_path ~ '^https://[^\s]+$' and char_length(cover_path) <= 1000));
alter table public.explore_trips add constraint explore_trips_thumb_path_check
    check (thumb_path ~ '^thumbs/[a-z0-9-]+\.(webp|jpg)$'
           or (thumb_path ~ '^https://[^\s]+$' and char_length(thumb_path) <= 1000));

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000025_admin_panel.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000026_admin_ai_devices.sql
-- =====================================================================================================
-- TravelPlanner - admin / 26: AI rights given in the admin panel really reach the user.
--
-- The daily AI quota is counted per account AND per device (migration 9): all accounts on one phone share the
-- phone's plans. So a higher limit or "reset today" from the panel (migration 25) did nothing when the phone's own
-- count was already at the limit - e.g. after testing with other (or since deleted) accounts on the same phone.
--
--   1. An account the admin gave a limit of its own (private.ai_user_limits) is no longer held back by the device
--      count: the admin decided for this account. Everyone else is counted as before.
--   2. The app asks ai_plan_quota_status with its device on every quota check, so the devices of an account are
--      remembered (private.ai_user_devices, kept 31 days). admin_reset_ai_today resets today's count of every
--      device the account used, not only the ones it planned on today.
--   3. The panel sees the highest device count of the account today (admin_user / admin_users: ai_device_used_today).
--
-- The global daily ceiling still applies to everyone. Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_user_devices (
    user_id   uuid not null references auth.users (id) on delete cascade,
    device    text not null check (device ~ '^[0-9a-f]{64}$'),
    last_seen date not null default current_date,
    primary key (user_id, device)
);
alter table private.ai_user_devices enable row level security;
revoke all on table private.ai_user_devices from public, anon, authenticated;

-- Remembers that this account uses this device (at most once a day per pair).
create or replace function private.ai_note_device(p_uid uuid, p_device text) returns void
language sql
security definer
set search_path = ''
as $$
    insert into private.ai_user_devices as d (user_id, device, last_seen) values (p_uid, p_device, current_date)
    on conflict (user_id, device) do update set last_seen = current_date
        where d.last_seen <> current_date;
$$;
revoke all on function private.ai_note_device(uuid, text) from public, anon, authenticated;

-- True when the admin set a limit of its own for the account.
create or replace function private.ai_has_own_limit(p_uid uuid) returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (select 1 from private.ai_user_limits where user_id = p_uid)
$$;
revoke all on function private.ai_has_own_limit(uuid) from public, anon, authenticated;

-- Same as migration 12, plus: notes the device, and an account with its own limit ignores the device count.
-- (Not "stable" any more: it writes the device note.)
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    perform private.ai_note_device(v_uid, v_device);
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    v_user := coalesce(v_user, 0);
    v_dev := case when v_own then 0 else coalesce(v_dev, 0) end;
    return jsonb_build_object(
        'limit', v_limit,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(v_user, v_dev), 0) end);
end;
$$;

-- Same as migration 12, plus: notes the device, and an account with its own limit ignores the device count
-- (the device is still counted, so other accounts on the phone keep sharing it).
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    perform private.ai_note_device(v_uid, v_device);

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

    if v_user >= v_limit or (not v_own and v_dev >= v_limit) then
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
    delete from private.ai_user_devices where last_seen < current_date - 31;

    return jsonb_build_object('limit', v_limit,
                              'remaining', v_limit - (case when v_own then v_user else greatest(v_user, v_dev) end) - 1,
                              'ticket', v_ticket);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;

-- ------------------------------------------------------------------------------------------- panel
-- Today's AI plans of the account start again, and of every device it used (today's tickets + noted devices).
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
       and (d.device in (select t.device from private.ai_quota_ticket t where t.user_id = p_user_id and t.day = current_date)
            or d.device in (select u.device from private.ai_user_devices u where u.user_id = p_user_id));
end;
$$;

-- One account as the panel shows it (migration 25, plus the highest device count of today).
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
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

revoke all on function public.admin_reset_ai_today(uuid) from public, anon, authenticated;
grant execute on function public.admin_reset_ai_today(uuid) to service_role;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261018000027_admin_accounts.sql
-- =====================================================================================================
-- TravelPlanner - admin / 27: account operations and catalog delete for the admin panel.
--
--   admin_ban_user(id, days|null)   bans the account (null = for good) and signs it out on every device; the app
--                                   signs out and says why (migration 8), Supabase Auth refuses new sign-ins
--   admin_unban_user(id)            lets it sign in again
--   admin_sign_out_user(id)         signs it out on every device (refresh tokens stop working; an access token
--                                   still works until it expires, at most an hour)
--   admin_delete_user(id)           deletes the account exactly as "delete my account" in the app does
--                                   (public.delete_account: owned trips and their files, profile, memberships)
--   admin_explore_delete(slug)      deletes a sample trip for good (users' copies are independent and stay)
--   admin_user / admin_users        now also return banned_until; admin_overview counts banned accounts
--
-- Like migration 25: service_role only. Safe to run again.
-- =====================================================================================================

create or replace function public.admin_ban_user(p_user_id uuid, p_days int default null) returns timestamptz
language plpgsql
security definer
set search_path = ''
as $$
declare v_until timestamptz;
begin
    if p_days is not null and p_days not between 1 and 3650 then
        raise exception 'days must be between 1 and 3650' using errcode = '22023';
    end if;
    v_until := now() + coalesce(make_interval(days => p_days), interval '100 years');

    update auth.users set banned_until = v_until where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
    return v_until;
end;
$$;

create or replace function public.admin_unban_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
end;
$$;

create or replace function public.admin_sign_out_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
end;
$$;

-- Runs public.delete_account as that account (auth.uid() reads request.jwt.claim.sub first), so there is one
-- deletion path. A banned account is unbanned first, because delete_account only works for active accounts.
create or replace function public.admin_delete_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform set_config('request.jwt.claim.sub', p_user_id::text, true);
    perform public.delete_account();
    perform set_config('request.jwt.claim.sub', '', true);
end;
$$;

create or replace function public.admin_explore_delete(p_slug text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    delete from public.explore_trips where slug = p_slug;
    if not found then
        raise exception 'no sample trip "%"', p_slug using errcode = 'P0002';
    end if;
end;
$$;

-- Start-page counts (migration 25, plus banned accounts).
create or replace function public.admin_overview() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'users',             (select count(*) from auth.users),
        'users_7d',          (select count(*) from auth.users where created_at > now() - interval '7 days'),
        'users_banned',      (select count(*) from auth.users where banned_until > now()),
        'trips',             (select count(*) from public.trips where deleted_at is null),
        'trips_7d',          (select count(*) from public.trips where deleted_at is null and created_at > now() - interval '7 days'),
        'ai_today',          coalesce((select count from private.ai_global_usage where day = current_date), 0),
        'ai_global_limit',   private.ai_global_daily_limit(),
        'ai_daily_limit',    private.ai_daily_limit(),
        'explore_published', (select count(*) from public.explore_trips where status = 'published'),
        'explore_draft',     (select count(*) from public.explore_trips where status = 'draft'),
        'explore_archived',  (select count(*) from public.explore_trips where status = 'archived'))
$$;

-- One account as the panel shows it (migration 26, plus banned_until).
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id), 'banned_until', u.banned_until,
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

do $$
declare f text;
begin
    foreach f in array array[
        'public.admin_ban_user(uuid, int)', 'public.admin_unban_user(uuid)', 'public.admin_sign_out_user(uuid)',
        'public.admin_delete_user(uuid)', 'public.admin_explore_delete(text)', 'public.admin_overview()'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261019000028_deleted_trip_invites.sql
-- =====================================================================================================
-- TravelPlanner - 28: invite codes of a deleted trip.
--
-- Until now:
--   * a trip moved to the trash kept its open invites. accept_invite refused them while the trip was in the trash,
--     but restoring the trip brought every code that hadn't expired back to life;
--   * a trip deleted for good (its owner closed the account) took its invites with it, so the code simply
--     didn't exist any more;
--   * either way the person typing the code only heard "invalid code", and it counted as a wrong guess (10 an
--     hour, then a block).
-- From now on:
--   1. moving a trip to the trash revokes its open invites (a restored trip needs a new code);
--   2. an invite that is still within its time when its trip is deleted for good leaves its code behind in
--      private.closed_invites until it would have expired;
--   3. accept_invite answers a code that exists but no longer works with one row without a trip_id and a reason:
--      'trip_deleted', 'revoked', 'expired' or 'used_up'. Such a code is not counted as a wrong guess (it was
--      not guessed). Older app versions read a row without trip_id as "invalid code", as before.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. trash
create or replace function private.revoke_invites_of_deleted_trip() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if new.deleted_at is not null and old.deleted_at is null then
        update public.trip_invites set revoked_at = now() where trip_id = new.id and revoked_at is null;
    end if;
    return null;
end;
$$;

drop trigger if exists trips_revoke_invites on public.trips;
create trigger trips_revoke_invites
    after update of deleted_at on public.trips
    for each row execute function private.revoke_invites_of_deleted_trip();

-- Invites already open on trips that are in the trash now.
update public.trip_invites i
set revoked_at = now()
from public.trips t
where t.id = i.trip_id and t.deleted_at is not null and i.revoked_at is null;

-- ------------------------------------------------------------------------------------------- 2. deleted for good
create table if not exists private.closed_invites (
    code       text primary key,
    expires_at timestamptz not null
);
alter table private.closed_invites enable row level security;
revoke all on table private.closed_invites from public, anon, authenticated;

create or replace function private.remember_closed_invite() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    -- only when the trip itself is gone (a cascade from public.trips), and the code could still be typed
    if old.expires_at > now() and not exists (select 1 from public.trips t where t.id = old.trip_id) then
        insert into private.closed_invites (code, expires_at) values (old.code, old.expires_at)
        on conflict (code) do update set expires_at = excluded.expires_at;
    end if;
    delete from private.closed_invites where expires_at < now();
    return null;
end;
$$;

drop trigger if exists trip_invites_remember_closed on public.trip_invites;
create trigger trip_invites_remember_closed
    after delete on public.trip_invites
    for each row execute function private.remember_closed_invite();

-- ------------------------------------------------------------------------------------------- 3. accept_invite
-- The answer gets a new column, so the function is dropped and created again (grants below).
drop function if exists public.accept_invite(text);

create function public.accept_invite(p_code text)
returns table (trip_id uuid, role text, trip_name text, reason text)
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
    v_reason text;
begin
    if exists (select 1 from private.invite_attempts a where a.user_id = v_uid and a.blocked_until > now()) then
        raise exception 'too many wrong invite codes, try again later' using errcode = 'PT429';
    end if;

    select * into v_invite from public.trip_invites i where i.code = v_code for update;

    -- Already a member (the owner typing their own code, or a retry after a lost answer): nothing is used up.
    if v_invite.id is not null and v_invite.revoked_at is null then
        select m.role into v_role from public.trip_members m where m.trip_id = v_invite.trip_id and m.user_id = v_uid;
        if v_role is not null then
            return query select v_invite.trip_id, v_role, t.name, null::text from public.trips t where t.id = v_invite.trip_id;
            return;
        end if;
    end if;

    if v_invite.id is null then
        if exists (select 1 from private.closed_invites c where c.code = v_code and c.expires_at > now()) then
            v_reason := 'trip_deleted';
        else
            perform private.note_failed_invite(v_uid);
            return;   -- no rows = a code that never existed (raising here would undo the counter)
        end if;
    elsif exists (select 1 from public.trips t where t.id = v_invite.trip_id and t.deleted_at is not null) then
        v_reason := 'trip_deleted';
    elsif v_invite.revoked_at is not null then
        v_reason := 'revoked';
    elsif v_invite.expires_at < now() then
        v_reason := 'expired';
    elsif v_invite.max_uses is not null and v_invite.used_count >= v_invite.max_uses then
        v_reason := 'used_up';
    end if;

    -- a real code that no longer works: say why, and don't count it as a guess
    if v_reason is not null then
        return query select null::uuid, null::text, null::text, v_reason;
        return;
    end if;

    insert into public.trip_members (trip_id, user_id, role, invited_by)
    values (v_invite.trip_id, v_uid, v_invite.role, v_invite.created_by);
    update public.trip_invites set used_count = used_count + 1 where id = v_invite.id;

    return query select v_invite.trip_id, v_invite.role, t.name, null::text from public.trips t where t.id = v_invite.trip_id;
end;
$$;

revoke all on function public.accept_invite(text) from public, anon;
grant execute on function public.accept_invite(text) to authenticated;

revoke all on function private.revoke_invites_of_deleted_trip() from public, anon, authenticated;
revoke all on function private.remember_closed_invite() from public, anon, authenticated;

-- PostgREST: accept_invite answers with a new column.
notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261020000029_app_settings.sql
-- =====================================================================================================
-- TravelPlanner - admin / 29: app settings managed from the admin panel.
--
-- Until now the AI limits were constants in SQL (private.ai_daily_limit = 5, ai_global_daily_limit = 200,
-- ai_new_account_limit = 2, ai_daily_refund_limit = 2) and changing one meant editing a migration. They now live
-- in one row, private.app_settings, together with the other things the panel manages:
--
--   ai_enabled / ai_paused_message   AI planning on, or paused for maintenance (with an optional message)
--   ai_*_limit                       the four AI limits above (same defaults)
--   announcement                     a short notice the app shows on its home page (empty = none)
--   min_app_version                  older app versions are asked to update (empty = nobody)
--   default_currency / _reminder_hour  defaults for users who haven't picked their own in Tercihler
--
--   public.app_config()              what the app reads (anon + signed in): everything above but the limits
--   public.admin_settings()          the whole row for the panel; admin_set_settings(json) changes it
--
-- The private.ai_*_limit functions keep their names and now read the row, so ai_plan_quota, admin_overview and
-- the rest use the new numbers without a change. When AI is paused ai_plan_quota answers HTTP 503 (SQLSTATE PT503;
-- plan-trip turns it into "ai_paused") and ai_plan_quota_status says paused. Safe to run again.
-- =====================================================================================================

create table if not exists private.app_settings (
    id                    boolean primary key default true check (id),
    ai_enabled            boolean not null default true,
    ai_paused_message     text check (char_length(ai_paused_message) <= 300),
    ai_daily_limit        int not null default 5 check (ai_daily_limit between 0 and 100),
    ai_new_account_limit  int not null default 2 check (ai_new_account_limit between 0 and 100),
    ai_global_daily_limit int not null default 200 check (ai_global_daily_limit between 0 and 100000),
    ai_daily_refund_limit int not null default 2 check (ai_daily_refund_limit between 0 and 20),
    announcement          text check (char_length(announcement) <= 300),
    min_app_version       text check (min_app_version ~ '^[0-9]{1,4}(\.[0-9]{1,4}){0,3}$'),
    default_currency      text not null default 'TRY' check (default_currency in ('TRY', 'EUR', 'USD', 'GBP')),
    default_reminder_hour int not null default 9 check (default_reminder_hour between 0 and 23),
    updated_at            timestamptz not null default now(),
    updated_by            text
);
alter table private.app_settings enable row level security;
revoke all on table private.app_settings from public, anon, authenticated;
insert into private.app_settings (id) values (true) on conflict (id) do nothing;

-- ------------------------------------------------------------------------------------------- AI limits
-- Same names as migrations 9 and 12; not "immutable" any more (they read the table). The defaults stand in when
-- the row is missing.
create or replace function private.ai_daily_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_daily_limit from private.app_settings where id), 5)
$$;

create or replace function private.ai_new_account_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_new_account_limit from private.app_settings where id), 2)
$$;

create or replace function private.ai_global_daily_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_global_daily_limit from private.app_settings where id), 200)
$$;

create or replace function private.ai_daily_refund_limit() returns int
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_daily_refund_limit from private.app_settings where id), 2)
$$;

create or replace function private.ai_enabled() returns boolean
language sql stable security definer set search_path = '' as $$
    select coalesce((select ai_enabled from private.app_settings where id), true)
$$;

revoke all on function private.ai_daily_limit(), private.ai_new_account_limit(), private.ai_global_daily_limit(),
    private.ai_daily_refund_limit(), private.ai_enabled() from public, anon, authenticated;

-- Same as migration 26, plus: paused (and its message) when AI is in maintenance; nothing left then.
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    perform private.ai_note_device(v_uid, v_device);
    if not private.ai_enabled() then
        return jsonb_build_object('limit', v_limit, 'remaining', 0, 'paused', true,
                                  'message', (select ai_paused_message from private.app_settings where id));
    end if;
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    v_user := coalesce(v_user, 0);
    v_dev := case when v_own then 0 else coalesce(v_dev, 0) end;
    return jsonb_build_object(
        'limit', v_limit,
        'paused', false,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(v_user, v_dev), 0) end);
end;
$$;

-- Same as migration 26, plus: HTTP 503 (SQLSTATE PT503) while AI is paused - before any plan is counted.
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    if not private.ai_enabled() then
        raise exception 'AI planning is paused' using errcode = 'PT503';
    end if;

    perform private.ai_note_device(v_uid, v_device);

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

    if v_user >= v_limit or (not v_own and v_dev >= v_limit) then
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
    delete from private.ai_user_devices where last_seen < current_date - 31;

    return jsonb_build_object('limit', v_limit,
                              'remaining', v_limit - (case when v_own then v_user else greatest(v_user, v_dev) end) - 1,
                              'ticket', v_ticket);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;

-- ------------------------------------------------------------------------------------------- app
-- What the app needs to know, also before sign-in (guests see the notice too). No limits, no admin data.
create or replace function public.app_config() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'ai_enabled', coalesce(s.ai_enabled, true),
        'ai_paused_message', s.ai_paused_message,
        'announcement', s.announcement,
        'min_app_version', s.min_app_version,
        'default_currency', coalesce(s.default_currency, 'TRY'),
        'default_reminder_hour', coalesce(s.default_reminder_hour, 9))
    from (select 1) one
    left join private.app_settings s on s.id
$$;
revoke all on function public.app_config() from public;
grant execute on function public.app_config() to anon, authenticated, service_role;

-- ------------------------------------------------------------------------------------------- panel
create or replace function public.admin_settings() returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select to_jsonb(s) - 'id'
           || jsonb_build_object('ai_today', coalesce((select count from private.ai_global_usage where day = current_date), 0))
    from private.app_settings s
    where s.id
$$;

-- Changes the keys given in p (others stay); an empty text clears a text setting. Returns the new settings.
-- p.updated_by: who changed it (the panel sends the admin's e-mail).
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
    foreach f in array array['public.admin_settings()', 'public.admin_set_settings(jsonb)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261020000030_forced_sign_out.sql
-- =====================================================================================================
-- TravelPlanner - admin / 30: "Tüm cihazlardan çıkış" takes effect at once.
--
-- admin_sign_out_user (migration 27) deletes the account's sessions and refresh tokens, but an access token the
-- phone already has stays valid until it expires (up to an hour), and the app only noticed when its refresh failed.
-- Now the time of the sign-out is kept (private.forced_sign_outs) and account_status answers 'signed_out' for a
-- token issued before it; the app checks account_status at start and when it comes back, and signs out.
-- A ban does the same. Signing in again afterwards gives a new token, which is fine. Safe to run again.
-- =====================================================================================================

create table if not exists private.forced_sign_outs (
    user_id uuid primary key references auth.users (id) on delete cascade,
    at      timestamptz not null default now()
);
alter table private.forced_sign_outs enable row level security;
revoke all on table private.forced_sign_outs from public, anon, authenticated;

create or replace function private.force_sign_out(p_user_id uuid) returns void
language sql
security definer
set search_path = ''
as $$
    delete from auth.sessions where user_id = p_user_id;
    delete from auth.refresh_tokens where user_id = p_user_id::text;
    insert into private.forced_sign_outs (user_id, at) values (p_user_id, now())
    on conflict (user_id) do update set at = excluded.at;
$$;
revoke all on function private.force_sign_out(uuid) from public, anon, authenticated;

-- Same as migration 27, through private.force_sign_out.
create or replace function public.admin_ban_user(p_user_id uuid, p_days int default null) returns timestamptz
language plpgsql
security definer
set search_path = ''
as $$
declare v_until timestamptz;
begin
    if p_days is not null and p_days not between 1 and 3650 then
        raise exception 'days must be between 1 and 3650' using errcode = '22023';
    end if;
    v_until := now() + coalesce(make_interval(days => p_days), interval '100 years');

    update auth.users set banned_until = v_until where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform private.force_sign_out(p_user_id);
    return v_until;
end;
$$;

create or replace function public.admin_sign_out_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform private.force_sign_out(p_user_id);
end;
$$;

-- Same as migration 21, plus 'signed_out': the caller's token was issued before the admin signed the account out.
create or replace function public.account_status(p_user_id uuid) returns text
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid   uuid := (select auth.uid());
    v_state text;
    v_iat   bigint;
begin
    -- (not require_uid: a deleted or banned account must still be able to learn that it is)
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if p_user_id is distinct from v_uid then
        raise exception 'only your own account' using errcode = '42501';
    end if;
    v_state := private.account_state(v_uid);
    if v_state <> 'active' then
        return v_state;
    end if;

    v_iat := case when (auth.jwt() ->> 'iat') ~ '^[0-9]+$' then (auth.jwt() ->> 'iat')::bigint end;
    if v_iat is not null and exists (select 1 from private.forced_sign_outs f
                                     where f.user_id = v_uid and f.at > to_timestamp(v_iat)) then
        return 'signed_out';
    end if;
    return 'active';
end;
$$;

revoke all on function public.account_status(uuid) from public, anon;
grant execute on function public.account_status(uuid) to authenticated;

do $$
declare f text;
begin
    foreach f in array array['public.admin_ban_user(uuid, int)', 'public.admin_sign_out_user(uuid)'] loop
        execute format('revoke all on function %s from public, anon, authenticated', f);
        execute format('grant execute on function %s to service_role', f);
    end loop;
end $$;

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261020000031_ai_model_setting.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261020000032_trip_cover.sql
-- =====================================================================================================
-- TravelPlanner - 32: a trip's cover photo.
--
-- The app crops the photo the user picks to 16:9 (1280x720 JPEG) and makes a small square thumbnail of it
-- (256x256 JPEG) for the trip list. Both go to the trip's folder in the "trip-files" bucket:
--
--   trips/<trip_id>/cover-<32 hex>.jpg          the cover          (trips.cover_path)
--   trips/<trip_id>/cover-<32 hex>-thumb.jpg    its thumbnail      (same name + "-thumb", not stored)
--
-- A new photo gets a new name, so every device knows a cached copy is still right by its name alone. The app uploads
-- the two files first and only then sends cover_path with the trip (apply_changes), so a device that sees the new
-- path can download it. Reading and uploading need the same trip roles as attachments (migration 5).
--
-- Clean-up, the same way as attachments (migrations 16, 17):
--   * a trigger queues the old cover and its thumbnail when cover_path changes (new photo, photo removed);
--   * the nightly orphan sweep also queues cover files of a trip that are not its current cover (an upload whose
--     path never reached the server, older than a day); a trip deleted for good takes its whole folder already;
--   * file_cleanup_claim keeps a file a trip points at again.
-- A trip in the trash keeps its cover. Safe to run again.
-- =====================================================================================================

alter table public.trips add column if not exists cover_path text;

do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'trips_cover_path_format'
                   and conrelid = 'public.trips'::regclass) then
        -- only the trip's own folder, only this file name shape (the app never writes anything else)
        alter table public.trips add constraint trips_cover_path_format
            check (cover_path is null or cover_path ~ ('^trips/' || id::text || '/cover-[0-9a-f]{32}\.jpg$'));
    end if;
end $$;

-- "trips/<id>/cover-<x>.jpg" -> "trips/<id>/cover-<x>-thumb.jpg"
create or replace function private.cover_thumb_path(p_cover text) returns text
language sql
immutable
set search_path = ''
as $$
    select case when p_cover ~ '\.jpg$' then left(p_cover, -4) || '-thumb.jpg' end
$$;

create or replace function private.queue_old_cover_files() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if new.cover_path is distinct from old.cover_path
       and old.cover_path ~ ('^trips/' || old.id::text || '/cover-[0-9a-f]{32}\.jpg$') then
        insert into private.file_cleanup_queue (path)
        values (old.cover_path), (private.cover_thumb_path(old.cover_path))
        on conflict (path) do nothing;
    end if;
    return null;
end;
$$;

drop trigger if exists trips_queue_old_cover on public.trips;
create trigger trips_queue_old_cover
    after update of cover_path on public.trips
    for each row execute function private.queue_old_cover_files();

-- Same as migration 17, plus: cover files of an existing trip that are not its current cover.
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
          (o.name ~* '^trips/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.[a-z0-9]{1,10}$'
           and not exists (
               select 1 from public.attachments a
               where a.trip_id = split_part(o.name, '/', 2)::uuid
                 and a.id = split_part(split_part(o.name, '/', 3), '.', 1)::uuid))
          or (private.trip_id_from_path(o.name) is not null
              and not exists (select 1 from public.trips t where t.id = private.trip_id_from_path(o.name)))
          or (o.name ~ '^trips/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/cover-[0-9a-f]{32}(-thumb)?\.jpg$'
              and not exists (
                  select 1 from public.trips t
                  where t.id = private.trip_id_from_path(o.name)
                    and o.name in (t.cover_path, private.cover_thumb_path(t.cover_path))))
      )
    on conflict (path) do nothing;
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;

-- Same as migration 16, plus: a path that is a trip's current cover (or its thumbnail) is kept too.
create or replace function public.file_cleanup_claim(p_limit int default 500) returns text[]
language plpgsql
security definer
set search_path = ''
as $$
declare v_paths text[];
begin
    delete from private.file_cleanup_queue q
    where exists (select 1 from public.attachments a where a.storage_path = q.path)
       or exists (select 1 from public.trips t
                  where t.id = private.trip_id_from_path(q.path)
                    and q.path in (t.cover_path, private.cover_thumb_path(t.cover_path)));

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

revoke all on function private.cover_thumb_path(text) from public, anon, authenticated;
revoke all on function private.queue_old_cover_files() from public, anon, authenticated;
revoke all on function private.queue_orphan_files() from public, anon, authenticated;
revoke all on function public.file_cleanup_claim(int) from public, anon, authenticated;
do $$
begin
    if exists (select 1 from pg_roles where rolname = 'service_role') then
        grant execute on function public.file_cleanup_claim(int) to service_role;
    end if;
end $$;

notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261021000033_admin_ai_usage_daily.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261025000034_audit_log.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261025000035_account_delete_log.sql
-- =====================================================================================================
-- TravelPlanner - log / 35: account deletion is logged (TP-53).
--
--   public.delete_account()      as migration 17, plus one audit log row (migration 34) in the same transaction:
--                                action "user.delete", actor = the account itself ("user"), target = the account
--                                (id + e-mail), detail {"by": "self", "trips_deleted": n}, the app's user agent and
--                                address from the API request. The row has no link to auth.users, so it stays after
--                                the account is gone (one year, migration 34).
--   public.admin_delete_user()   as migration 27; the panel already writes its own "user.delete" row with the admin's
--                                e-mail (AccountActions), so the row above is skipped for a deletion from the panel.
--
-- Safe to run again.
-- =====================================================================================================

-- The API request's header (PostgREST puts them in request.headers); null outside an API call.
create or replace function private.request_header(p_name text) returns text
language plpgsql
stable
set search_path = ''
as $$
begin
    return nullif(btrim(current_setting('request.headers', true)::jsonb ->> lower(p_name)), '');
exception when others then
    return null;
end;
$$;
revoke all on function private.request_header(text) from public, anon, authenticated;

create or replace function public.delete_account() returns void
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid   uuid := private.require_uid();
    v_email text;
    v_trips int;
begin
    select lower(email) into v_email from auth.users where id = v_uid;

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
    get diagnostics v_trips = row_count;
    delete from auth.users where id = v_uid;           -- cascades to profile, memberships, templates

    -- from the app; a deletion from the panel is logged by the panel, with the admin's e-mail
    if coalesce(current_setting('travelplanner.admin_delete', true), '') <> 'on' then
        perform private.audit_write(
            p_actor_type   => 'user',
            p_action       => 'user.delete',
            p_actor_id     => v_uid,
            p_actor_email  => v_email,
            p_target_type  => 'user',
            p_target_id    => v_uid::text,
            p_target_label => v_email,
            p_detail       => jsonb_build_object('by', 'self', 'trips_deleted', v_trips),
            p_device       => private.request_header('user-agent'),
            p_ip           => split_part(coalesce(private.request_header('x-forwarded-for'), ''), ',', 1));
    end if;
end;
$$;
revoke all on function public.delete_account() from public, anon;
grant execute on function public.delete_account() to authenticated;

create or replace function public.admin_delete_user(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    update auth.users set banned_until = null where id = p_user_id;
    if not found then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    perform set_config('request.jwt.claim.sub', p_user_id::text, true);
    perform set_config('travelplanner.admin_delete', 'on', true);
    perform public.delete_account();
    perform set_config('travelplanner.admin_delete', '', true);
    perform set_config('request.jwt.claim.sub', '', true);
end;
$$;
revoke all on function public.admin_delete_user(uuid) from public, anon, authenticated;
grant execute on function public.admin_delete_user(uuid) to service_role;

notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261025000036_ai_plan_log.sql
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

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261025000037_account_devices.sql
-- =====================================================================================================
-- TravelPlanner - admin / 37: accounts opened on the same device (TP-55).
--
-- Until now the device of an account was only noted on the AI screen (private.ai_user_devices, migration 26: kept 31
-- days and gone with the account). Now:
--
--   private.account_devices          account + device (the app's salted SHA-256, never the raw id) + the account's
--                                    e-mail, first and last seen. No foreign key to auth.users: a deleted account
--                                    stays visible as "silindi". Rows not seen for a year are dropped (03:37).
--   public.note_device(device)       the app on every sign-in, start and return to the foreground (at most one
--                                    write an hour per account and device). The AI quota checks note it too.
--   public.admin_device_accounts(id) the panel's user page: the other accounts seen on this account's devices -
--                                    e-mail, state (active / banned / deleted), first and last seen together
--   admin_user / admin_users         + "device_accounts": how many other accounts share a device with this one
--
-- Safe to run again.
-- =====================================================================================================

create table if not exists private.account_devices (
    user_id    uuid not null,
    device     text not null check (device ~ '^[0-9a-f]{64}$'),
    email      text check (char_length(email) <= 320),
    first_seen timestamptz not null default now(),
    last_seen  timestamptz not null default now(),
    primary key (user_id, device)
);
alter table private.account_devices enable row level security;
revoke all on table private.account_devices from public, anon, authenticated;
create index if not exists account_devices_device_idx on private.account_devices (device);
create index if not exists account_devices_last_seen_idx on private.account_devices (last_seen);

-- What the AI screen already knew (migration 26): the last 31 days.
insert into private.account_devices (user_id, device, email, first_seen, last_seen)
select d.user_id, d.device, lower(u.email), d.last_seen::timestamptz, d.last_seen::timestamptz
from private.ai_user_devices d
join auth.users u on u.id = d.user_id
on conflict (user_id, device) do nothing;

-- Notes the pair; an existing pair is touched at most once an hour.
create or replace function private.note_account_device(p_uid uuid, p_device text) returns void
language sql
security definer
set search_path = ''
as $$
    insert into private.account_devices as d (user_id, device, email)
    values (p_uid, p_device, (select lower(u.email) from auth.users u where u.id = p_uid))
    on conflict (user_id, device) do update
        set last_seen = now(), email = coalesce(excluded.email, d.email)
        where d.last_seen < now() - interval '1 hour';
$$;
revoke all on function private.note_account_device(uuid, text) from public, anon, authenticated;

create or replace function public.note_device(p_device text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.note_account_device(private.require_uid(), private.ai_check_device(p_device));
end;
$$;
revoke all on function public.note_device(text) from public, anon;
grant execute on function public.note_device(text) to authenticated;

-- As migration 26, plus the long-lived note above (older apps only report the device on the AI screen).
create or replace function private.ai_note_device(p_uid uuid, p_device text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    insert into private.ai_user_devices as d (user_id, device, last_seen) values (p_uid, p_device, current_date)
    on conflict (user_id, device) do update set last_seen = current_date
        where d.last_seen <> current_date;
    perform private.note_account_device(p_uid, p_device);
end;
$$;
revoke all on function private.ai_note_device(uuid, text) from public, anon, authenticated;

-- How many other accounts were seen on this account's devices.
create or replace function private.device_account_count(p_uid uuid) returns int
language sql
stable
security definer
set search_path = ''
as $$
    select count(distinct o.user_id)::int
    from private.account_devices m
    join private.account_devices o on o.device = m.device and o.user_id <> m.user_id
    where m.user_id = p_uid
$$;
revoke all on function private.device_account_count(uuid) from public, anon, authenticated;

-- [{user_id, email, state, first_seen, last_seen, devices}, ...]: newest together first. first/last seen are the
-- other account's on the shared devices.
create or replace function public.admin_device_accounts(p_user_id uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select coalesce(jsonb_agg(jsonb_build_object(
               'user_id', x.user_id,
               'email', coalesce(lower(u.email), x.email),
               'state', case when u.id is null then 'deleted' else private.account_state(x.user_id) end,
               'first_seen', x.first_seen, 'last_seen', x.last_seen, 'devices', x.devices)
             order by x.last_seen desc), '[]'::jsonb)
    from (
        select o.user_id, max(o.email) as email, min(o.first_seen) as first_seen, max(o.last_seen) as last_seen,
               count(distinct o.device)::int as devices
        from private.account_devices m
        join private.account_devices o on o.device = m.device and o.user_id <> m.user_id
        where m.user_id = p_user_id
        group by o.user_id
    ) x
    left join auth.users u on u.id = x.user_id
$$;
revoke all on function public.admin_device_accounts(uuid) from public, anon, authenticated;
grant execute on function public.admin_device_accounts(uuid) to service_role;

-- One account as the panel shows it (migration 27, plus device_accounts).
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id),
        'banned_until', u.banned_until,
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id),
        'device_accounts', private.device_account_count(u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- retention
create or replace function private.purge_account_devices() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_count int;
begin
    delete from private.account_devices where last_seen < now() - interval '1 year';
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;
revoke all on function private.purge_account_devices() from public, anon, authenticated;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-account-devices';
        perform cron.schedule('travelplanner-purge-account-devices', '37 3 * * *', 'select private.purge_account_devices()');
    else
        raise notice 'pg_cron missing: old device notes are not purged. Enable it in Database -> Extensions and re-run this file.';
    end if;
end $$;

notify pgrst, 'reload schema';

-- >>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>>> migrations/20261025000038_audit_log_filters.sql
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
