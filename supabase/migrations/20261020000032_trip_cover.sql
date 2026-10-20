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
