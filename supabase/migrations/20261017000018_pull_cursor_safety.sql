-- =====================================================================================================
-- TravelPlanner - TP-8 / 18: the pull cursor never skips a row written by a slow, still-running transaction.
--
-- pull_changes (migration 3) moved the cursor to the largest change_seq among rows whose updated_at was more
-- than 10 seconds old. But updated_at was the writing transaction's START time (now()), while change_seq is
-- drawn when the row is written, and the row stays invisible until the transaction commits. An apply_changes
-- call that waited on row locks for more than 10 seconds could hold a row with a low change_seq and an old
-- updated_at; meanwhile someone else's higher change_seq committed, a reader's cursor moved past it, and when
-- the slow call finally committed, its row was never sent to that reader (until the row changed again).
-- Example: two people sync a lot at once (one restores a backup: 500 rows); a third member misses some edits.
-- From now on:
--   * updated_at is the time the row is written (clock_timestamp(), taken right after its change_seq), so
--     "older than the safe point" really means "its change_seq was drawn before the safe point".
--   * the safe point (private.pull_safe_before) is 10 seconds ago, or the start of the oldest transaction
--     that is still writing, whichever is earlier: no transaction that could still commit a lower change_seq
--     is passed over. Transactions running for more than 10 minutes are ignored, so a stuck session can't
--     freeze everyone's cursor.
--   * apply_changes waits at most 2 seconds for a row lock (that change comes back "invalid" and the app
--     sends it again) and stops a call after 6 seconds (the whole call fails and the app tries again), so even
--     where the transaction list can't be read, no write stays open longer than the 10-second window.
-- The app now sends at most 50 changes per call and reads everything again (p_since = 0) once a day.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. write time
create or replace function private.touch_synced_row() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.change_seq := nextval('public.sync_change_seq');
    -- TP-8: the write time, not the transaction's start - pull_changes relies on it (private.pull_safe_before)
    new.updated_at := clock_timestamp();
    if tg_op = 'INSERT' then
        new.version := 1;
        new.created_at := now();
    else
        new.id := old.id;
        new.version := old.version + 1;
        new.created_at := old.created_at;
        new.created_by := old.created_by;
    end if;
    return new;
end;
$$;

create or replace function private.touch_member_row() returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.change_seq := nextval('public.sync_change_seq');
    new.updated_at := clock_timestamp(); -- TP-8: see touch_synced_row
    return new;
end;
$$;

-- ------------------------------------------------------------------------------------------- 2. safe point
-- Rows written before this moment can be passed by the cursor: every transaction that could still commit a
-- lower change_seq started after it. pg_stat_activity shows other sessions' transactions only to roles allowed
-- to see them; where it shows nothing, the 10 seconds and apply_changes' time limit still cover sync writes.
create or replace function private.pull_safe_before() returns timestamptz
language sql
volatile
security definer
set search_path = ''
as $$
    select least(
        now() - interval '10 seconds',
        (select min(a.xact_start)
         from pg_catalog.pg_stat_activity a
         where a.backend_xid is not null                         -- has written something
           and a.pid <> pg_catalog.pg_backend_pid()
           and a.datname = pg_catalog.current_database()
           and a.xact_start > now() - interval '10 minutes'));   -- a stuck session doesn't freeze every cursor
$$;

revoke all on function private.pull_safe_before() from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- 3. apply_changes
-- Same as migration 13, plus: at most 2 seconds waiting for a row lock, at most 6 seconds per call.
create or replace function public.apply_changes(p_changes jsonb) returns jsonb
language plpgsql
security definer
set search_path = ''
set lock_timeout = '2s'
as $$
declare
    v_uid     uuid := (select auth.uid());
    v_change  jsonb;
    v_results jsonb := '[]'::jsonb;
    v_minute  timestamptz := date_trunc('minute', now());
    v_count   int;
    v_calls   int;
    v_changes int;
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
    v_count := jsonb_array_length(p_changes);
    if v_count > 500 then
        raise exception 'at most 500 changes per call' using errcode = '22023';
    end if;

    -- TP-3: calls and changes per minute (a refused call rolls back and isn't counted)
    insert into private.sync_usage as u (user_id, minute_start, minute_calls, minute_changes)
    values (v_uid, v_minute, 1, v_count)
    on conflict (user_id) do update
        set minute_calls   = case when u.minute_start = v_minute then u.minute_calls + 1 else 1 end,
            minute_changes = case when u.minute_start = v_minute then u.minute_changes + v_count else v_count end,
            minute_start   = v_minute
    returning u.minute_calls, u.minute_changes into v_calls, v_changes;
    if v_calls > private.quota_limit('calls_per_minute') or v_changes > private.quota_limit('changes_per_minute') then
        raise exception 'too many sync requests, try again in a minute' using errcode = 'PT429';
    end if;

    for v_change in select value from jsonb_array_elements(p_changes) loop
        -- TP-8: no sync write stays open as long as pull_changes' 10-second window; the whole call rolls back
        -- and the app sends it again (query_canceled is not caught by "others" below)
        if clock_timestamp() - now() > interval '6 seconds' then
            raise exception 'sync call took too long, try again' using errcode = '57014';
        end if;
        begin
            v_results := v_results || private.apply_change(v_change, v_uid);
        exception
            when sqlstate 'TPQ01' then
                v_results := v_results || private.change_result(v_change, 'rejected', 'quota', p_error => sqlerrm);
            when others then
                -- constraint violations, bad values, a parent not uploaded yet, a row locked for 2 s ... - this change only
                v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => sqlerrm);
        end;
    end loop;

    return v_results;
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;

-- ------------------------------------------------------------------------------------------- 4. pull_changes
-- Same as migration 3, with the safe point from private.pull_safe_before().
create or replace function public.pull_changes(p_since bigint default 0) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := (select auth.uid());
    v_since  bigint := greatest(coalesce(p_since, 0), 0);
    v_safe   timestamptz := private.pull_safe_before();
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

revoke all on function public.pull_changes(bigint) from public, anon;
grant execute on function public.pull_changes(bigint) to authenticated;
