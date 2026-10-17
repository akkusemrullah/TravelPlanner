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
