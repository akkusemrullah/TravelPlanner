-- =====================================================================================================
-- TravelPlanner - TP-5 / 15: an attachment row can only point at its OWN file, and only as a photo or a PDF.
--
-- storage_path and content_type are columns a client may write (private.writable_columns). Without a check an
-- editor calling the REST API directly could
--   * point the row of someone else's ticket at a fake PDF he uploaded ("storage_path" of another row/trip),
--   * put "..", "?" or "#" into storage_path, so the other members' download request goes somewhere else,
--   * set content_type to application/vnd.android.package-archive, so opening the "ticket" starts the
--     Android package installer.
-- From now on:
--   storage_path   null, or exactly  trips/<trip_id>/<id>.<ext>  (this row's own trip and id; ext = 1-10 of a-z 0-9)
--   content_type   null, or one of   image/jpeg, image/png, image/heic, image/heif, image/webp, application/pdf
--                  (the "trip-files" bucket's allowed_mime_types, migration 13)
-- apply_changes answers a change that breaks either rule with  status "rejected", reason "invalid"  (a check
-- violation); the rest of the batch still goes through.
--
-- Rows stored before this migration are fixed first, so editing them later isn't refused: a content type
-- outside the list becomes null (the app derives the type from the file's extension anyway), a storage_path
-- that isn't the row's own key becomes null (the uploading device puts the right one back). Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- old rows
update public.attachments
set content_type = case lower(btrim(split_part(content_type, ';', 1)))
                       when 'image/jpg'   then 'image/jpeg'
                       when 'image/pjpeg' then 'image/jpeg'
                       else lower(btrim(split_part(content_type, ';', 1)))
                   end
where content_type is not null
  and content_type not in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf');

update public.attachments
set content_type = null
where content_type is not null
  and content_type not in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf');

update public.attachments
set storage_path = null
where storage_path is not null
  and storage_path !~ ('^trips/' || trip_id::text || '/' || id::text || '\.[a-z0-9]{1,10}$');

-- ------------------------------------------------------------------------------------------- the rules
-- (uuid::text is always lower case hex and "-", so it is safe inside the pattern)
alter table public.attachments drop constraint if exists attachments_storage_path_format;
alter table public.attachments add constraint attachments_storage_path_format check (
    storage_path is null
    or storage_path ~ ('^trips/' || trip_id::text || '/' || id::text || '\.[a-z0-9]{1,10}$'));

alter table public.attachments drop constraint if exists attachments_content_type_allowed;
alter table public.attachments add constraint attachments_content_type_allowed check (
    content_type is null
    or content_type in ('image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf'));
