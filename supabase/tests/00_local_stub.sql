-- LOCAL TESTING ONLY - never run this on Supabase.
-- Recreates the bits of a Supabase database the migrations rely on (roles, auth.users, auth.uid(),
-- storage.buckets/objects), so the migrations and tests/sync_tests.sql can run on a plain PostgreSQL 15+.
do $$ begin
    if not exists (select 1 from pg_roles where rolname = 'anon') then create role anon nologin; end if;
    if not exists (select 1 from pg_roles where rolname = 'authenticated') then create role authenticated nologin; end if;
    if not exists (select 1 from pg_roles where rolname = 'service_role') then create role service_role nologin bypassrls; end if;
end $$;

create schema if not exists auth;
create table if not exists auth.users (
    id uuid primary key,
    email text,
    raw_user_meta_data jsonb not null default '{}',
    banned_until timestamptz,
    created_at timestamptz default now()
);
alter table auth.users add column if not exists created_at timestamptz default now();
alter table auth.users add column if not exists last_sign_in_at timestamptz;
create table if not exists auth.sessions (
    id uuid primary key default gen_random_uuid(),
    user_id uuid not null references auth.users (id) on delete cascade
);
create table if not exists auth.refresh_tokens (
    id bigserial primary key,
    token text,
    user_id varchar(255),
    session_id uuid references auth.sessions (id) on delete cascade
);
create or replace function auth.uid() returns uuid language sql stable as $$
    select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid
$$;
grant usage on schema auth to anon, authenticated;
grant execute on function auth.uid() to anon, authenticated;

create schema if not exists storage;
create table if not exists storage.buckets (
    id text primary key, name text not null, public boolean default false, file_size_limit bigint
);
create table if not exists storage.objects (
    id uuid primary key default gen_random_uuid(),
    bucket_id text references storage.buckets (id),
    name text not null,
    owner uuid,
    owner_id text
);
alter table storage.buckets add column if not exists allowed_mime_types text[];
alter table storage.objects add column if not exists metadata jsonb;
alter table storage.objects add column if not exists created_at timestamptz default now();
alter table storage.objects enable row level security;
grant usage on schema storage to authenticated;
grant select, insert, update, delete on storage.objects to authenticated;
grant usage on schema public to anon, authenticated, service_role;
