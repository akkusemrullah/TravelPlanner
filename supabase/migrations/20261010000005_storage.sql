-- =====================================================================================================
-- TravelPlanner - G2 / 5: attachment files.
-- Private bucket "trip-files"; object keys: trips/<trip_id>/<attachment_id>.<ext>
-- Members of the trip read; owner/editor upload, replace and delete. Max 50 MB per file (the app's limit;
-- also the Free plan's cap).
-- =====================================================================================================

insert into storage.buckets (id, name, public, file_size_limit)
values ('trip-files', 'trip-files', false, 52428800)
on conflict (id) do update set public = false, file_size_limit = excluded.file_size_limit;

-- "trips/<uuid>/..." -> <uuid>; anything else -> null (no access).
create or replace function private.trip_id_from_path(p_name text) returns uuid
language plpgsql
immutable
set search_path = ''
as $$
declare v_parts text[] := string_to_array(p_name, '/');
begin
    if array_length(v_parts, 1) >= 3
       and v_parts[1] = 'trips'
       and v_parts[2] ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then
        return v_parts[2]::uuid;
    end if;
    return null;
end;
$$;

grant execute on function private.trip_id_from_path(text) to authenticated;

drop policy if exists "trip-files: members read" on storage.objects;
create policy "trip-files: members read" on storage.objects
    for select to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor', 'viewer']));

drop policy if exists "trip-files: editors upload" on storage.objects;
create policy "trip-files: editors upload" on storage.objects
    for insert to authenticated
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));

drop policy if exists "trip-files: editors replace" on storage.objects;
create policy "trip-files: editors replace" on storage.objects
    for update to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']))
    with check (bucket_id = 'trip-files'
                and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));

drop policy if exists "trip-files: editors delete" on storage.objects;
create policy "trip-files: editors delete" on storage.objects
    for delete to authenticated
    using (bucket_id = 'trip-files'
           and private.has_trip_role(private.trip_id_from_path(name), array['owner', 'editor']));
