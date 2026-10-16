-- =====================================================================================================
-- TravelPlanner - G2 / 3: the two sync calls the app makes.
--
-- public.apply_changes(p_changes jsonb) -> jsonb
--   The app's outbox (PendingChange rows), in order. Each element:
--     { "entity": "Expense",              -- the app's table name (Trip, Todo, Expense, Place, Activity,
--                                         --   Attachment, Participant, ExpenseShare, PackingTemplate)
--       "op": "upsert" | "delete",
--       "id": "<SyncId>",
--       "trip_id": "<trip SyncId>" | null,
--       "base_version": 3,                -- version the edit was based on (0 = never synced)
--       "full": false,                    -- true when "fields" holds the whole row (new / restored row)
--       "fields": { "amount_minor": 1250, "description": "Taksi" } }   -- snake_case column names
--   Every element is applied on its own (one bad change never blocks the rest) and answered with
--     { "id", "entity", "status": applied | deleted | ignored | rejected,
--       "reason": forbidden | trip_missing | trip_deleted | row_deleted | trip_mismatch | invalid | unknown_entity,
--       "version", "change_seq", "conflict": true when someone else changed the row since base_version,
--       "error": database message for "invalid" }
--   Rules: only the changed fields are written (two people editing different fields both keep their edit;
--   on the same field the later write wins); a delete beats an offline edit (row_deleted) but a "full"
--   upsert - restoring from the trash - brings a deleted row back; deleting an unknown row is ignored.
--
-- public.pull_changes(p_since bigint) -> jsonb
--   Everything the user can see that changed after p_since, plus everything in trips they joined after it:
--     { "cursor": <next p_since>, "memberships": [{trip_id, role, change_seq}],
--       "trips": [...], "participants": [...], "places": [...], "expenses": [...], "expense_shares": [...],
--       "todos": [...], "activities": [...], "attachments": [...], "packing_templates": [...] }
--   Rows with deleted_at set mean "delete locally". A trip missing from "memberships" (that the app had
--   synced before) means the user was removed from it. Rows changed in the last few seconds are sent but
--   the cursor doesn't move past them, so a slow concurrent write is never skipped (they may arrive twice -
--   applying a row is idempotent).
-- =====================================================================================================

create or replace function private.sync_table(p_entity text) returns text
language sql
immutable
set search_path = ''
as $$
    select case p_entity
        when 'Trip' then 'trips'                    when 'trips' then 'trips'
        when 'Participant' then 'participants'      when 'participants' then 'participants'
        when 'Place' then 'places'                  when 'places' then 'places'
        when 'Expense' then 'expenses'              when 'expenses' then 'expenses'
        when 'ExpenseShare' then 'expense_shares'   when 'expense_shares' then 'expense_shares'
        when 'Todo' then 'todos'                    when 'todos' then 'todos'
        when 'Activity' then 'activities'           when 'activities' then 'activities'
        when 'Attachment' then 'attachments'        when 'attachments' then 'attachments'
        when 'PackingTemplate' then 'packing_templates' when 'packing_templates' then 'packing_templates'
    end;
$$;

-- Columns a client may set: everything except identity, scope and bookkeeping.
create or replace function private.writable_columns(p_table text) returns text[]
language sql
stable
set search_path = ''
as $$
    select coalesce(array_agg(c.column_name::text), '{}')
    from information_schema.columns c
    where c.table_schema = 'public'
      and c.table_name = p_table
      and c.column_name not in ('id', 'trip_id', 'owner_id', 'created_by', 'created_at', 'updated_by',
                                'updated_at', 'version', 'change_seq', 'deleted_at');
$$;

create or replace function private.change_result(
    p_change jsonb, p_status text, p_reason text default null,
    p_version bigint default null, p_change_seq bigint default null,
    p_conflict boolean default false, p_error text default null)
returns jsonb
language sql
immutable
set search_path = ''
as $$
    select jsonb_strip_nulls(jsonb_build_object(
        'id', p_change ->> 'id',
        'entity', p_change ->> 'entity',
        'status', p_status,
        'reason', p_reason,
        'version', p_version,
        'change_seq', p_change_seq,
        'conflict', p_conflict,
        'error', p_error));
$$;

-- One change. Runs inside apply_changes' per-change exception block.
create or replace function private.apply_change(p_change jsonb, p_uid uuid) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_table    text := private.sync_table(p_change ->> 'entity');
    v_op       text := coalesce(p_change ->> 'op', 'upsert');
    v_id       uuid := (p_change ->> 'id')::uuid;
    v_trip     uuid := nullif(p_change ->> 'trip_id', '')::uuid;
    v_base     bigint := coalesce((p_change ->> 'base_version')::bigint, 0);
    v_full     boolean := coalesce((p_change ->> 'full')::boolean, false);
    v_fields   jsonb := coalesce(p_change -> 'fields', '{}'::jsonb);
    v_row      jsonb;
    v_deleted  boolean;
    v_keys     text[];
    v_cols     text;
    v_vals     text;
    v_set      text;
    v_version  bigint;
    v_seq      bigint;
    v_conflict boolean := false;
begin
    if v_table is null then
        return private.change_result(p_change, 'rejected', 'unknown_entity');
    end if;
    if v_id is null or v_op not in ('upsert', 'delete') or jsonb_typeof(v_fields) <> 'object' then
        return private.change_result(p_change, 'rejected', 'invalid');
    end if;

    execute format('select to_jsonb(t) from public.%I t where t.id = $1 for update', v_table) into v_row using v_id;
    v_deleted := v_row is not null and v_row ->> 'deleted_at' is not null;

    -- ---------------------------------------------------------------- who may do this
    if v_table = 'trips' then
        v_trip := v_id;
        if v_row is not null then
            if v_op = 'delete' or v_deleted then
                -- deleting a trip, or bringing a deleted one back, is the owner's call
                if not private.has_trip_role(v_id, array['owner']) then
                    return private.change_result(p_change, 'rejected', 'forbidden');
                end if;
            elsif not private.has_trip_role(v_id, array['owner', 'editor']) then
                return private.change_result(p_change, 'rejected', 'forbidden');
            end if;
        end if;
        -- (a new trip may be created by anyone signed in: they become its owner)
    elsif v_table = 'packing_templates' then
        if v_row is not null and (v_row ->> 'owner_id')::uuid is distinct from p_uid then
            return private.change_result(p_change, 'rejected', 'forbidden');
        end if;
    else
        if v_row is not null then
            if v_trip is null then
                v_trip := (v_row ->> 'trip_id')::uuid;
            elsif v_trip <> (v_row ->> 'trip_id')::uuid then
                return private.change_result(p_change, 'rejected', 'trip_mismatch');
            end if;
        end if;
        if v_trip is null or not exists (select 1 from public.trips t where t.id = v_trip) then
            return private.change_result(p_change, 'rejected', 'trip_missing');
        end if;
        if not private.has_trip_role(v_trip, array['owner', 'editor']) then
            return private.change_result(p_change, 'rejected', 'forbidden');
        end if;
        if exists (select 1 from public.trips t where t.id = v_trip and t.deleted_at is not null) then
            return private.change_result(p_change, 'rejected', 'trip_deleted');
        end if;
    end if;

    -- ---------------------------------------------------------------- delete (soft)
    if v_op = 'delete' then
        if v_row is null or v_deleted then
            return private.change_result(p_change, 'ignored');
        end if;
        execute format('update public.%I set deleted_at = now(), updated_by = $2 where id = $1 returning version, change_seq', v_table)
            into v_version, v_seq using v_id, p_uid;
        return private.change_result(p_change, 'deleted', null, v_version, v_seq);
    end if;

    -- ---------------------------------------------------------------- upsert
    select array_agg(k) into v_keys
    from jsonb_object_keys(v_fields) k
    where k = any (private.writable_columns(v_table));
    v_keys := coalesce(v_keys, '{}');

    if v_row is null then
        -- new row: the server fills in identity, scope and author
        v_fields := v_fields || jsonb_build_object('id', v_id, 'created_by', p_uid, 'updated_by', p_uid);
        v_keys := v_keys || array['id', 'created_by', 'updated_by'];
        if v_table in ('trips', 'packing_templates') then
            v_fields := v_fields || jsonb_build_object('owner_id', p_uid);
            v_keys := v_keys || array['owner_id'];
        else
            v_fields := v_fields || jsonb_build_object('trip_id', v_trip);
            v_keys := v_keys || array['trip_id'];
        end if;

        select string_agg(format('%I', k), ', '), string_agg(format('r.%I', k), ', ')
        into v_cols, v_vals
        from unnest(v_keys) k;

        execute format('insert into public.%I (%s) select %s from jsonb_populate_record(null::public.%I, $1) r returning version, change_seq',
                       v_table, v_cols, v_vals, v_table)
            into v_version, v_seq using v_fields;

        if v_table = 'trips' then
            insert into public.trip_members (trip_id, user_id, role, invited_by)
            values (v_id, p_uid, 'owner', p_uid);
        end if;

        return private.change_result(p_change, 'applied', null, v_version, v_seq);
    end if;

    -- existing row
    if v_deleted and not v_full then
        -- deleted by someone else while this edit waited offline: the delete wins
        return private.change_result(p_change, 'rejected', 'row_deleted');
    end if;

    v_conflict := v_base > 0 and v_base < (v_row ->> 'version')::bigint;

    if cardinality(v_keys) = 0 and not v_deleted then
        return private.change_result(p_change, 'applied', null, (v_row ->> 'version')::bigint, (v_row ->> 'change_seq')::bigint, v_conflict);
    end if;

    select string_agg(format('%I = r.%I', k, k), ', ') into v_set from unnest(v_keys) k;
    v_set := coalesce(v_set || ', ', '') || 'updated_by = $3, deleted_at = null';

    execute format('update public.%I t set %s from jsonb_populate_record(null::public.%I, $1) r where t.id = $2 returning t.version, t.change_seq',
                   v_table, v_set, v_table)
        into v_version, v_seq using v_fields, v_id, p_uid;

    return private.change_result(p_change, 'applied', null, v_version, v_seq, v_conflict);
end;
$$;

create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_change  jsonb;
    v_results jsonb := '[]'::jsonb;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    -- G7: a banned or deleted account is refused at once, even with a still-valid token (migration 8)
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    if jsonb_typeof(p_changes) <> 'array' then
        raise exception 'p_changes must be a JSON array' using errcode = '22023';
    end if;
    if jsonb_array_length(p_changes) > 500 then
        raise exception 'at most 500 changes per call' using errcode = '22023';
    end if;

    for v_change in select value from jsonb_array_elements(p_changes) loop
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception when others then
            -- constraint violations, bad values, a parent not uploaded yet ... - this change only
            v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => sqlerrm);
        end;
    end loop;

    return v_results;
end;
$$;

create or replace function public.pull_changes(p_since bigint default 0) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := (select auth.uid());
    v_since  bigint := greatest(coalesce(p_since, 0), 0);
    v_safe   timestamptz := now() - interval '10 seconds';
    v_result jsonb;
    v_cursor bigint := v_since;
    v_rows   jsonb;
    v_max    bigint;
    t        text;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    -- G7: a banned or deleted account is refused at once, even with a still-valid token (migration 8)
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;

    select coalesce(jsonb_agg(jsonb_build_object('trip_id', m.trip_id, 'role', m.role, 'change_seq', m.change_seq)), '[]'),
           max(m.change_seq) filter (where m.change_seq > v_since and m.updated_at < v_safe)
    into v_rows, v_max
    from public.trip_members m
    where m.user_id = v_uid;
    v_result := jsonb_build_object('memberships', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    -- trips: changed since the cursor, or joined since the cursor (then the whole live trip)
    select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), '[]'),
           max(x.change_seq) filter (where x.updated_at < v_safe)
    into v_rows, v_max
    from public.trips x
    join public.trip_members m on m.trip_id = x.id and m.user_id = v_uid
    where x.change_seq > v_since or (m.change_seq > v_since and x.deleted_at is null);
    v_result := v_result || jsonb_build_object('trips', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format(
            'select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), ''[]''), '
            '       max(x.change_seq) filter (where x.updated_at < $3) '
            'from public.%I x '
            'join public.trip_members m on m.trip_id = x.trip_id and m.user_id = $1 '
            'where x.change_seq > $2 or (m.change_seq > $2 and x.deleted_at is null)', t)
        into v_rows, v_max
        using v_uid, v_since, v_safe;
        v_result := v_result || jsonb_build_object(t, v_rows);
        v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));
    end loop;

    select coalesce(jsonb_agg(to_jsonb(x) order by x.change_seq), '[]'),
           max(x.change_seq) filter (where x.updated_at < v_safe)
    into v_rows, v_max
    from public.packing_templates x
    where x.owner_id = v_uid and x.change_seq > v_since;
    v_result := v_result || jsonb_build_object('packing_templates', v_rows);
    v_cursor := greatest(v_cursor, coalesce(v_max, v_cursor));

    return v_result || jsonb_build_object('cursor', v_cursor);
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
revoke all on function public.pull_changes(bigint) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;
grant execute on function public.pull_changes(bigint) to authenticated;
revoke all on all functions in schema private from public, anon;
grant execute on function private.has_trip_role(uuid, text[]) to authenticated;
grant execute on function private.shares_trip_with(uuid) to authenticated;
