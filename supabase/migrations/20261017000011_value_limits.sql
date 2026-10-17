-- =====================================================================================================
-- TravelPlanner - SEC-01 / 11: value limits on every synced column (same limits as the app's InputLimits).
--
-- apply_changes writes whatever a member of the trip sends, and every other member's device stores it.
-- Without these limits one editor calling the REST API directly could write e.g. amount_minor =
-- 9223372036854775807, exchange_rate = 1e300 or date = '20000-01-01' (PostgreSQL accepts it) and break the
-- sync (and the totals) of everyone in the trip. With them such a change is refused by apply_changes as
-- "invalid" (a check violation), and the rest of the batch still goes through.
--
-- The limits mirror Helpers/InputLimits.cs:
--   money (minor units)  0 .. 999 999 999 999        (InputLimits.MaxMoneyMinor = 9 999 999 999.99)
--   exchange rate        0.000001 .. 1 000 000       (InputLimits.MinExchangeRate / MaxExchangeRate)
--   dates                1900-01-01 .. 2100-12-31    (InputLimits.MinDate / MaxDate)
--   expense category     0 .. 5                      (Models/ExpenseCategory.cs)
--   attachment kind      0 .. 1                      (Models/TripAttachment.cs, AttachmentKind)
--   activity sort order  0 .. 100 000               (position within a day)
--   activity end time    after the start time (and only together with a start time)
--
-- Constraints are added NOT VALID first, so a row stored before this migration never blocks it; then each one
-- is validated. If old data breaks a limit, a NOTICE names the constraint and it stays NOT VALID: new writes
-- (and edits of those old rows) are still checked. Safe to run again.
-- =====================================================================================================

create or replace function pg_temp.add_limit(p_table text, p_name text, p_check text) returns void
language plpgsql
as $$
begin
    if not exists (select 1 from pg_constraint
                   where conname = p_name and conrelid = format('public.%I', p_table)::regclass) then
        execute format('alter table public.%I add constraint %I check (%s) not valid', p_table, p_name, p_check);
    end if;
    begin
        execute format('alter table public.%I validate constraint %I', p_table, p_name);
    exception when check_violation then
        raise notice 'SEC-01: existing rows of public.% break %, it stays NOT VALID (new writes are still checked)', p_table, p_name;
    end;
end;
$$;

-- ------------------------------------------------------------------------------------------- trips
select pg_temp.add_limit('trips', 'trips_planned_budget_limit', 'planned_budget_minor between 0 and 999999999999');
select pg_temp.add_limit('trips', 'trips_start_date_range', 'start_date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('trips', 'trips_end_date_range', 'end_date between date ''1900-01-01'' and date ''2100-12-31''');

-- ------------------------------------------------------------------------------------------- expenses
select pg_temp.add_limit('expenses', 'expenses_amount_limit', 'amount_minor between 0 and 999999999999');
select pg_temp.add_limit('expenses', 'expenses_original_amount_limit', 'original_amount_minor between 0 and 999999999999');
-- (NaN and Infinity fail "between" too)
select pg_temp.add_limit('expenses', 'expenses_exchange_rate_range', 'exchange_rate between 0.000001 and 1000000');
select pg_temp.add_limit('expenses', 'expenses_date_range', 'date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('expenses', 'expenses_category_range', 'category between 0 and 5');

-- ------------------------------------------------------------------------------------------- activities
select pg_temp.add_limit('activities', 'activities_date_range', 'date between date ''1900-01-01'' and date ''2100-12-31''');
select pg_temp.add_limit('activities', 'activities_sort_order_range', 'sort_order between 0 and 100000');
select pg_temp.add_limit('activities', 'activities_end_after_start',
                         'end_time is null or (start_time is not null and end_time > start_time)');

-- ------------------------------------------------------------------------------------------- attachments
select pg_temp.add_limit('attachments', 'attachments_kind_range', 'kind between 0 and 1');

-- ------------------------------------------------------------------------------------------- created_date (timestamp)
-- The app parses these back into DateTime as well, so they get the same calendar range.
select pg_temp.add_limit('participants', 'participants_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('todos', 'todos_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('attachments', 'attachments_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
select pg_temp.add_limit('packing_templates', 'packing_templates_created_date_range',
                         'created_date is null or (created_date >= timestamp ''1900-01-01'' and created_date < timestamp ''2101-01-01'')');
