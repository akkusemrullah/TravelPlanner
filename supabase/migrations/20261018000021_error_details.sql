-- =====================================================================================================
-- TravelPlanner - TP-14 / 21: less detail for clients - database errors and other people's account status.
--
--   * apply_changes answered a refused change with PostgreSQL's own message (table, column and constraint
--     names). It now answers with a short code (private.error_code) and writes the full message to the server
--     log (RAISE LOG: Dashboard -> Logs -> Postgres). Usage-limit messages (quota) are our own text and stay.
--       duplicate    unique / primary key violation           missing_parent  foreign key violation
--       out_of_range check constraint (InputLimits)           missing_value   not-null violation
--       bad_value    wrong type, format or number range       busy            row locked / statement timeout
--       error        anything else
--   * account_status is no longer open to anon: a signed-in user can ask only about their own account. Anyone
--     who knew a user id (trip members see them) could find out whether that account was deleted or banned.
--     An access token issued before a deletion or ban still works for up to an hour, which is enough for the
--     app's check; after that the token can't be refreshed and the app treats it as "session ended".
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. error codes
create or replace function private.error_code(p_sqlstate text) returns text
language sql
immutable
set search_path = ''
as $$
    select case
        when p_sqlstate = '23505' then 'duplicate'
        when p_sqlstate = '23503' then 'missing_parent'
        when p_sqlstate = '23514' then 'out_of_range'
        when p_sqlstate = '23502' then 'missing_value'
        when p_sqlstate in ('55P03', '57014', '40P01', '40001') then 'busy'
        when p_sqlstate like '22%' then 'bad_value'
        else 'error'
    end;
$$;

revoke all on function private.error_code(text) from public, anon, authenticated;
-- ------------------------------------------------------------------------------------------- 2. apply_changes
-- Same as migration 18; a refused change now carries a short code instead of the database message (TP-14).
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
    v_state   text;
    v_message text;
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
                -- TP-14: the client gets a short code; the database message goes to the server log only
                v_state := sqlstate;
                v_message := sqlerrm;
                raise log 'apply_changes: % % rejected (%): %', v_change ->> 'entity', v_change ->> 'id', v_state, v_message;
                v_results := v_results || private.change_result(v_change, 'rejected', 'invalid', p_error => private.error_code(v_state));
        end;
    end loop;

    return v_results;
end;
$$;

revoke all on function public.apply_changes(jsonb) from public, anon;
grant execute on function public.apply_changes(jsonb) to authenticated;

-- ------------------------------------------------------------------------------------------- 3. account_status
create or replace function public.account_status(p_user_id uuid) returns text
language plpgsql
stable
security definer
set search_path = ''
as $$
declare v_uid uuid := (select auth.uid());
begin
    -- (not require_uid: a deleted or banned account must still be able to learn that it is)
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if p_user_id is distinct from v_uid then
        raise exception 'only your own account' using errcode = '42501';
    end if;
    return private.account_state(v_uid);
end;
$$;

revoke all on function public.account_status(uuid) from public, anon;
grant execute on function public.account_status(uuid) to authenticated;
