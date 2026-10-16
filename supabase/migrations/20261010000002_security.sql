-- =====================================================================================================
-- TravelPlanner - G2 / 2: who may see what (Row Level Security) and what the API roles may touch.
-- Reading: any member of a trip (owner / editor / viewer) reads everything in it.
-- Writing: never directly - only through the RPCs in 000003/000004, which check the role themselves:
--   viewer  - reads only
--   editor  - adds / edits / deletes trip content
--   owner   - all of that + members, invites, deleting (and restoring) the trip
-- =====================================================================================================

-- Role of the current user in a trip, as a check. SECURITY DEFINER so policies on trip_members itself
-- don't recurse.
create or replace function private.has_trip_role(p_trip_id uuid, p_roles text[])
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members m
        where m.trip_id = p_trip_id
          and m.user_id = (select auth.uid())
          and m.role = any (p_roles));
$$;

create or replace function private.shares_trip_with(p_user_id uuid)
returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (
        select 1
        from public.trip_members mine
        join public.trip_members theirs on theirs.trip_id = mine.trip_id
        where mine.user_id = (select auth.uid())
          and theirs.user_id = p_user_id);
$$;

-- ------------------------------------------------------------------------------------------- RLS
alter table public.profiles          enable row level security;
alter table public.trips             enable row level security;
alter table public.trip_members      enable row level security;
alter table public.trip_invites      enable row level security;
alter table public.participants      enable row level security;
alter table public.places            enable row level security;
alter table public.expenses          enable row level security;
alter table public.expense_shares    enable row level security;
alter table public.todos             enable row level security;
alter table public.activities        enable row level security;
alter table public.attachments       enable row level security;
alter table public.packing_templates enable row level security;

drop policy if exists "profiles: self and trip mates read" on public.profiles;
create policy "profiles: self and trip mates read" on public.profiles
    for select to authenticated
    using (id = (select auth.uid()) or private.shares_trip_with(id));

drop policy if exists "profiles: self update" on public.profiles;
create policy "profiles: self update" on public.profiles
    for update to authenticated
    using (id = (select auth.uid()))
    with check (id = (select auth.uid()));

drop policy if exists "trips: members read" on public.trips;
create policy "trips: members read" on public.trips
    for select to authenticated
    using (private.has_trip_role(id, array['owner', 'editor', 'viewer']));

drop policy if exists "trip_members: members read" on public.trip_members;
create policy "trip_members: members read" on public.trip_members
    for select to authenticated
    using (private.has_trip_role(trip_id, array['owner', 'editor', 'viewer']));

drop policy if exists "trip_invites: owner reads" on public.trip_invites;
create policy "trip_invites: owner reads" on public.trip_invites
    for select to authenticated
    using (private.has_trip_role(trip_id, array['owner']));

do $$
declare t text;
begin
    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format('drop policy if exists "%s: members read" on public.%I', t, t);
        execute format(
            'create policy "%s: members read" on public.%I for select to authenticated '
            'using (private.has_trip_role(trip_id, array[''owner'', ''editor'', ''viewer'']))', t, t);
    end loop;
end $$;

drop policy if exists "packing_templates: owner reads" on public.packing_templates;
create policy "packing_templates: owner reads" on public.packing_templates
    for select to authenticated
    using (owner_id = (select auth.uid()));

-- ------------------------------------------------------------------------------------------- grants
-- The project was created with "automatically expose new tables" OFF; be explicit anyway.
revoke all on all tables in schema public from anon, authenticated;
revoke all on all sequences in schema public from anon, authenticated;

grant select on
    public.trips, public.trip_members, public.trip_invites,
    public.participants, public.places, public.expenses, public.expense_shares,
    public.todos, public.activities, public.attachments, public.packing_templates
to authenticated;

grant select on public.profiles to authenticated;
grant update (display_name, avatar_url) on public.profiles to authenticated;

-- Policies call these, so the API role needs to reach them (they only answer yes/no).
grant usage on schema private to authenticated;
revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;
