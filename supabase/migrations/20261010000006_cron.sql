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
