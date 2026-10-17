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
