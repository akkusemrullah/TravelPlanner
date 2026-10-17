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
