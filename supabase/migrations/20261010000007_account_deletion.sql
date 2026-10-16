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
