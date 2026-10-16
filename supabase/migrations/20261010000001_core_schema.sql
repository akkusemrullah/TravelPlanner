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
