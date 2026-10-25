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
