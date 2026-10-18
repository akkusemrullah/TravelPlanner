-- LOCAL TESTING ONLY - run after 00_local_stub.sql and the migrations, on a throw-away database:
--   psql -v ON_ERROR_STOP=1 -d tp -f tests/sync_tests.sql
-- Each check raises (and stops the script) when it fails; the last line prints "ALL SYNC TESTS PASSED".
\set ON_ERROR_STOP 1
\set QUIET 1

create schema if not exists test;
grant usage on schema test to authenticated, service_role;

create or replace function test.eq(p_actual anyelement, p_expected anyelement, p_what text) returns void
language plpgsql as $$
begin
    if p_actual is distinct from p_expected then
        raise exception 'FAILED: % (expected %, got %)', p_what, p_expected, p_actual;
    end if;
    raise notice 'ok - %', p_what;
end $$;
grant execute on function test.eq(anyelement, anyelement, text) to authenticated, service_role;

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
select test.eq((select role from public.accept_invite((select lower(code) from editor_invite))), 'viewer', 'TP-4: Ayşe joins as viewer even from an "editor" invite (code is case-insensitive)');

reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select public.set_member_role(:'trip', :'ayse', 'editor');
select test.eq((select role from public.trip_members where trip_id = :'trip' and user_id = :'ayse'), 'editor', 'TP-4: the owner makes Ayşe an editor afterwards');

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
select test.eq((select count(*) from public.accept_invite((select code from viewer_invite))), 0::bigint, 'a single-use invite works once (a used code returns no rows)');
select test.eq((select count(*) from public.trip_members where trip_id = :'trip' and user_id = :'deniz'), 0::bigint, 'and Deniz did not join');

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

-- ------------------------------------------------------------------------------- TP-4: invites and e-mails (migration 14)
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
-- what an old app version asks for: editor, 7 days, unlimited
create temp table old_app_invite as select code from public.create_invite(:'trip', 'editor', 168, null);
select test.eq((select i.role from public.trip_invites i join old_app_invite x using (code)), 'viewer', 'TP-4: an old app''s editor invite becomes a viewer invite');
select test.eq((select i.max_uses from public.trip_invites i join old_app_invite x using (code)), 1, 'TP-4: ... single-use');
select test.eq((select i.expires_at <= now() + interval '48 hours' from public.trip_invites i join old_app_invite x using (code)), true, 'TP-4: ... valid for 48 hours at most');
create temp table default_invite as select code from public.create_invite(:'trip');
select test.eq((select i.role || '/' || i.max_uses from public.trip_invites i join default_invite x using (code)), 'viewer/1', 'TP-4: the default invite is single-use, viewer');
create temp table group_invite as select code from public.create_invite(:'trip', 'viewer', 48, 10);
select test.eq((select i.max_uses from public.trip_invites i join group_invite x using (code)), 10, 'TP-4: a group code is asked for on purpose (10 uses)');
do $$ begin
    perform public.create_invite('11111111-1111-1111-1111-111111111111', 'viewer', 48, 11);
    raise exception 'FAILED: an invite for 11 people was created';
exception when sqlstate '22023' then raise notice 'ok - TP-4: at most 10 uses per code';
end $$;
-- an invite handed out before migration 14 is cut down when the migration runs
reset role;
insert into public.trip_invites (trip_id, code, role, created_by, expires_at, max_uses, used_count)
values (:'trip', 'OLDCODE2', 'editor', :'ali', now() + interval '6 days', null, 3);
update public.trip_invites i
set role = 'viewer', max_uses = least(coalesce(i.max_uses, i.used_count + 1), i.used_count + 1),
    expires_at = least(i.expires_at, now() + make_interval(hours => private.invite_limit('valid_hours')))
where i.code = 'OLDCODE2';   -- the statement from migration 14
select test.eq((select role || '/' || max_uses from public.trip_invites where code = 'OLDCODE2'), 'viewer/4', 'TP-4: an old open invite gets one more use, as viewer');
delete from public.trip_invites where code = 'OLDCODE2';

-- Can (viewer) and Ayşe (editor) see names, but no one else's e-mail; Ali (owner) sees them all.
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((select count(*) from public.get_trip_members(:'trip') where email is not null), 1::bigint, 'TP-4: a viewer gets no e-mails but his own');
select test.eq((select email from public.get_trip_members(:'trip') where user_id = :'can'), 'can@example.com', 'TP-4: ... his own is there');
select test.eq((select display_name from public.get_trip_members(:'trip') where user_id = :'ali'), 'Ali Yılmaz', 'TP-4: ... names still are');
select test.eq((select count(*) from (select id, display_name, avatar_url from public.profiles) p), 3::bigint, 'TP-4: trip mates'' names and photos can be read');
do $$ begin
    perform email from public.profiles;
    raise exception 'FAILED: a trip mate read profiles.email';
exception when insufficient_privilege then raise notice 'ok - TP-4: profiles.email is closed to the API';
end $$;
reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
select test.eq((select count(*) from public.get_trip_members(:'trip') where email is not null), 1::bigint, 'TP-4: an editor gets no e-mails but her own');
select test.eq((select count(*) from public.get_recent_joins(null)), 0::bigint, 'TP-4: only the owner hears about new members');
reset role;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq((select count(*) from public.get_trip_members(:'trip') where email is not null), 3::bigint, 'TP-4: the owner sees every e-mail');
select test.eq((select count(*) from public.get_recent_joins(null)), 2::bigint, 'TP-4: the owner hears that Ayşe and Can joined');
select test.eq((select count(*) from public.get_recent_joins(now())), 0::bigint, 'TP-4: ... and nothing newer');

-- Can already is a member: typing a used code again gives the trip back and costs nothing
reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((select role from public.accept_invite((select code from viewer_invite))), 'viewer', 'TP-4: a member re-entering a used code gets the trip back');
reset role;
select test.eq((select count(*) from private.invite_attempts where user_id = :'can'), 0::bigint, 'TP-4: ... and it is not counted as a wrong code');

-- Deniz guesses: 10 wrong codes in an hour, then 429 for an hour - even for a good code
select set_config('request.jwt.claim.sub', :'deniz', false);
set role authenticated;
select count(*) from generate_series(2, 8) n cross join lateral public.accept_invite('ZZZZZZZ' || n);  -- 1 above + 7 wrong
select test.eq((select count(*) from public.accept_invite('ZZZZZZZZ')), 0::bigint, 'TP-4: a wrong code returns no rows (9th)');
select test.eq((select count(*) from public.accept_invite('ZZZZZZZY')), 0::bigint, 'TP-4: the 10th wrong code still answers');
do $$ begin
    perform public.accept_invite((select code from group_invite));
    raise exception 'FAILED: an 11th try was allowed';
exception when sqlstate 'PT429' then raise notice 'ok - TP-4: after 10 wrong codes the account waits an hour (429)';
end $$;
reset role;
select test.eq((select blocked_until > now() + interval '59 minutes' from private.invite_attempts where user_id = :'deniz'), true, 'TP-4: blocked for an hour');
update private.invite_attempts set blocked_until = now() - interval '1 second' where user_id = :'deniz';
set role authenticated;
select test.eq((select role from public.accept_invite((select code from group_invite))), 'viewer', 'TP-4: after the hour a good code works again');
select public.leave_trip(:'trip');   -- Deniz is the stranger again for the tests below
reset role;
select test.eq((select used_count from public.trip_invites where code = (select code from group_invite)), 1, 'TP-4: a group code counts its uses');

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

-- ------------------------------------------------------------------------------- TP-5: attachment paths and types (migration 15)
\set att1 'aaaaaaaa-0000-0000-0000-000000000001'
\set att2 'aaaaaaaa-0000-0000-0000-000000000002'
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('display_name', 'bilet.pdf', 'kind', 1, 'content_type', 'application/pdf',
                                     'storage_path', 'trips/' || :'trip' || '/' || :'att1' || '.pdf')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip', 'full', true,
        'fields', jsonb_build_object('display_name', 'foto', 'kind', 0, 'content_type', 'image/heif'))
)) as res;
select test.eq(test.status(res, 0), 'applied', 'TP-5: a row pointing at its own file is accepted') from r;
select test.eq(test.status(res, 1), 'applied', 'TP-5: a row without a file yet (storage_path null) is accepted') from r;

-- Ayşe (editor) talks to the REST API directly
reset role;
select set_config('request.jwt.claim.sub', :'ayse', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    /* 0 another row's file   */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att1' || '.pdf')),
    /* 1 another trip's file  */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/99999999-9999-9999-9999-999999999999/' || :'att2' || '.pdf')),
    /* 2 ".." in the path     */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/../../../rest/v1/profiles')),
    /* 3 "?" in the path      */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.pdf?x=1')),
    /* 4 "#" in the path      */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.pdf#x')),
    /* 5 no extension         */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2')),
    /* 6 upper-case extension */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.PDF')),
    /* 7 extension too long   */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.abcdefghijk')),
    /* 8 trailing new line    */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.pdf' || chr(10))),
    /* 9 APK type             */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('content_type', 'application/vnd.android.package-archive')),
    /*10 HTML type            */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('content_type', 'text/html')),
    /*11 parameters           */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('content_type', 'application/pdf; charset=binary')),
    /*12 upper case           */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip',
                                     'fields', jsonb_build_object('content_type', 'APPLICATION/PDF')),
    /*13 a new row, bad path  */ jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', gen_random_uuid(), 'trip_id', :'trip', 'full', true,
                                     'fields', jsonb_build_object('display_name', 'sahte.pdf', 'kind', 1, 'content_type', 'application/pdf',
                                                                  'storage_path', 'trips/' || :'trip' || '/' || :'att1' || '.pdf'))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'reason' = 'invalid'), 14::bigint,
               'TP-5: every foreign/broken storage_path and every type outside the list is refused as "invalid"');
select test.eq((select storage_path from public.attachments where id = :'att2'), null::text, 'TP-5: the photo row still has no file');
select test.eq((select content_type from public.attachments where id = :'att1'), 'application/pdf', 'TP-5: the ticket keeps its type');
select test.eq((select count(*) from public.attachments where trip_id = :'trip'), 2::bigint, 'TP-5: no fake row was added');

drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att2', 'trip_id', :'trip',
        'fields', jsonb_build_object('storage_path', 'trips/' || :'trip' || '/' || :'att2' || '.heif', 'content_type', 'image/heif')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'att1', 'trip_id', :'trip',
        'fields', jsonb_build_object('content_type', null))
)) as res;
select test.eq(test.status(res, 0), 'applied', 'TP-5: an editor may record the row''s own file') from r;
select test.eq(test.status(res, 1), 'applied', 'TP-5: content_type may be empty') from r;

-- Old rows are cleaned up when the migration runs again (it is safe to repeat).
reset role;
alter table public.attachments drop constraint attachments_storage_path_format;
alter table public.attachments drop constraint attachments_content_type_allowed;
update public.attachments set storage_path = 'trips/' || :'trip' || '/' || :'att1' || '.pdf', content_type = 'image/JPG' where id = :'att2';
update public.attachments set content_type = 'application/x-msdownload' where id = :'att1';
\ir ../migrations/20261017000015_attachment_paths.sql
select test.eq((select storage_path from public.attachments where id = :'att2'), null::text, 'TP-5: an old foreign storage_path is cleared');
select test.eq((select content_type from public.attachments where id = :'att2'), 'image/jpeg', 'TP-5: an old image/JPG becomes image/jpeg');
select test.eq((select content_type from public.attachments where id = :'att1'), null::text, 'TP-5: an old type outside the list is cleared');
select test.eq((select count(*) from pg_constraint where conname in ('attachments_storage_path_format', 'attachments_content_type_allowed')),
               2::bigint, 'TP-5: both rules are back');

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

-- ------------------------------------------------------------------------------- TP-8: slow concurrent writes
-- The scenario: a sync call (session B) writes a to-do and stays open - waiting on row locks, say - for more
-- than 10 seconds. Meanwhile Ali writes another to-do: it gets a HIGHER change_seq and commits first. Before
-- TP-8 the viewer's cursor moved past Ali's row once it was 10 s old, so B's row (lower change_seq, committed
-- later) never reached the viewer. Now the cursor stops before anything an open transaction may still commit.
reset role;
select test.eq((select proconfig @> array['lock_timeout=2s'] from pg_proc where oid = 'public.apply_changes(jsonb)'::regprocedure),
               true, 'TP-8: apply_changes waits at most 2 s for a row lock');
select test.eq(private.pull_safe_before() <= now() - interval '10 seconds', true, 'TP-8: the safe point is at least 10 s back');
do $$
declare v_first timestamptz; v_second timestamptz;
begin
    update public.todos set title = 'Pasaport' where id = '77777777-7777-7777-7777-777777777777' returning updated_at into v_first;
    perform pg_sleep(0.05);
    update public.todos set title = 'Pasaport' where id = '77777777-7777-7777-7777-777777777777' returning updated_at into v_second;
    perform test.eq(v_second > v_first and v_first > now(), true, 'TP-8: updated_at is the write time, not the transaction start');
end $$;

-- The scenario itself needs a second session (dblink); without it this part is skipped. It waits 10.5 s.
select exists (select 1 from pg_available_extensions where name = 'dblink') as has_dblink \gset
\if :has_dblink
create extension if not exists dblink;
\set quick 'cccccccc-0000-0000-0000-000000000008'
select dblink_connect('tp8', format('dbname=%s port=%s', current_database(), current_setting('port')));
select dblink_exec('tp8', 'begin');
select dblink_exec('tp8', format('update public.todos set title = %L where id = %L', 'Yavaş yazma', :'todo'));
drop table if exists slow;
create temp table slow as
    select seq from dblink('tp8', format('select change_seq from public.todos where id = %L', :'todo')) as t(seq bigint);
grant select on slow to authenticated;

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq(test.status(public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Todo', 'op', 'upsert', 'id', :'quick', 'trip_id', :'trip', 'full', true,
                       'fields', jsonb_build_object('title', 'Hızlı yazma', 'is_completed', false))))), 'applied',
               'TP-8: Ali writes a to-do while the slow call is still open');
reset role;
select test.eq((select change_seq from public.todos where id = :'quick') > (select seq from slow), true,
               'TP-8: his row has the higher change_seq but commits first');
select pg_sleep(10.5);

select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
drop table if exists p8;
create temp table p8 as select public.pull_changes((select cursor from c)) as res;
select test.eq((select count(*) from jsonb_array_elements(res -> 'todos') r where r ->> 'id' = :'quick'), 1::bigint,
               'TP-8: the viewer gets Ali''s row') from p8;
select test.eq((res ->> 'cursor')::bigint < (select seq from slow), true,
               'TP-8: but the cursor stays before the open call''s row') from p8;

reset role;
select dblink_exec('tp8', 'commit');
select dblink_disconnect('tp8');
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq((select count(*) from jsonb_array_elements(public.pull_changes((select (res ->> 'cursor')::bigint from p8)) -> 'todos') r
                where r ->> 'title' = 'Yavaş yazma'), 1::bigint, 'TP-8: the slow row arrives once it commits');
reset role;
\else
\echo 'skipped - TP-8: dblink is not installed, the slow concurrent write scenario did not run'
\endif

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

-- Banning: RPCs refuse at once, sessions are gone (state checked with private.account_state; TP-14: account_status is only for the account itself).
select test.eq(private.account_state(:'can'), 'active', 'account_status: active');
insert into auth.sessions (user_id) values (:'can');
select private.ban_user('CAN@example.com');
select test.eq(private.account_state(:'can'), 'banned', 'account_status: banned');
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
select test.eq(private.account_state(:'can'), 'active', 'unban_user lets him in again');
select test.eq(private.account_state('00000000-0000-0000-0000-0000000000ee'), 'deleted', 'account_status: deleted');

-- TP-14: account_status answers only the signed-in account about itself
set role anon;
do $$ begin
    perform public.account_status('00000000-0000-0000-0000-00000000a333');
    raise exception 'FAILED: anon could read an account status';
exception when insufficient_privilege then raise notice 'ok - TP-14: anon cannot call account_status';
end $$;
reset role;
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
select test.eq(public.account_status(:'can'), 'active', 'TP-14: an account can read its own status');
do $$ begin
    perform public.account_status('00000000-0000-0000-0000-00000000a111');
    raise exception 'FAILED: an account read someone else''s status';
exception when insufficient_privilege then raise notice 'ok - TP-14: not someone else''s';
end $$;
reset role;
-- a token issued before the account was deleted still learns that it is gone
select set_config('request.jwt.claim.sub', '00000000-0000-0000-0000-0000000000ee', false);
set role authenticated;
select test.eq(public.account_status('00000000-0000-0000-0000-0000000000ee'), 'deleted', 'TP-14: a deleted account''s old token sees "deleted"');
reset role;

-- TP-14: a refused change carries a short code, not the database message
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
drop table if exists r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', 'bbbbbbbb-0000-0000-0000-000000000014', 'full', true, 'fields',
        jsonb_build_object('name', 'Kötü', 'start_date', '2026-11-05', 'end_date', '2026-11-01')))) as res;
select test.eq(test.reason((select res from r)), 'invalid', 'TP-14: an impossible trip is refused');
select test.eq((select res -> 0 ->> 'error' from r), 'out_of_range', 'TP-14: with only a short code');
reset role;
select test.eq(private.error_code('23505'), 'duplicate', 'TP-14: unique violation -> duplicate');
select test.eq(private.error_code('22P02'), 'bad_value', 'TP-14: bad input -> bad_value');
select test.eq(private.error_code('XX000'), 'error', 'TP-14: anything else -> error');

-- ------------------------------------------------------------------------------- G7: files of deleted attachments (migration 16)
\set t7     'bbbbbbbb-0000-0000-0000-000000000007'
\set fa     'bbbbbbbb-0000-0000-0000-0000000000a1'
\set fb     'bbbbbbbb-0000-0000-0000-0000000000b1'
\set fc     'bbbbbbbb-0000-0000-0000-0000000000c1'
\set ghost  'bbbbbbbb-0000-0000-0000-0000000000d1'
\set fresh  'bbbbbbbb-0000-0000-0000-0000000000e1'
reset role;
delete from private.file_cleanup_queue;
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table if exists r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t7', 'full', true, 'fields',
        jsonb_build_object('name', 'Lizbon', 'start_date', '2026-09-01', 'end_date', '2026-09-03', 'currency_code', 'EUR')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'fa', 'trip_id', :'t7', 'full', true,
        'fields', jsonb_build_object('display_name', 'bilet.pdf', 'kind', 1, 'storage_path', 'trips/' || :'t7' || '/' || :'fa' || '.pdf')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'fb', 'trip_id', :'t7', 'full', true,
        'fields', jsonb_build_object('display_name', 'foto', 'kind', 0, 'storage_path', 'trips/' || :'t7' || '/' || :'fb' || '.jpg')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'fc', 'trip_id', :'t7', 'full', true,
        'fields', jsonb_build_object('display_name', 'not uploaded yet', 'kind', 0))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'status' = 'applied'), 4::bigint, 'G7: trip with three attachments');
reset role;
insert into storage.objects (bucket_id, name, created_at) values
    ('trip-files', 'trips/' || :'t7' || '/' || :'fa' || '.pdf', now() - interval '40 days'),
    ('trip-files', 'trips/' || :'t7' || '/' || :'fb' || '.jpg', now() - interval '40 days'),
    ('trip-files', 'trips/' || :'t7' || '/' || :'fb' || '.png', now() - interval '2 days'),
    ('trip-files', 'trips/' || :'t7' || '/' || :'ghost' || '.jpg', now() - interval '2 days'),   -- no row at all
    ('trip-files', 'trips/' || :'t7' || '/' || :'fresh' || '.jpg', now()),                       -- upload in progress
    ('trip-files', 'trips/' || :'t7' || '/' || :'fc' || '.jpg', now() - interval '2 days'),      -- row exists, path not recorded yet
    ('trip-files', 'trips/' || :'t7' || '/notlar.txt', now() - interval '2 days');               -- not ours to judge

-- in the trash: the file stays (the app can restore it for 30 days)
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Attachment', 'op', 'delete', 'id', :'fa', 'trip_id', :'t7'))) as res;
select test.eq(test.status(res), 'deleted', 'G7: attachment moved to the trash') from r;
reset role;
select test.eq((select count(*) from private.file_cleanup_queue), 0::bigint, 'G7: a soft-deleted attachment keeps its file');

-- the file is replaced by one with another extension: the old one is no longer needed
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'fb', 'trip_id', :'t7',
        'fields', jsonb_build_object('storage_path', 'trips/' || :'t7' || '/' || :'fb' || '.png')))) as res;
select test.eq(test.status(res), 'applied', 'G7: storage_path changed') from r;
reset role;
select test.eq((select array_agg(path) from private.file_cleanup_queue), array['trips/' || :'t7' || '/' || :'fb' || '.jpg'],
               'G7: the replaced file is queued');

-- the nightly purge: the 30-day-old trash row goes, and so does its file; orphans more than a day old too
update public.attachments set deleted_at = now() - interval '31 days' where id = :'fa';
select private.purge_deleted_rows();
select test.eq((select count(*) from public.attachments where id = :'fa'), 0::bigint, 'G7: purge removes the old trash row');
select test.eq((select array_agg(path order by path) from private.file_cleanup_queue),
               array['trips/' || :'t7' || '/' || :'fa' || '.pdf', 'trips/' || :'t7' || '/' || :'fb' || '.jpg',
                     'trips/' || :'t7' || '/' || :'ghost' || '.jpg'],
               'G7: purged row''s file and the day-old orphan are queued; a fresh upload, a file of a known row and a foreign name are not');
select test.eq(private.queue_orphan_files(), 0, 'G7: queueing orphans again adds nothing');
select test.eq(private.is_own_file('trips/' || :'t7' || '/' || :'fb' || '.pdf', :'t7', :'fa'), false, 'G7: another row''s file is never "own"');

-- only the service role empties the queue
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
do $$
begin
    perform public.file_cleanup_claim(10);
    raise exception 'FAILED: a signed-in user could claim the clean-up queue';
exception when insufficient_privilege then raise notice 'ok - G7: users cannot claim the queue';
end $$;
do $$
begin
    perform public.file_cleanup_done(array['x']);
    raise exception 'FAILED: a signed-in user could confirm deletions';
exception when insufficient_privilege then raise notice 'ok - G7: users cannot confirm deletions';
end $$;
reset role;

-- a queued path some row points at again (restored from a device's trash) is dropped, the file kept
insert into private.file_cleanup_queue (path) values ('trips/' || :'t7' || '/' || :'fb' || '.png');
set role service_role;
select test.eq(public.file_cleanup_claim(2),
               array['trips/' || :'t7' || '/' || :'fa' || '.pdf', 'trips/' || :'t7' || '/' || :'fb' || '.jpg'],
               'G7: claim hands out the oldest paths, never one still in use');
select test.eq(public.file_cleanup_claim(10), array['trips/' || :'t7' || '/' || :'ghost' || '.jpg'],
               'G7: claimed paths are not handed out twice');
select test.eq(public.file_cleanup_claim(10), '{}'::text[], 'G7: nothing left to claim');
reset role;
select test.eq((select count(*) from private.file_cleanup_queue where path like '%' || :'fb' || '.png'), 0::bigint,
               'G7: the path in use left the queue');

set role service_role;
select test.eq(public.file_cleanup_done(array['trips/' || :'t7' || '/' || :'fa' || '.pdf', 'trips/' || :'t7' || '/' || :'fb' || '.jpg']), 2,
               'G7: confirmed deletions leave the queue');
reset role;
-- the ghost's delete failed: handed out again after 15 minutes, but not forever
update private.file_cleanup_queue set claimed_at = now() - interval '20 minutes';
set role service_role;
select test.eq(public.file_cleanup_claim(10), array['trips/' || :'t7' || '/' || :'ghost' || '.jpg'], 'G7: an unconfirmed path is retried later');
reset role;
update private.file_cleanup_queue set claimed_at = now() - interval '20 minutes', attempts = 10;
set role service_role;
select test.eq(public.file_cleanup_claim(10), '{}'::text[], 'G7: after 10 tries it waits for a look by hand');
reset role;
delete from private.file_cleanup_queue;
-- (the Edge Function's Storage API call removed these)
delete from storage.objects where name in ('trips/' || :'t7' || '/' || :'fa' || '.pdf', 'trips/' || :'t7' || '/' || :'fb' || '.jpg',
                                           'trips/' || :'t7' || '/' || :'ghost' || '.jpg');

-- a trip purged for good takes its attachments' files with it
update public.trips set deleted_at = now() - interval '31 days' where id = :'t7';
select private.purge_deleted_rows();
select test.eq((select count(*) from public.attachments where trip_id = :'t7'), 0::bigint, 'G7: purged trip takes its attachments');
select test.eq((select array_agg(path order by path) from private.file_cleanup_queue),
               array['trips/' || :'t7' || '/' || :'fb' || '.png', 'trips/' || :'t7' || '/' || :'fc' || '.jpg'],
               'G7: and their files are queued (the unrecorded upload is an orphan now)');
delete from private.file_cleanup_queue;
delete from storage.objects where name like 'trips/' || :'t7' || '/%';

-- ------------------------------------------------------------------------------- TP-11: trip currency (migration 19)
\set t11    'bbbbbbbb-0000-0000-0000-000000000011'
\set e11a   'bbbbbbbb-0000-0000-0000-0000000011a1'
\set e11b   'bbbbbbbb-0000-0000-0000-0000000011b1'
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t11', 'full', true, 'fields',
        jsonb_build_object('name', 'Viyana', 'start_date', '2026-11-01', 'end_date', '2026-11-03', 'currency_code', 'TRY')),
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e11a', 'trip_id', :'t11', 'full', true, 'fields',
        jsonb_build_object('amount_minor', 100000, 'original_amount_minor', 100000, 'original_currency_code', 'TRY',
                           'exchange_rate', 1, 'date', '2026-11-01', 'amount_currency_code', 'TRY'))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'status' = 'applied'), 2::bigint, 'TP-11: Ali''s TRY trip with a 1000 TRY expense');

-- the currency can't be changed field by field while the trip has expenses
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t11', 'base_version', 1, 'fields', jsonb_build_object('currency_code', 'EUR')))) as res;
select test.eq(test.reason((select res from r)), 'invalid', 'TP-11: a plain currency_code edit is refused');

-- one atomic conversion
select test.eq((public.convert_trip_currency(:'t11', 'TRY', 'EUR', 0.025) ->> 'converted')::int, 1, 'TP-11: convert_trip_currency converts the expense');
select test.eq((select currency_code from public.trips where id = :'t11'), 'EUR', 'TP-11: the trip is in EUR');
select test.eq((select amount_minor from public.expenses where id = :'e11a'), 2500::bigint, 'TP-11: 1000 TRY became 25 EUR');
select test.eq((select amount_currency_code from public.expenses where id = :'e11a'), 'EUR', 'TP-11: and is marked EUR');

-- a second conversion based on the old currency (someone else converted first) is refused
do $$
begin
    perform public.convert_trip_currency('bbbbbbbb-0000-0000-0000-000000000011', 'TRY', 'USD', 0.03);
    raise exception 'FAILED: a conversion from a stale currency went through';
exception when sqlstate 'TPC01' then raise notice 'ok - TP-11: a conversion from a stale currency is refused';
end $$;

-- an expense added offline in TRY arrives after the conversion: it is converted on arrival
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Expense', 'op', 'upsert', 'id', :'e11b', 'trip_id', :'t11', 'full', true, 'fields',
        jsonb_build_object('amount_minor', 150000, 'original_amount_minor', 150000, 'original_currency_code', 'TRY',
                           'exchange_rate', 1, 'date', '2026-11-02', 'amount_currency_code', 'TRY')))) as res;
select test.eq(test.status((select res from r)), 'applied', 'TP-11: the late TRY expense is taken');
select test.eq((select amount_minor from public.expenses where id = :'e11b'), 3750::bigint, 'TP-11: 1500 TRY is stored as 37.50 EUR');
select test.eq((select amount_currency_code from public.expenses where id = :'e11b'), 'EUR', 'TP-11: marked EUR');
select test.eq((select original_amount_minor from public.expenses where id = :'e11b'), 150000::bigint, 'TP-11: what was typed stays');

-- converting back to TRY restores the typed amounts
select public.convert_trip_currency(:'t11', 'EUR', 'TRY', 40);
select test.eq((select amount_minor from public.expenses where id = :'e11b'), 150000::bigint, 'TP-11: back in TRY = the typed amount');
select test.eq((select exchange_rate from public.expenses where id = :'e11b'), 1::double precision, 'TP-11: with rate 1');
reset role;

-- a viewer can't convert
insert into public.trip_members (trip_id, user_id, role, invited_by) values (:'t11', :'can', 'viewer', :'ali');
select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
do $$
begin
    perform public.convert_trip_currency('bbbbbbbb-0000-0000-0000-000000000011', 'TRY', 'EUR', 0.025);
    raise exception 'FAILED: a viewer converted the trip';
exception when insufficient_privilege then raise notice 'ok - TP-11: a viewer cannot convert';
end $$;
reset role;

-- ------------------------------------------------------------------------------- TP-12: ownership transfer (migration 20)
-- (Ali owns Viyana (t11); Can is a viewer in it.)
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq((select other_members from public.get_owned_shared_trips() where trip_id = :'t11'), 1, 'TP-12: Viyana is listed as shared with 1 member');
reset role;

select set_config('request.jwt.claim.sub', :'can', false);
set role authenticated;
do $$
begin
    perform public.transfer_trip_ownership('bbbbbbbb-0000-0000-0000-000000000011', '00000000-0000-0000-0000-00000000a333');
    raise exception 'FAILED: a viewer handed the trip over';
exception when insufficient_privilege then raise notice 'ok - TP-12: only the owner can hand a trip over';
end $$;
reset role;

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
do $$
begin
    perform public.transfer_trip_ownership('bbbbbbbb-0000-0000-0000-000000000011', '00000000-0000-0000-0000-00000000a444');
    raise exception 'FAILED: the trip went to a stranger';
exception when sqlstate 'P0002' then raise notice 'ok - TP-12: only to a member';
end $$;
select public.transfer_trip_ownership(:'t11', :'can');
reset role;
select test.eq((select owner_id from public.trips where id = :'t11'), :'can'::uuid, 'TP-12: Can owns Viyana now');
select test.eq((select role from public.trip_members where trip_id = :'t11' and user_id = :'can'), 'owner', 'TP-12: Can is the owner member');
select test.eq((select role from public.trip_members where trip_id = :'t11' and user_id = :'ali'), 'editor', 'TP-12: Ali stays on as an editor');

-- an ordinary update still can't move ownership
update public.trips set owner_id = :'ali' where id = :'t11';
select test.eq((select owner_id from public.trips where id = :'t11'), :'can'::uuid, 'TP-12: owner_id doesn''t move through an ordinary update');

-- ------------------------------------------------------------------------------- TP-15: sign-up hook (migration 22)
select test.eq(public.hook_before_user_created('{"user": {"email": "ali@gmail.com"}}'::jsonb), '{}'::jsonb, 'TP-15: an ordinary address may sign up');
select test.eq(public.hook_before_user_created('{"user": {"email": "Bot@Mailinator.com"}}'::jsonb) -> 'error' ->> 'http_code', '400', 'TP-15: a throw-away address is refused');
select test.eq(public.hook_before_user_created('{"user": {"email": "bot@x.yopmail.com"}}'::jsonb) -> 'error' ->> 'message' like 'disposable_email:%', true, 'TP-15: subdomains too, with the marker the app reads');
select test.eq(public.hook_before_user_created('{"user": {}}'::jsonb), '{}'::jsonb, 'TP-15: no e-mail (e.g. phone) is not refused here');
set role authenticated;
do $$ begin
    perform public.hook_before_user_created('{}'::jsonb);
    raise exception 'FAILED: a signed-in user could call the auth hook';
exception when insufficient_privilege then raise notice 'ok - TP-15: the hook is for Supabase Auth only';
end $$;
reset role;

-- ------------------------------------------------------------------------------- TP-33: Keşfet catalog (migration 23)
grant usage on schema test to anon;
grant execute on function test.eq(anyelement, anyelement, text) to anon;

select private.explore_upsert($json${
    "slug": "istanbul-3-gun", "title": "İstanbul 3 gün", "country": "Türkiye", "city": "İstanbul",
    "duration_days": 3, "budget_level": 2, "tags": ["culture", "food"], "months": [4, 5, 9, 10],
    "sort_rank": 10, "status": "published",
    "places": [{"key": "ayasofya", "name": "Ayasofya", "lat": 41.0086, "lon": 28.9802}],
    "days": [{"day": 1, "activities": [{"start": "09:00", "end": "11:00", "title": "Ayasofya", "place": "ayasofya"}]},
             {"day": 2, "activities": [{"title": "Boğaz turu"}]}],
    "todos": [{"title": "Müze kart al"}, {"title": "Rahat ayakkabı", "kind": "packing"}]
}$json$::jsonb);
select private.explore_upsert('{"slug": "roma-4-gun", "title": "Roma 4 gün", "country": "İtalya", "city": "Roma",
    "duration_days": 4, "budget_level": 3, "tags": ["culture"], "sort_rank": 20, "status": "published"}'::jsonb);
select private.explore_upsert('{"slug": "ege-5-gun", "title": "Ege kıyıları", "country": "Türkiye", "city": "İzmir",
    "duration_days": 5, "budget_level": 1, "tags": ["sea"], "sort_rank": 30, "status": "published"}'::jsonb);
select private.explore_upsert('{"slug": "taslak", "title": "Taslak", "country": "Türkiye", "city": "Van",
    "duration_days": 2, "sort_rank": 1}'::jsonb);
select private.explore_upsert('{"slug": "kayak", "title": "Kayak", "country": "Türkiye", "city": "Erzurum",
    "duration_days": 2, "sort_rank": 2, "status": "published", "publish_until": "2000-01-01T00:00:00Z"}'::jsonb);

select set_config('request.jwt.claim.sub', '', false);
set role anon;
select test.eq(jsonb_array_length(public.explore_list() -> 'items'), 3, 'TP-33: anon sees only the 3 published, in-window trips');
select test.eq((select count(*) from public.explore_trips), 3::bigint, 'TP-33: RLS hides drafts and expired trips from direct reads too');
select test.eq(public.explore_list() -> 'items' -> 0 ->> 'slug', 'istanbul-3-gun', 'TP-33: recommended = admin order');
select test.eq(public.explore_list(p_sort => 'shortest') -> 'items' -> 0 ->> 'slug', 'istanbul-3-gun', 'TP-33: shortest first');
select test.eq(jsonb_array_length(public.explore_list(p_query => 'İSTANBUL') -> 'items'), 1, 'TP-33: search ignores case and Turkish letters (İSTANBUL)');
select test.eq(jsonb_array_length(public.explore_list(p_query => 'istanbul') -> 'items'), 1, 'TP-33: search (istanbul)');
select test.eq(jsonb_array_length(public.explore_list(p_query => 'ege kiyi') -> 'items'), 1, 'TP-33: every word must match (ege kiyi)');
select test.eq(jsonb_array_length(public.explore_list(p_query => '%') -> 'items'), 0, 'TP-33: % is matched literally');
select test.eq(jsonb_array_length(public.explore_list(p_country => 'turkiye') -> 'items'), 2, 'TP-33: country filter (folded)');
select test.eq(jsonb_array_length(public.explore_list(p_min_days => 4, p_max_days => 7) -> 'items'), 2, 'TP-33: day range filter');
select test.eq(jsonb_array_length(public.explore_list(p_budget_levels => array[1, 3]) -> 'items'), 2, 'TP-33: budget filter');
select test.eq(jsonb_array_length(public.explore_list(p_tags => array['sea', 'food']) -> 'items'), 2, 'TP-33: any of the styles');
select test.eq(jsonb_array_length(public.explore_list(p_month => 7) -> 'items'), 2, 'TP-33: month filter (no months = all year)');

-- keyset pages: 1 + 1 + 1, no repeats, the last page has no cursor
create temp table explore_pages (slug text);
do $$
declare v_page jsonb; v_cursor text; v_pages int := 0;
begin
    loop
        v_page := public.explore_list(p_cursor => v_cursor, p_limit => 1);
        insert into explore_pages select x ->> 'slug' from jsonb_array_elements(v_page -> 'items') x;
        v_cursor := v_page ->> 'next_cursor';
        v_pages := v_pages + 1;
        exit when v_cursor is null or v_pages > 10;
    end loop;
    perform test.eq(v_pages, 3, 'TP-33: 3 pages of 1, then no cursor');
end $$;
select test.eq((select count(distinct slug) from explore_pages), 3::bigint, 'TP-33: every card once');
do $$ declare v_cursor text;
begin
    v_cursor := public.explore_list(p_sort => 'newest', p_limit => 2) ->> 'next_cursor';
    perform test.eq(jsonb_array_length(public.explore_list(p_sort => 'newest', p_cursor => v_cursor, p_limit => 2) -> 'items'), 1, 'TP-33: newest, second page');
end $$;

do $$ begin
    perform public.explore_list(p_limit => 31);
    raise exception 'FAILED: more than 30 cards per page';
exception when sqlstate '22023' then raise notice 'ok - TP-33: at most 30 per page';
end $$;
do $$ begin
    perform public.explore_list(p_query => repeat('a', 101));
    raise exception 'FAILED: a 101-character query';
exception when sqlstate '22023' then raise notice 'ok - TP-33: query at most 100 characters';
end $$;
do $$ begin
    perform public.explore_list(p_tags => array['casino']);
    raise exception 'FAILED: an unknown tag';
exception when sqlstate '22023' then raise notice 'ok - TP-33: unknown filter refused';
end $$;
do $$ begin
    perform public.explore_list(p_cursor => 'not json');
    raise exception 'FAILED: a broken cursor';
exception when sqlstate '22023' then raise notice 'ok - TP-33: broken cursor refused';
end $$;
do $$ begin
    insert into public.explore_trips (slug, title, country, city, duration_days) values ('x', 'x', 'x', 'x', 1);
    raise exception 'FAILED: anon wrote into the catalog';
exception when insufficient_privilege then raise notice 'ok - TP-33: the catalog is read-only for clients';
end $$;
do $$ begin
    perform private.explore_upsert('{"slug": "x"}'::jsonb);
    raise exception 'FAILED: anon called the admin function';
exception when insufficient_privilege then raise notice 'ok - TP-33: admin functions are not for clients';
end $$;
reset role;

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
select test.eq(jsonb_array_length(public.explore_detail((select id from public.explore_trips where slug = 'istanbul-3-gun')) -> 'activities'), 2,
               'TP-33: detail has the day plan');
select test.eq(public.explore_detail((select id from public.explore_trips where slug = 'istanbul-3-gun')) -> 'activities' -> 0 ->> 'start_time', '09:00',
               'TP-33: times as HH:mm');
do $$ begin
    update public.explore_trips set title = 'x';
    raise exception 'FAILED: a signed-in user changed the catalog';
exception when insufficient_privilege then raise notice 'ok - TP-33: signed-in users cannot write either';
end $$;
reset role;
select test.eq(public.explore_detail((select id from public.explore_trips where slug = 'taslak')), null::jsonb, 'TP-33: no detail for a draft');

-- updating a sample bumps its version and replaces its plan; archiving hides it
select private.explore_upsert('{"slug": "roma-4-gun", "title": "Roma 4 gün", "country": "İtalya", "city": "Roma",
    "duration_days": 4, "days": [{"day": 1, "activities": [{"title": "Kolezyum"}]}]}'::jsonb);
select test.eq((select content_version from public.explore_trips where slug = 'roma-4-gun'), 2, 'TP-33: update bumps content_version');
select test.eq((select status from public.explore_trips where slug = 'roma-4-gun'), 'published', 'TP-33: update keeps the status');
select private.explore_set_status('roma-4-gun', 'archived');
set role anon;
select test.eq(jsonb_array_length(public.explore_list() -> 'items'), 2, 'TP-33: an archived trip disappears');
reset role;
do $$ begin
    perform private.explore_upsert('{"slug": "bozuk", "title": "Bozuk", "country": "x", "city": "x", "duration_days": 2,
        "days": [{"day": 3, "activities": [{"title": "x"}]}]}'::jsonb);
    raise exception 'FAILED: a day outside the trip';
exception when sqlstate '22023' then raise notice 'ok - TP-33: days must fit the trip';
end $$;

-- ------------------------------------------------------------------------------- admin panel (migration 25)
set role service_role;
select test.eq((public.admin_overview() ->> 'explore_draft')::int >= 1, true, 'admin: overview counts drafts');
select test.eq((select count(*) from jsonb_array_elements(public.admin_explore_list()) x where x ->> 'slug' = 'taslak'), 1::bigint,
               'admin: the list shows drafts too');
select test.eq(public.admin_explore_get('istanbul-3-gun') -> 'days' -> 0 -> 'activities' -> 0 ->> 'place',
               public.admin_explore_get('istanbul-3-gun') -> 'places' -> 0 ->> 'key', 'admin: activities point at their place key');
select test.eq(public.admin_explore_get('yok'), null::jsonb, 'admin: unknown slug gives null');
-- what admin_explore_get returns goes back through admin_explore_upsert unchanged
select public.admin_explore_upsert(public.admin_explore_get('istanbul-3-gun'));
select test.eq(public.admin_explore_get('istanbul-3-gun') ->> 'title', 'İstanbul 3 gün', 'admin: round trip keeps the trip');
select test.eq(jsonb_array_length(public.admin_explore_get('istanbul-3-gun') -> 'todos'), 2, 'admin: round trip keeps the to-dos');
select test.eq(public.admin_explore_get('istanbul-3-gun') -> 'days' -> 0 -> 'activities' -> 0 ->> 'start', '09:00', 'admin: round trip keeps the times');
select public.admin_explore_set_status('taslak', 'published');
select test.eq((select status from public.explore_trips where slug = 'taslak'), 'published', 'admin: publish from the panel');
select public.admin_explore_set_status('taslak', 'draft');

select test.eq(jsonb_array_length(public.admin_users('ALI@example')), 1, 'admin: user search ignores case');
select test.eq(jsonb_array_length(public.admin_users('%')), 0, 'admin: % in the search is literal');
select test.eq(public.admin_user(:'ali') ->> 'email', 'ali@example.com', 'admin: one account');
select test.eq(public.admin_user('00000000-0000-0000-0000-0000000000ff'), null::jsonb, 'admin: unknown account gives null');

select public.admin_set_ai_limit(:'ali', 20);
select test.eq((public.admin_user(:'ali') ->> 'ai_limit')::int, 20, 'admin: an account limit of its own');
select test.eq((public.admin_user(:'ali') ->> 'ai_custom_limit')::int, 20, 'admin: the panel shows it');
reset role;
insert into private.ai_usage (user_id, day, count) values (:'ali', current_date, 7)
on conflict (user_id, day) do update set count = 7;
insert into private.ai_device_usage (device, day, count) values (repeat('ab', 32), current_date, 7)
on conflict (device, day) do update set count = 7;
insert into private.ai_quota_ticket (user_id, device, day) values (:'ali', repeat('ab', 32), current_date);
set role service_role;
select public.admin_reset_ai_today(:'ali');
reset role;
select test.eq((select count from private.ai_usage where user_id = :'ali' and day = current_date), 0, 'admin: reset today''s account count');
select test.eq((select count from private.ai_device_usage where device = repeat('ab', 32) and day = current_date), 0, 'admin: and its device');
set role service_role;
select public.admin_set_ai_limit(:'ali', null);
reset role;
select test.eq(private.ai_limit_for(:'ali'), private.ai_daily_limit(), 'admin: null goes back to the default');
do $$ begin
    perform public.admin_set_ai_limit('00000000-0000-0000-0000-00000000a111', 101);
    raise exception 'FAILED: a limit over 100';
exception when sqlstate '22023' then raise notice 'ok - admin: limit at most 100';
end $$;

select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
do $$ begin
    perform public.admin_users(null, 10);
    raise exception 'FAILED: a signed-in user listed the accounts';
exception when insufficient_privilege then raise notice 'ok - admin: functions are not for signed-in users';
end $$;
do $$ begin
    perform public.admin_set_ai_limit('00000000-0000-0000-0000-00000000a111', 100);
    raise exception 'FAILED: a signed-in user raised their own AI limit';
exception when insufficient_privilege then raise notice 'ok - admin: users cannot raise their own limit';
end $$;
reset role;
select set_config('request.jwt.claim.sub', '', false);
set role anon;
do $$ begin
    perform public.admin_explore_upsert('{"slug": "x"}'::jsonb);
    raise exception 'FAILED: anon called an admin function';
exception when insufficient_privilege then raise notice 'ok - admin: functions are not for anon';
end $$;
reset role;

-- ------------------------------------------------------------------------------- TP-7: account files (migration 17)
\set t8     'bbbbbbbb-0000-0000-0000-000000000008'
\set f8     'bbbbbbbb-0000-0000-0000-0000000000a8'
\set gone   'bbbbbbbb-0000-0000-0000-0000000000f8'
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
drop table r;
create temp table r as select public.apply_changes(jsonb_build_array(
    jsonb_build_object('entity', 'Trip', 'op', 'upsert', 'id', :'t8', 'full', true, 'fields',
        jsonb_build_object('name', 'Porto', 'start_date', '2026-10-01', 'end_date', '2026-10-03', 'currency_code', 'EUR')),
    jsonb_build_object('entity', 'Attachment', 'op', 'upsert', 'id', :'f8', 'trip_id', :'t8', 'full', true,
        'fields', jsonb_build_object('display_name', 'pasaport', 'kind', 0, 'storage_path', 'trips/' || :'t8' || '/' || :'f8' || '.jpg'))
)) as res;
select test.eq((select count(*) from r, jsonb_array_elements(r.res) e where e ->> 'status' = 'applied'), 2::bigint, 'TP-7: Ali''s trip with a passport photo');
reset role;
insert into storage.objects (bucket_id, name, created_at) values
    ('trip-files', 'trips/' || :'t8' || '/' || :'f8' || '.jpg', now() - interval '5 days'),
    ('trip-files', 'trips/' || :'t8' || '/notlar.txt', now() - interval '5 days'),          -- not an attachment key
    ('trip-files', 'trips/' || :'t8' || '/' || gen_random_uuid() || '.pdf', now()),        -- uploaded while the app was listing
    ('trip-files', 'trips/' || :'gone' || '/vize.pdf', now() - interval '2 days'),          -- folder of a trip that is gone
    ('trip-files', 'trips/' || :'gone' || '/yeni.jpg', now());                              -- ... but uploaded just now

-- the nightly scan: a gone trip's folder is emptied (after a day); a live trip's files are not touched
delete from private.file_cleanup_queue;
select test.eq(private.queue_orphan_files(), 1, 'TP-7: orphan scan queues one file');
select test.eq((select array_agg(path) from private.file_cleanup_queue), array['trips/' || :'gone' || '/vize.pdf'],
               'TP-7: the day-old file of a trip that no longer exists, nothing of a live trip');
delete from private.file_cleanup_queue;

-- the queue is not the users' business
select set_config('request.jwt.claim.sub', :'ali', false);
set role authenticated;
do $$
begin
    perform 1 from private.file_cleanup_queue;
    raise exception 'FAILED: a signed-in user could read the clean-up queue';
exception when insufficient_privilege then raise notice 'ok - TP-7: users cannot read the clean-up queue';
end $$;
reset role;

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
select test.eq((select count(*) from public.trips where id <> :'t11'), 0::bigint, 'and the trips it owned');
select test.eq((select owner_id from public.trips where id = :'t11'), :'can'::uuid, 'TP-12: the trip handed over to Can survives');
select test.eq((select count(*) from public.expenses where trip_id = :'t11' and deleted_at is null), 2::bigint, 'TP-12: with its expenses');
select test.eq((select count(*) from public.packing_templates), 0::bigint, 'and its packing templates');
select test.eq((select owner from storage.objects where name like '%/foto.jpg'), null::uuid, 'his uploads elsewhere lose their owner');
select test.eq((select count(*) from storage.objects where name like '%/foto.jpg'), 1::bigint, 'but stay for the other trip');
select test.eq((select count(*) from private.file_cleanup_queue where path like 'trips/' || :'t8' || '/%'), 3::bigint,
               'TP-7: delete_account queues all files of his trip (attachment, other name, fresh upload)');
select test.eq((select count(*) from private.file_cleanup_queue where path not like 'trips/' || :'t8' || '/%'), 0::bigint,
               'TP-7: and nothing of other trips (his upload elsewhere stays)');
set role service_role;
select test.eq(cardinality(public.file_cleanup_claim(10)), 3, 'TP-7: purge-files gets them the same night');
reset role;
delete from private.file_cleanup_queue;
delete from storage.objects where name like 'trips/' || :'t8' || '/%' or name like 'trips/' || :'gone' || '/%';

\echo ALL SYNC TESTS PASSED
