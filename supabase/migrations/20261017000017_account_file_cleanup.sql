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
