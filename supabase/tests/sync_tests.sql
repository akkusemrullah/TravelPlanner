-- LOCAL TESTING ONLY - run after 00_local_stub.sql and the migrations, on a throw-away database:
--   psql -v ON_ERROR_STOP=1 -d tp -f tests/sync_tests.sql
-- Each check raises (and stops the script) when it fails; the last line prints "ALL SYNC TESTS PASSED".
\set ON_ERROR_STOP 1
\set QUIET 1

create schema if not exists test;
grant usage on schema test to authenticated;

create or replace function test.eq(p_actual anyelement, p_expected anyelement, p_what text) returns void
language plpgsql as $$
begin
    if p_actual is distinct from p_expected then
        raise exception 'FAILED: % (expected %, got %)', p_what, p_expected, p_actual;
    end if;
    raise notice 'ok - %', p_what;
end $$;
grant execute on function test.eq(anyelement, anyelement, text) to authenticated;

create or replace function test.status(p_result jsonb, p_index int default 0) returns text
language sql as $$ select p_result -> p_index ->> 'status' $$;
create or replace function test.reason(p_result jsonb, p_index int default 0) returns text
language sql as $$ select p_result -> p_index ->> 'reason' $$;
grant execute on function test.status(jsonb, int), test.reason(jsonb, int) to authenticated;

-- Three accounts: Ali owns the trip, Ayşe will edit, Can will only view, Deniz is a stranger.
insert into auth.users (id, email, raw_user_meta_data) values
    ('00000000-0000-0000-0000-00000000a111', 'ali@example.com',   '{"full_name": "Ali Yılmaz"}'),
    ('00000000-0000-0000-0000-00000000a222', 'ayse@example.com',  '{}'),
    ('00000000-0000-0000-0000-00000000a333', 'can@example.com',   '{}'),
    ('00000000-0000-0000-0000-00000000a444', 'deniz@example.com', '{}');

select test.eq((select display_name from public.profiles where email = 'ali@example.com'), 'Ali Yılmaz', 'profile created from sign-up metadata');
select test.eq((select display_name from public.profiles where email = 'ayse@example.com'), 'ayse', 'profile name falls back to the e-mail');

\set trip   '11111111-1111-1111-1111-111111111111'
\set ali    '00000000-0000-0000-0000-00000000a111'
\set ayse   '00000000-0000-0000-0000-00000000a222'
\set can    '00000000-0000-0000-0000-00000000a333'
\set deniz  '00000000-0000-0000-0000-00000000a444'
\set part   '22222222-2222-2222-2222-222222222222'
\set exp    '33333333-3333-3333-3333-333333333333'
\set share  '44444444-4444-4444-4444-444444444444'
\set place  '55555555-5555-5555-5555-555555555555'
\set act    '66666666-6666-6666-6666-666666666666'
\set todo   '77777777-7777-7777-7777-777777777777'

-- ------------------------------------------------------------------------------- Ali creates a trip offline, syncs
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;

create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'trip', 'full', true, 'fields',
        jsonb_build_object('name', 'Roma', 'country', 'İtalya', 'city', 'Roma', 'start_date', '2026-08-20',
                           'end_date', '2026-08-22', 'planned_budget_minor', 50000, 'currency_code', 'EUR')),
    jsonb_build_object('entity', 'Participant', 'op', 'upsert', 'id', :'part', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('name', 'Ali', 'created_date', '2026-08-01T10:00:00')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('category', 2, 'amount_minor', 1250, 'original_amount_minor', 1250,
                                     'original_currency_code', 'EUR', 'exchange_rate', 1, 'date', '2026-08-21',
                                     'paid_by_participant_id', :'part', 'description', 'Taksi',
                                     'version', 999, 'owner_id', :'deniz')),   -- bookkeeping is ignored
    jsonb_build_object('entity', 'ExpenseShare', 'op', 'upsert', 'id', :'share', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('expense_id', :'exp', 'participant_id', :'part')),
    jsonb_build_object('entity', 'Place', 'op', 'upsert', 'id', :'place', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('name', 'Kolezyum', 'latitude', 41.89, 'longitude', 12.49)),
    jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', :'act', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('date', '2026-08-21', 'start_time', '09:30:00', 'title', 'Tur', 'place_id', :'place', 'sort_order', 0)),
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'todo', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('title', 'Pasaport', 'is_completed', false))
)) as res;

select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'status' = 'applied'), 7::bigint, 'owner uploads a whole new trip');
select test.eq((select role from public.trip_members where trip_id = :'trip' and user_id = :'ali'), 'owner', 'creator becomes the owner');
select test.eq((select version from public.expenses where id = :'exp'), 1::bigint, 'client cannot set version');
select test.eq((select created_by from public.expenses where id = :'exp'), :'ali'::uuid, 'created_by is the caller');
select test.eq((select start_time from public.activities where id = :'act'), '09:30'::time, 'times arrive as time');

-- the same upload again is harmless (idempotent) and not a conflict
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'todo', 'trip_id', :'trip', 'full', true,
        'base_version', 1, 'fields', jsonb_build_object('title', 'Pasaport', 'is_completed', false)))) as res;
select test.eq(test.status(res), 'applied', 're-sending a row is fine') from r;

-- ------------------------------------------------------------------------------- strangers see and do nothing
reset role;
select set_config('request.jwt.claim.sub', :'deniz', false);
set role authenticated;

select test.eq((select count(*) from public.trips), 0::bigint, 'stranger reads no trips');
select test.eq((select count(*) from public.expenses), 0::bigint, 'stranger reads no expenses');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip',
                       'fields', jsonb_build_object('amount_minor', 1))))), 'forbidden', 'stranger cannot edit');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                       'fields', jsonb_build_object('title', 'x'))))), 'forbidden', 'stranger cannot add to a trip');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'delete', 'id', :'trip')))), 'forbidden', 'stranger cannot delete the trip');

do $$ begin
    insert into public.trips (id, owner_id, name, start_date, end_date) values (gen_random_uuid(), auth.uid(), 'x', now(), now());
    raise exception 'FAILED: direct insert was allowed';
exception when insufficient_privilege then raise notice 'ok - direct table writes are refused';
end $$;

-- ------------------------------------------------------------------------------- invites
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
create temp table editor_invite as select code from public.create_invite(:'trip', 'editor');
create temp table viewer_invite as select code from public.create_invite(:'trip', 'viewer', 24, 1);
select test.eq(length((select code from editor_invite)), 8, 'invite code has 8 characters');

reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select test.eq((select role from public.accept_invite((select lower(code) from editor_invite))), 'editor', 'Ayşe joins as editor (code is case-insensitive)');

reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
do $$ begin
    perform public.create_invite('11111111-1111-1111-1111-111111111111', 'editor');
    raise exception 'FAILED: editor created an invite';
exception when insufficient_privilege then raise notice 'ok - only the owner invites';
end $$;

reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((select role from public.accept_invite((select code from viewer_invite))), 'viewer', 'Can joins as viewer');

reset role;
select set_config('request.jwt.claim.sub', :'deniz', false);
set role authenticated;
do $$ begin
    perform public.accept_invite((select code from viewer_invite));
    raise exception 'FAILED: single-use invite used twice';
exception when no_data_found then raise notice 'ok - a single-use invite works once';
end $$;

-- ------------------------------------------------------------------------------- the viewer
reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((select count(*) from public.expenses), 1::bigint, 'viewer reads the trip content');
select test.eq((select count(*) from public.profiles), 3::bigint, 'viewer sees the trip mates'' profiles (and only them)');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                       'fields', jsonb_build_object('amount_minor', 500, 'date', '2026-08-21'))))), 'forbidden', 'viewer cannot add an expense');
select test.eq((select count(*) from public.trip_invites), 0::bigint, 'viewer does not see invites');
select test.eq((select count(*) from public.get_trip_members(:'trip')), 3::bigint, 'members list has owner, editor, viewer');

-- ------------------------------------------------------------------------------- editors edit different fields offline
reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'base_version', 1,
                       'fields', jsonb_build_object('description', 'Havalimanı taksisi'))))), 'applied', 'editor edits the description');

reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'base_version', 1,
                       'fields', jsonb_build_object('amount_minor', 1500)))) as res;
select test.eq((r.res -> 0 ->> 'conflict')::boolean, true, 'owner''s offline edit is flagged as a conflict') from r;
select test.eq((select description from public.expenses where id = :'exp'), 'Havalimanı taksisi', 'the other field keeps the editor''s value');
select test.eq((select amount_minor from public.expenses where id = :'exp'), 1500::bigint, 'and the owner''s amount is applied');
select test.eq((select updated_by from public.expenses where id = :'exp'), :'ali'::uuid, 'updated_by is the last writer');

-- a share pointing at another trip's participant is refused (composite foreign key)
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', '99999999-9999-9999-9999-999999999999', 'full', true,
        'fields', jsonb_build_object('name', 'Paris', 'start_date', '2026-09-01', 'end_date', '2026-09-02')),
    jsonb_build_object('entity', 'ExpenseShare', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', '99999999-9999-9999-9999-999999999999', 'full', true,
        'fields', jsonb_build_object('expense_id', :'exp', 'participant_id', :'part')),
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'todo', 'trip_id', '99999999-9999-9999-9999-999999999999',
        'fields', jsonb_build_object('title', 'x')),
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', gen_random_uuid(), 'full', true,
        'fields', jsonb_build_object('name', 'Ters', 'start_date', '2026-09-05', 'end_date', '2026-09-01')),
    jsonb_build_object('entity', 'Nope', 'op', 'upsert', 'id', gen_random_uuid())
)) as res;
select test.eq(test.status(res, 0), 'applied', 'second trip created') from r;
select test.eq(test.reason(res, 1), 'invalid', 'cross-trip share refused') from r;
select test.eq(test.reason(res, 2), 'trip_mismatch', 'a row cannot move to another trip') from r;
select test.eq(test.reason(res, 3), 'invalid', 'end before start refused (and the batch goes on)') from r;
select test.eq(test.reason(res, 4), 'unknown_entity', 'unknown entity refused') from r;

-- ------------------------------------------------------------------------------- SEC-01: value limits (migration 11)
-- The owner may use the whole allowed range (his own second trip; no one else sees it).
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', '99999999-9999-9999-9999-999999999999', 'full', true,
        'fields', jsonb_build_object('category', 5, 'amount_minor', 999999999999, 'original_amount_minor', 999999999999,
                                     'exchange_rate', 1000000, 'date', '2100-12-31')),
    jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', '99999999-9999-9999-9999-999999999999', 'full', true,
        'fields', jsonb_build_object('date', '1900-01-01', 'start_time', '09:00:00', 'end_time', '10:00:00', 'title', 'Sınır', 'sort_order', 100000))
)) as res;
select test.eq(test.status(res, 0), 'applied', 'limits: the largest allowed amount/rate/date is accepted') from r;
select test.eq(test.status(res, 1), 'applied', 'limits: the edge dates and sort order are accepted') from r;

-- An editor talking to the REST API directly cannot store values that break everyone's sync.
reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    /* 0 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('amount_minor', 9223372036854775807)),
    /* 1 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('amount_minor', 1000000000000)),
    /* 2 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('original_amount_minor', 1000000000000)),
    /* 3 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('exchange_rate', 1e300)),
    /* 4 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('exchange_rate', 0.0000001)),
    /* 5 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('exchange_rate', 'NaN')),
    /* 6 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('date', '20000-01-01')),
    /* 7 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('category', 99)),
    /* 8 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'exp', 'trip_id', :'trip', 'fields', jsonb_build_object('category', -1)),
    /* 9 */ jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', :'act', 'trip_id', :'trip', 'fields', jsonb_build_object('date', '20000-01-01')),
    /*10 */ jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', :'act', 'trip_id', :'trip', 'fields', jsonb_build_object('sort_order', 2147483647)),
    /*11 */ jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', :'act', 'trip_id', :'trip', 'fields', jsonb_build_object('end_time', '08:00:00')),
    /*12 */ jsonb_build_object('entity', 'Activity', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                'fields', jsonb_build_object('date', '2026-08-21', 'end_time', '10:00:00', 'title', 'Bitişi var, başlangıcı yok')),
    /*13 */ jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'trip', 'fields', jsonb_build_object('start_date', '1800-01-01')),
    /*14 */ jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'trip', 'fields', jsonb_build_object('end_date', '20000-01-01')),
    /*15 */ jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'trip', 'fields', jsonb_build_object('planned_budget_minor', 9223372036854775807)),
    /*16 */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                'fields', jsonb_build_object('display_name', 'x.pdf', 'kind', 7)),
    /*17 */ jsonb_build_object('entity', 'Participant', 'op', 'upsert', 'id', :'part', 'trip_id', :'trip', 'fields', jsonb_build_object('created_date', '20000-01-01T00:00:00')),
    /*18 */ jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                'fields', jsonb_build_object('amount_minor', 9223372036854775807, 'date', '2026-08-21'))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'reason' = 'invalid'), 19::bigint,
               'limits: every out-of-range value is refused as "invalid"');
select test.eq((select amount_minor from public.expenses where id = :'exp'), 1500::bigint, 'limits: the expense keeps its amount');
select test.eq((select exchange_rate from public.expenses where id = :'exp'), 1::double precision, 'limits: and its rate');
select test.eq((select category from public.expenses where id = :'exp'), 2, 'limits: and its category');
select test.eq((select date from public.activities where id = :'act'), '2026-08-21'::date, 'limits: the activity keeps its date');
select test.eq((select start_date from public.trips where id = :'trip'), '2026-08-20'::date, 'limits: the trip keeps its dates');
select test.eq((select count(*) from public.expenses where trip_id = :'trip'), 1::bigint, 'limits: no bad expense was added');

reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;

-- ------------------------------------------------------------------------------- delete beats an offline edit; restore
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'delete', 'id', :'todo', 'trip_id', :'trip')))), 'deleted', 'owner deletes the to-do');

reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'todo', 'trip_id', :'trip', 'base_version', 2,
                       'fields', jsonb_build_object('is_completed', true))))), 'row_deleted', 'an offline edit of a deleted row loses');
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'delete', 'id', :'todo', 'trip_id', :'trip')))), 'ignored', 'deleting twice is harmless');
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'delete', 'id', gen_random_uuid(), 'trip_id', :'trip')))), 'ignored', 'deleting an unknown row is harmless');
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'todo', 'trip_id', :'trip', 'full', true,
                       'fields', jsonb_build_object('title', 'Pasaport', 'is_completed', true))))), 'applied', 'restoring from the trash (full row) undeletes');
select test.eq((select deleted_at is null and is_completed from public.todos where id = :'todo'), true, 'the to-do is back');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'delete', 'id', :'trip')))), 'forbidden', 'an editor cannot delete the trip');

-- ------------------------------------------------------------------------------- pulling
reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
-- make every row "older than the safety window" so the cursor can move
reset role;
update public.trips set updated_at = updated_at - interval '1 minute';
update public.trip_members set updated_at = updated_at - interval '1 minute';
do $$ declare t text; begin
    foreach t in array array['participants', 'places', 'expenses', 'expense_shares', 'todos', 'activities', 'attachments'] loop
        execute format('alter table public.%I disable trigger touch_synced_row', t);
        execute format('update public.%I set updated_at = updated_at - interval ''1 minute''', t);
        execute format('alter table public.%I enable trigger touch_synced_row', t);
    end loop;
end $$;
alter table public.trips disable trigger touch_synced_row;
update public.trips set updated_at = updated_at - interval '1 minute';
alter table public.trips enable trigger touch_synced_row;
alter table public.trip_members disable trigger touch_member_row;
update public.trip_members set updated_at = updated_at - interval '1 minute';
alter table public.trip_members enable trigger touch_member_row;

select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
drop table if exists p;
create temp table p as select public.pull_changes(0) as res;
select test.eq(jsonb_array_length(res -> 'trips'), 1, 'viewer pulls only the shared trip (not Ali''s other trip)') from p;
select test.eq(jsonb_array_length(res -> 'expenses'), 1, 'and its expense') from p;
select test.eq(jsonb_array_length(res -> 'expense_shares'), 1, 'and its split') from p;
select test.eq(res -> 'memberships' -> 0 ->> 'role', 'viewer', 'memberships tell the role') from p;
select test.eq((res ->> 'cursor')::bigint > 0, true, 'a cursor comes back') from p;
create temp table c as select (res ->> 'cursor')::bigint as cursor from p;
select test.eq(jsonb_array_length(public.pull_changes((select cursor from c)) -> 'expenses'), 0, 'nothing new after the cursor');

reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'delete', 'id', :'exp', 'trip_id', :'trip')));

reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((public.pull_changes((select cursor from c)) -> 'expenses' -> 0 ->> 'deleted_at') is not null, true, 'a delete arrives as a row with deleted_at');

-- ------------------------------------------------------------------------------- members
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select public.set_member_role(:'trip', :'can', 'editor');
select test.eq((select role from public.trip_members where trip_id = :'trip' and user_id = :'can'), 'editor', 'owner promotes the viewer');
select public.remove_member(:'trip', :'can');
do $$ begin
    perform public.remove_member('11111111-1111-1111-1111-111111111111', '00000000-0000-0000-0000-00000000a111');
    raise exception 'FAILED: owner removed themself';
exception when no_data_found then raise notice 'ok - the owner cannot be removed';
end $$;

reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq(jsonb_array_length(public.pull_changes(0) -> 'memberships'), 0, 'a removed member no longer has the trip');
select test.eq((select count(*) from public.expenses), 0::bigint, 'and reads nothing of it');

reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select public.leave_trip(:'trip');
select test.eq((select count(*) from public.trips), 0::bigint, 'after leaving, the trip is gone for Ayşe');

-- ------------------------------------------------------------------------------- personal data and storage
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'PackingTemplate', 'op', 'upsert', 'id', '88888888-8888-8888-8888-888888888888', 'full', true,
                       'fields', jsonb_build_object('name', 'Kış', 'items', 'Mont'))))), 'applied', 'own packing template');
reset role;
select set_config('request.jwt.claim.sub', :'deniz', false);
set role authenticated;
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'PackingTemplate', 'op', 'upsert', 'id', '88888888-8888-8888-8888-888888888888',
                       'fields', jsonb_build_object('name', 'Hack'))))), 'forbidden', 'someone else''s template is off limits');

reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
insert into storage.objects (bucket_id, name) values ('trip-files', 'trips/11111111-1111-1111-1111-111111111111/a.pdf');
select test.eq((select count(*) from storage.objects), 1::bigint, 'owner uploads a trip file');
reset role;
select set_config('request.jwt.claim.sub', :'deniz', false);
set role authenticated;
select test.eq((select count(*) from storage.objects), 0::bigint, 'a stranger cannot see it');
do $$ begin
    insert into storage.objects (bucket_id, name) values ('trip-files', 'trips/11111111-1111-1111-1111-111111111111/b.pdf');
    raise exception 'FAILED: stranger uploaded';
exception when insufficient_privilege then raise notice 'ok - a stranger cannot upload';
end $$;
do $$ begin
    insert into storage.objects (bucket_id, name) values ('trip-files', 'elsewhere/x.pdf');
    raise exception 'FAILED: upload outside trips/';
exception when insufficient_privilege then raise notice 'ok - nothing outside trips/<trip_id>/';
end $$;

-- ------------------------------------------------------------------------------- TP-3: usage limits (migration 13)
-- Small test limits: private.quota_limit reads test.quota_override first.
reset role;
create table test.quota_override (key text primary key, value bigint);
grant select on test.quota_override to authenticated;
alter function private.quota_limit(text) rename to quota_limit_real;
create function private.quota_limit(p_key text) returns bigint language sql stable set search_path = '' as $$
    select coalesce((select o.value from test.quota_override o where o.key = p_key), private.quota_limit_real(p_key))
$$;
grant execute on function private.quota_limit(text) to authenticated;
insert into test.quota_override values ('expenses', 3);

\set t2 'aaaaaaaa-0000-0000-0000-000000000002'
\set t3 'aaaaaaaa-0000-0000-0000-000000000003'
\set t4 'aaaaaaaa-0000-0000-0000-000000000004'
\set e1 'eeeeeeee-0000-0000-0000-000000000001'
\set e2 'eeeeeeee-0000-0000-0000-000000000002'
\set e3 'eeeeeeee-0000-0000-0000-000000000003'
\set e4 'eeeeeeee-0000-0000-0000-000000000004'
\set e5 'eeeeeeee-0000-0000-0000-000000000005'

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table if exists r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t2', 'full', true, 'fields',
        jsonb_build_object('name', 'Paris', 'start_date', '2026-09-01', 'end_date', '2026-09-03')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e1', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 100, 'date', '2026-09-01')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e2', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 200, 'date', '2026-09-01')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e3', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 300, 'date', '2026-09-01')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e4', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 400, 'date', '2026-09-01'))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'status' = 'applied'), 4::bigint, 'quota: trip + 3 expenses fit');
select test.eq((select test.status(res, 4) || '/' || test.reason(res, 4) from r), 'rejected/quota', 'quota: the 4th expense of a trip is refused as "quota"');
select test.eq((select res -> 4 ->> 'error' from r), 'quota: at most 3 expenses per trip', 'quota: the error names the limit');
select test.eq((select count(*) from public.expenses where trip_id = :'t2'), 3::bigint, 'quota: nothing of the refused change is stored');

-- the trash: a deleted row frees a live place; bringing it back needs one again
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'delete', 'id', :'e3', 'trip_id', :'t2')))), 'deleted', 'quota: delete one expense');
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e4', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 400, 'date', '2026-09-01'))))), 'applied', 'quota: then a new one fits');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e3', 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 300, 'date', '2026-09-01'))))), 'quota', 'quota: restoring from the trash counts as well');
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e1', 'trip_id', :'t2',
        'fields', jsonb_build_object('amount_minor', 150))))), 'applied', 'quota: editing at the limit is fine');
-- trash counts up to twice the limit: 1 live + 5 deleted = 6 rows, then even free live places don't help
reset role;
update public.expenses set deleted_at = now() where id in (:'e1', :'e2');
insert into public.expenses (id, trip_id, amount_minor, date, deleted_at)
values (:'e5', :'t2', 1, '2026-09-01', now()), (gen_random_uuid(), :'t2', 1, '2026-09-01', now());
set role authenticated;
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'t2', 'full', true,
        'fields', jsonb_build_object('amount_minor', 1, 'date', '2026-09-01'))))), 'quota', 'quota: a full trash counts too');

-- trips per account
reset role;
insert into test.quota_override
select 'trips', count(*) from public.trips where owner_id = :'ali' and deleted_at is null;
set role authenticated;
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t3', 'full', true, 'fields',
        jsonb_build_object('name', 'Lyon', 'start_date', '2026-09-01', 'end_date', '2026-09-03'))))), 'quota', 'quota: trips per account');
reset role;
delete from test.quota_override where key = 'trips';

-- new rows per account per day
insert into test.quota_override
select 'new_rows_per_day', day_rows + 1 from private.sync_usage where user_id = :'ali';
set role authenticated;
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t3', 'full', true, 'fields',
        jsonb_build_object('name', 'Lyon', 'start_date', '2026-09-01', 'end_date', '2026-09-03'))))), 'applied', 'quota: under the daily row limit');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'t3', 'full', true,
        'fields', jsonb_build_object('title', 'x'))))), 'quota', 'quota: new rows per account per day');
reset role;
delete from test.quota_override where key = 'new_rows_per_day';

-- calls and changes per minute (HTTP 429)
insert into test.quota_override values ('calls_per_minute', 0);
set role authenticated;
do $$ begin
    perform public.apply_changes('[]'::jsonb);
    raise exception 'FAILED: a call over the per-minute limit went through';
exception when sqlstate 'PT429' then raise notice 'ok - too many calls in a minute get 429';
end $$;
reset role;
update test.quota_override set key = 'changes_per_minute' where key = 'calls_per_minute';
insert into test.quota_override values ('calls_per_minute', 1000);  -- this script makes many calls a minute
delete from private.sync_usage;                                     -- start the minute from 0 changes
set role authenticated;
select test.eq(jsonb_array_length(public.apply_changes('[]'::jsonb)), 0, 'rate: a call without changes fits 0 changes a minute');
do $$ begin
    perform public.apply_changes(jsonb_build_array(jsonb_build_object('entity', 'Todo', 'op', 'delete', 'id', gen_random_uuid())));
    raise exception 'FAILED: changes over the per-minute limit went through';
exception when sqlstate 'PT429' then raise notice 'ok - too many changes in a minute get 429';
end $$;
reset role;
delete from test.quota_override where key in ('changes_per_minute', 'calls_per_minute');

-- Storage: MIME types, size and files per trip, size per account
select test.eq((select allowed_mime_types from storage.buckets where id = 'trip-files'),
               array['image/jpeg', 'image/png', 'image/heic', 'image/heif', 'image/webp', 'application/pdf'],
               'storage: only photos and PDFs are accepted');
insert into test.quota_override values ('storage_bytes_per_trip', 1000), ('storage_files_per_trip', 3), ('storage_bytes_per_account', 1500);
set role authenticated;
insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/' || :'t2' || '/a.pdf', '{"size": 600}');
do $$ begin
    insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/aaaaaaaa-0000-0000-0000-000000000002/b.pdf', '{"size": 500}');
    raise exception 'FAILED: a trip went over its storage size';
exception when insufficient_privilege then raise notice 'ok - a trip cannot go over its storage size';
end $$;
insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/' || :'t2' || '/b.pdf', '{"size": 300}');
update storage.objects set metadata = '{"size": 700}' where name = 'trips/' || :'t2' || '/a.pdf';
select test.eq((select sum((metadata ->> 'size')::bigint) from storage.objects where name like 'trips/' || :'t2' || '/%'), 1000::numeric,
               'storage: replacing a file counts only its new size');
do $$ begin
    update storage.objects set metadata = '{"size": 701}' where name = 'trips/aaaaaaaa-0000-0000-0000-000000000002/a.pdf';
    raise exception 'FAILED: a replaced file went over the trip size';
exception when insufficient_privilege then raise notice 'ok - a replaced file cannot go over the trip size';
end $$;
reset role;
update test.quota_override set value = 2000 where key = 'storage_bytes_per_trip';
set role authenticated;
insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/' || :'t2' || '/c.pdf', '{"size": 10}');
do $$ begin
    insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/aaaaaaaa-0000-0000-0000-000000000002/d.pdf', '{"size": 1}');
    raise exception 'FAILED: a trip went over its file count';
exception when insufficient_privilege then raise notice 'ok - a trip cannot go over its file count';
end $$;
-- the account: 1010 bytes in Paris, Lyon gets the rest of 1500
do $$ begin
    insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/aaaaaaaa-0000-0000-0000-000000000003/x.pdf', '{"size": 491}');
    raise exception 'FAILED: an account went over its storage size';
exception when insufficient_privilege then raise notice 'ok - an account cannot go over its storage size';
end $$;
insert into storage.objects (bucket_id, name, metadata) values ('trip-files', 'trips/' || :'t3' || '/x.pdf', '{"size": 490}');
-- full account: refused before the upload already (no size known yet)
do $$ begin
    insert into storage.objects (bucket_id, name) values ('trip-files', 'trips/aaaaaaaa-0000-0000-0000-000000000003/y.pdf');
    raise exception 'FAILED: an upload to a full account was not refused up front';
exception when insufficient_privilege then raise notice 'ok - a full account is refused before the upload';
end $$;
reset role;
delete from test.quota_override where key like 'storage%';

-- text limits
set role authenticated;
select test.eq((select test.status(res) || '/' || test.reason(res) from (select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'PackingTemplate', 'op', 'upsert', 'id', gen_random_uuid(), 'full', true,
                       'fields', jsonb_build_object('name', 'Uzun', 'items', repeat('x', 32001))))) as res) q),
               'rejected/invalid', 'packing template items are at most 32 000 characters');
update public.profiles set avatar_url = 'https://example.com/a.png' where id = :'ali';
do $$ begin
    update public.profiles set avatar_url = 'javascript:alert(1)' where id = '00000000-0000-0000-0000-00000000a111';
    raise exception 'FAILED: a non-https avatar_url was stored';
exception when check_violation then raise notice 'ok - avatar_url must be https://';
end $$;
do $$ begin
    update public.profiles set avatar_url = 'https://' || repeat('x', 493) where id = '00000000-0000-0000-0000-00000000a111';
    raise exception 'FAILED: a 501 character avatar_url was stored';
exception when check_violation then raise notice 'ok - avatar_url is at most 500 characters';
end $$;
reset role;
insert into auth.users (id, email, raw_user_meta_data) values
    ('00000000-0000-0000-0000-00000000a555', 'odd@example.com', '{"avatar_url": "http://insecure.example.com/a.png"}'),
    ('00000000-0000-0000-0000-00000000a666', 'pic@example.com', '{"avatar_url": "https://lh3.example.com/a.png"}');
select test.eq((select avatar_url from public.profiles where email = 'odd@example.com'), null::text, 'sign-up drops an odd provider avatar');
select test.eq((select avatar_url from public.profiles where email = 'pic@example.com'), 'https://lh3.example.com/a.png', 'sign-up keeps an https avatar');
delete from auth.users where id in ('00000000-0000-0000-0000-00000000a555', '00000000-0000-0000-0000-00000000a666');

-- back to the real limits; the test trips go
drop function private.quota_limit(text);
alter function private.quota_limit_real(text) rename to quota_limit;
drop table test.quota_override;
delete from private.sync_usage;  -- the rest of the script calls quickly again
delete from storage.objects where name like 'trips/aaaaaaaa-%';
delete from public.trips where id in (:'t2', :'t3');
select test.eq(private.quota_limit('expenses'), 2000::bigint, 'the real limits are back');

-- ------------------------------------------------------------------------------- trip delete, purge, account delete
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'delete', 'id', :'trip')))), 'deleted', 'owner deletes the trip');
select test.eq(test.reason(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                       'fields', jsonb_build_object('title', 'x'))))), 'trip_deleted', 'nothing can be added to a deleted trip');

reset role;
update public.trips set deleted_at = now() - interval '31 days' where id = :'trip';
select private.purge_deleted_rows();
select test.eq((select count(*) from public.trips where id = :'trip'), 0::bigint, 'purge removes a trip deleted 31 days ago');
select test.eq((select count(*) from public.activities where trip_id = :'trip'), 0::bigint, 'with everything in it');

-- AI planner quota: 5 a day per account AND per device, then HTTP 429 (PT429).
-- (these accounts are a month old: new accounts get fewer plans, tested below)
update auth.users set created_at = now() - interval '30 days';
\set dev1 '1111111111111111111111111111111111111111111111111111111111111111'
\set dev2 '2222222222222222222222222222222222222222222222222222222222222222'
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select test.eq((public.ai_plan_quota_status(:'dev1') ->> 'remaining')::int, 5, 'ai quota: 5 plans a day');
select public.ai_plan_quota(:'dev1') from generate_series(1, 3);
select test.eq((public.ai_plan_quota_status(:'dev1') ->> 'remaining')::int, 2, 'ai quota: 3 used');
-- Can signs in on the same phone: the device has only 2 left for him too
select set_config('request.jwt.claim.sub', :'can', false);
select test.eq((public.ai_plan_quota_status(:'dev1') ->> 'remaining')::int, 2, 'ai quota: another account on the same device shares its quota');
select public.ai_plan_quota(:'dev1') from generate_series(1, 2);
do $$ begin
    perform public.ai_plan_quota('1111111111111111111111111111111111111111111111111111111111111111');
    raise exception 'FAILED: a sixth plan on one device was allowed';
exception when sqlstate 'PT429' then raise notice 'ok - the device is out of plans for every account (429)';
end $$;
-- ...but on his own phone he still has 3 (5 minus the 2 he used)
select test.eq((public.ai_plan_quota_status(:'dev2') ->> 'remaining')::int, 3, 'ai quota: the account counts too');
select public.ai_plan_quota(:'dev2') from generate_series(1, 3);
do $$ begin
    perform public.ai_plan_quota('2222222222222222222222222222222222222222222222222222222222222222');
    raise exception 'FAILED: a sixth plan for one account was allowed';
exception when sqlstate 'PT429' then raise notice 'ok - the account is out of plans on any device (429)';
end $$;
do $$ begin
    perform public.ai_plan_quota('not-a-hash');
    raise exception 'FAILED: a bad device id was accepted';
exception when sqlstate '22023' then raise notice 'ok - device ids must be SHA-256 hashes';
end $$;
reset role;
select test.eq((select count from private.ai_device_usage where device = :'dev1'), 5, 'ai quota: refused calls are not counted');

-- A failed AI request gives its plan back - once, only with its own ticket, only for its own account.
\set dev3 '3333333333333333333333333333333333333333333333333333333333333333'
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select (public.ai_plan_quota(:'dev3') ->> 'ticket')::uuid as ticket \gset
select test.eq((public.ai_plan_quota_status(:'dev3') ->> 'remaining')::int, 1, 'ai refund: ayse has 1 plan left (4 used)');
select set_config('request.jwt.claim.sub', :'can', false);
select test.eq(public.ai_plan_refund(:'ticket'), false, 'ai refund: someone else''s ticket gives nothing back');
select set_config('request.jwt.claim.sub', :'ayse', false);
select test.eq(public.ai_plan_refund(:'ticket'), true, 'ai refund: the plan comes back');
select test.eq((public.ai_plan_quota_status(:'dev3') ->> 'remaining')::int, 2, 'ai refund: 2 left again');
select test.eq(public.ai_plan_refund(:'ticket'), false, 'ai refund: a ticket works only once');
select test.eq(public.ai_plan_refund(gen_random_uuid()), false, 'ai refund: an unknown ticket gives nothing back');
reset role;
update private.ai_quota_ticket set created_at = now() - interval '11 minutes';
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select (public.ai_plan_quota(:'dev3') ->> 'ticket')::uuid as ticket2 \gset
reset role;
update private.ai_quota_ticket set created_at = now() - interval '11 minutes' where id = :'ticket2';
set role authenticated;
select test.eq(public.ai_plan_refund(:'ticket2'), false, 'ai refund: an old ticket gives nothing back');
reset role;

-- SEC-02: at most 2 refunds per account and day (ayse already got 1 back above).
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select (public.ai_plan_quota(:'dev3') ->> 'ticket')::uuid as ticket3 \gset
select test.eq(public.ai_plan_refund(:'ticket3'), true, 'ai refund: the second refund of the day comes back');
select (public.ai_plan_quota(:'dev3') ->> 'ticket')::uuid as ticket4 \gset
select test.eq(public.ai_plan_refund(:'ticket4'), false, 'ai refund: no third refund on the same day');
select test.eq((public.ai_plan_quota_status(:'dev3') ->> 'remaining')::int, 0, 'ai refund: the refused refund keeps the plan used');
reset role;

-- SEC-02: an account in its first 24 hours gets only 2 plans.
\set eda  '00000000-0000-0000-0000-00000000a555'
\set dev4 '4444444444444444444444444444444444444444444444444444444444444444'
\set dev5 '5555555555555555555555555555555555555555555555555555555555555555'
insert into auth.users (id, email) values (:'eda', 'eda@example.com');
select set_config('request.jwt.claim.sub', :'eda', false);
set role authenticated;
select test.eq((public.ai_plan_quota_status(:'dev4') ->> 'limit')::int, 2, 'ai quota: a new account has a limit of 2');
select public.ai_plan_quota(:'dev4') from generate_series(1, 2);
do $$ begin
    perform public.ai_plan_quota('5555555555555555555555555555555555555555555555555555555555555555');
    raise exception 'FAILED: a new account got a third plan (with a fresh device id)';
exception when sqlstate 'PT429' then raise notice 'ok - a new account is out of plans after 2, whatever device id it sends (429)';
end $$;
reset role;
update auth.users set created_at = now() - interval '25 hours' where id = :'eda';
select set_config('request.jwt.claim.sub', :'eda', false);
set role authenticated;
select test.eq((public.ai_plan_quota_status(:'dev5') ->> 'remaining')::int, 3, 'ai quota: after 24 hours the normal limit applies');
reset role;

-- SEC-02: the global ceiling stops everyone (429), also accounts and devices with plans left.
update private.ai_global_usage set count = private.ai_global_daily_limit() - 1 where day = current_date;
select set_config('request.jwt.claim.sub', :'eda', false);
set role authenticated;
select (public.ai_plan_quota(:'dev5') ->> 'ticket')::uuid as ticket5 \gset
select test.eq((public.ai_plan_quota_status(:'dev5') ->> 'remaining')::int, 0, 'ai global: status shows 0 when the ceiling is reached');
select set_config('request.jwt.claim.sub', :'deniz', false);
do $$ begin
    perform public.ai_plan_quota('6666666666666666666666666666666666666666666666666666666666666666');
    raise exception 'FAILED: a plan past the global ceiling was allowed';
exception when sqlstate 'PT429' then raise notice 'ok - past the global ceiling everyone gets 429';
end $$;
select set_config('request.jwt.claim.sub', :'eda', false);
select test.eq(public.ai_plan_refund(:'ticket5'), true, 'ai global: a refund frees the global plan too');
select set_config('request.jwt.claim.sub', :'deniz', false);
select test.eq((public.ai_plan_quota('6666666666666666666666666666666666666666666666666666666666666666') ->> 'remaining')::int, 4,
               'ai global: after the refund the next user gets it');
reset role;
select test.eq((select count from private.ai_global_usage where day = current_date), private.ai_global_daily_limit(),
               'ai global: refused calls are not counted');

-- Banning: the account state is public (state only), RPCs refuse at once, sessions are gone.
select test.eq(public.account_status(:'can'), 'active', 'account_status: active');
insert into auth.sessions (user_id) values (:'can');
select private.ban_user('CAN@example.com');
select test.eq(public.account_status(:'can'), 'banned', 'account_status: banned');
select test.eq((select count(*) from auth.sessions where user_id = :'can'), 0::bigint, 'ban_user signs out every device');
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
do $$ begin
    perform public.pull_changes(0);
    raise exception 'FAILED: a banned account could still sync';
exception when sqlstate '28000' then raise notice 'ok - a banned account is refused by the RPCs';
end $$;
reset role;
select private.unban_user('can@example.com');
select test.eq(public.account_status(:'can'), 'active', 'unban_user lets him in again');
select test.eq(public.account_status('00000000-0000-0000-0000-0000000000ee'), 'deleted', 'account_status: deleted');

-- A file Ali uploaded to someone else's trip must not block deleting his account (Supabase refuses to delete
-- users who own Storage objects) - it stays for that trip, without an owner.
insert into storage.objects (bucket_id, name, owner, owner_id)
values ('trip-files', 'trips/00000000-0000-0000-0000-0000000000ff/foto.jpg', :'ali', :'ali');

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select public.delete_account();
reset role;
select test.eq((select count(*) from auth.users where id = :'ali'), 0::bigint, 'delete_account removes the account');
select test.eq(public.account_status(:'ali'), 'deleted', 'and account_status reports it');
select test.eq((select count(*) from public.trips), 0::bigint, 'and the trips it owned');
select test.eq((select count(*) from public.packing_templates), 0::bigint, 'and its packing templates');
select test.eq((select owner from storage.objects where name like '%/foto.jpg'), null::uuid, 'his uploads elsewhere lose their owner');
select test.eq((select count(*) from storage.objects where name like '%/foto.jpg'), 1::bigint, 'but stay for the other trip');

\echo ALL SYNC TESTS PASSED
