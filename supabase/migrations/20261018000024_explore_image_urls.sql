-- =====================================================================================================
-- TravelPlanner - TP-41 / 24: Keşfet images may also be external https URLs.
--
-- cover_path / thumb_path accepted only a file in the explore-images bucket (covers/<slug>.webp). They now also
-- accept a full https URL (e.g. a Wikimedia Commons thumbnail), so images do not have to be uploaded. The app uses
-- such a value as is; a bucket path still becomes the bucket's public URL. Content is written only by the admin.
-- Safe to run again.
-- =====================================================================================================

alter table public.explore_trips drop constraint if exists explore_trips_cover_path_check;
alter table public.explore_trips drop constraint if exists explore_trips_thumb_path_check;

alter table public.explore_trips add constraint explore_trips_cover_path_check
    check (cover_path ~ '^covers/[a-z0-9-]+\.(webp|jpg)$'
           or (cover_path ~ '^https://[^\s]+$' and char_length(cover_path) <= 1000));
alter table public.explore_trips add constraint explore_trips_thumb_path_check
    check (thumb_path ~ '^thumbs/[a-z0-9-]+\.(webp|jpg)$'
           or (thumb_path ~ '^https://[^\s]+$' and char_length(thumb_path) <= 1000));
