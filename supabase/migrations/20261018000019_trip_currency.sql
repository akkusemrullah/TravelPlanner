-- =====================================================================================================
-- TravelPlanner - TP-11 / 19: changing a shared trip's currency keeps every expense's amount and currency in step.
--
-- Until now the app converted the expenses locally and synced each amount and the trip's currency_code as
-- separate field edits. A member who added an expense offline in the old currency ended up with an amount in
-- the old currency under the new currency's label (1 500 TRY shown as 1 500 EUR), and two members converting at
-- the same time could leave the amounts in one currency and the label in another.
-- From now on:
--   * expenses.amount_currency_code: the currency amount_minor is in (the trip's currency when it was written).
--   * public.convert_trip_currency(trip, from, to, rate): converts the trip and all its expenses in ONE
--     transaction; owner/editor only; refused (TPC01) when the trip is no longer in "from" - someone else
--     converted it first. Every conversion is logged in private.trip_currency_changes.
--   * an expense written in an older currency of its trip (amount_currency_code differs from the trip's, e.g. added
--     offline before the conversion arrived) is converted on arrival with the logged rate(s).
--   * a trip's currency_code can't be changed through apply_changes any more while it has expenses (that
--     change comes back "invalid"); the app calls convert_trip_currency for trips in the cloud.
-- Safe to run again.
-- =====================================================================================================

-- ------------------------------------------------------------------------------------------- 1. column, log
alter table public.expenses add column if not exists amount_currency_code text;
do $$ begin
    if not exists (select 1 from pg_constraint where conname = 'expenses_amount_currency_format') then
        alter table public.expenses add constraint expenses_amount_currency_format
            check (amount_currency_code is null or amount_currency_code ~ '^[A-Z]{3}$');
    end if;
end $$;

create table if not exists private.trip_currency_changes (
    id            bigserial primary key,
    trip_id       uuid not null references public.trips (id) on delete cascade,
    from_currency text not null,
    to_currency   text not null,
    rate          numeric not null,
    changed_by    uuid,
    changed_at    timestamptz not null default now()
);
create index if not exists trip_currency_changes_trip on private.trip_currency_changes (trip_id, id);
revoke all on private.trip_currency_changes from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- 2. one step
-- The app's rule (ExpenseService.ConvertTripExpensesAsync): what the user typed stays; the amount is
-- re-expressed in the new currency, or set back to the typed amount when the new currency is the typed one.
create or replace function private.convert_expense_amount(
    p_amount bigint, p_rate_so_far double precision, p_original_code text, p_original_amount bigint,
    p_from text, p_to text, p_rate numeric,
    out amount_minor bigint, out exchange_rate double precision, out original_currency_code text, out original_amount_minor bigint)
language plpgsql
immutable
set search_path = ''
as $$
declare
    v_rate double precision;
begin
    original_currency_code := coalesce(p_original_code, p_from);
    original_amount_minor := case when p_original_code is null then p_amount else p_original_amount end;
    if original_currency_code = p_to then
        amount_minor := original_amount_minor;
        exchange_rate := 1;
    else
        amount_minor := round(p_amount * p_rate);
        if amount_minor > 999999999999 then
            raise exception 'a converted expense exceeds the supported amount' using errcode = '22003';
        end if;
        v_rate := (case when p_rate_so_far is null or p_rate_so_far <= 0 then 1 else p_rate_so_far end) * p_rate::double precision;
        exchange_rate := case when v_rate between 0.000001 and 1000000 then v_rate else p_rate::double precision end;
    end if;
end;
$$;

-- ------------------------------------------------------------------------------------------- 3. the RPC
create or replace function public.convert_trip_currency(p_trip_id uuid, p_from text, p_to text, p_rate numeric)
returns jsonb
language plpgsql
security definer
set search_path = ''
set lock_timeout = '5s'
as $$
declare
    v_uid      uuid := (select auth.uid());
    v_current  text;
    v_count    int := 0;
    v_row      record;
    v_step     record;
begin
    if v_uid is null then
        raise exception 'not signed in' using errcode = '28000';
    end if;
    if private.account_state(v_uid) <> 'active' then
        raise exception 'account disabled' using errcode = '28000';
    end if;
    if p_from !~ '^[A-Z]{3}$' or p_to !~ '^[A-Z]{3}$' or p_from = p_to then
        raise exception 'invalid currency' using errcode = '22023';
    end if;
    if p_rate is null or p_rate < 0.000001 or p_rate > 1000000 then
        raise exception 'invalid rate' using errcode = '22023';
    end if;

    -- The trip row lock serializes conversions: a second one waits here and then finds the new currency.
    select t.currency_code into v_current
    from public.trips t
    where t.id = p_trip_id and t.deleted_at is null
    for update;
    if not found then
        raise exception 'trip not found' using errcode = 'P0002';
    end if;
    if not private.has_trip_role(p_trip_id, array['owner', 'editor']) then
        raise exception 'only the owner or an editor can change the currency' using errcode = '42501';
    end if;
    -- (null = a trip from before multi-currency support: it is in whatever the app shows as "from")
    if v_current is not null and v_current <> p_from then
        raise exception 'the trip is in % now', v_current using errcode = 'TPC01';
    end if;

    perform set_config('tp.currency_rpc', 'on', true);

    for v_row in
        select e.id, e.amount_minor, e.exchange_rate, e.original_currency_code, e.original_amount_minor
        from public.expenses e
        where e.trip_id = p_trip_id and e.deleted_at is null and coalesce(e.amount_currency_code, p_from) = p_from
        for update
    loop
        select * into v_step from private.convert_expense_amount(
            v_row.amount_minor, v_row.exchange_rate, v_row.original_currency_code, v_row.original_amount_minor,
            p_from, p_to, p_rate);
        update public.expenses e
        set amount_minor = v_step.amount_minor,
            exchange_rate = v_step.exchange_rate,
            original_currency_code = v_step.original_currency_code,
            original_amount_minor = v_step.original_amount_minor,
            amount_currency_code = p_to,
            updated_by = v_uid
        where e.id = v_row.id;
        v_count := v_count + 1;
    end loop;

    update public.trips t set currency_code = p_to, updated_by = v_uid where t.id = p_trip_id;
    insert into private.trip_currency_changes (trip_id, from_currency, to_currency, rate, changed_by)
    values (p_trip_id, p_from, p_to, p_rate, v_uid);

    perform set_config('tp.currency_rpc', 'off', true);
    return jsonb_build_object('converted', v_count, 'currency', p_to);
end;
$$;

revoke all on function public.convert_trip_currency(uuid, text, text, numeric) from public, anon;
grant execute on function public.convert_trip_currency(uuid, text, text, numeric) to authenticated;

-- ------------------------------------------------------------------------------------------- 4. late expenses
-- An expense whose amount is in an older currency of its trip is brought to the trip's currency with the
-- logged rates: from the latest conversion out of its currency, then every conversion after that one.
create or replace function private.normalize_expense_currency() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_trip_currency text;
    v_first         record;
    v_step          record;
    v_result        record;
begin
    if new.amount_currency_code is null or coalesce(current_setting('tp.currency_rpc', true), '') = 'on' then
        return new;
    end if;

    select t.currency_code into v_trip_currency from public.trips t where t.id = new.trip_id;
    if v_trip_currency is null or v_trip_currency = new.amount_currency_code then
        return new;
    end if;

    select c.id, c.from_currency, c.to_currency, c.rate into v_first
    from private.trip_currency_changes c
    where c.trip_id = new.trip_id and c.from_currency = new.amount_currency_code
    order by c.id desc
    limit 1;
    if not found then
        return new; -- never a currency of this trip: leave it, the app shows it with its own currency
    end if;

    for v_step in
        select c.from_currency, c.to_currency, c.rate
        from private.trip_currency_changes c
        where c.trip_id = new.trip_id and c.id >= v_first.id
        order by c.id
    loop
        select * into v_result from private.convert_expense_amount(
            new.amount_minor, new.exchange_rate, new.original_currency_code, new.original_amount_minor,
            v_step.from_currency, v_step.to_currency, v_step.rate);
        new.amount_minor := v_result.amount_minor;
        new.exchange_rate := v_result.exchange_rate;
        new.original_currency_code := v_result.original_currency_code;
        new.original_amount_minor := v_result.original_amount_minor;
        new.amount_currency_code := v_step.to_currency;
    end loop;

    return new;
end;
$$;

drop trigger if exists expenses_normalize_currency on public.expenses;
create trigger expenses_normalize_currency
    before insert or update on public.expenses
    for each row execute function private.normalize_expense_currency();

-- ------------------------------------------------------------------------------------------- 5. guard
-- A trip with expenses changes currency only through convert_trip_currency. (Setting the code of a trip from
-- before multi-currency support - null - is no conversion and stays allowed.)
create or replace function private.guard_trip_currency() returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    if old.currency_code is not null
       and new.currency_code is distinct from old.currency_code
       and coalesce(current_setting('tp.currency_rpc', true), '') <> 'on'
       and exists (select 1 from public.expenses e where e.trip_id = new.id and e.deleted_at is null) then
        raise exception 'change the currency of a trip with expenses with convert_trip_currency' using errcode = '22023';
    end if;
    return new;
end;
$$;

drop trigger if exists trips_guard_currency on public.trips;
create trigger trips_guard_currency
    before update on public.trips
    for each row execute function private.guard_trip_currency();
