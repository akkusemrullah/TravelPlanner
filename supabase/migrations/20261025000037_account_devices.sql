-- =====================================================================================================
-- TravelPlanner - admin / 37: accounts opened on the same device (TP-55).
--
-- Until now the device of an account was only noted on the AI screen (private.ai_user_devices, migration 26: kept 31
-- days and gone with the account). Now:
--
--   private.account_devices          account + device (the app's salted SHA-256, never the raw id) + the account's
--                                    e-mail, first and last seen. No foreign key to auth.users: a deleted account
--                                    stays visible as "silindi". Rows not seen for a year are dropped (03:37).
--   public.note_device(device)       the app on every sign-in, start and return to the foreground (at most one
--                                    write an hour per account and device). The AI quota checks note it too.
--   public.admin_device_accounts(id) the panel's user page: the other accounts seen on this account's devices -
--                                    e-mail, state (active / banned / deleted), first and last seen together
--   admin_user / admin_users         + "device_accounts": how many other accounts share a device with this one
--
-- Safe to run again.
-- =====================================================================================================

create table if not exists private.account_devices (
    user_id    uuid not null,
    device     text not null check (device ~ '^[0-9a-f]{64}$'),
    email      text check (char_length(email) <= 320),
    first_seen timestamptz not null default now(),
    last_seen  timestamptz not null default now(),
    primary key (user_id, device)
);
alter table private.account_devices enable row level security;
revoke all on table private.account_devices from public, anon, authenticated;
create index if not exists account_devices_device_idx on private.account_devices (device);
create index if not exists account_devices_last_seen_idx on private.account_devices (last_seen);

-- What the AI screen already knew (migration 26): the last 31 days.
insert into private.account_devices (user_id, device, email, first_seen, last_seen)
select d.user_id, d.device, lower(u.email), d.last_seen::timestamptz, d.last_seen::timestamptz
from private.ai_user_devices d
join auth.users u on u.id = d.user_id
on conflict (user_id, device) do nothing;

-- Notes the pair; an existing pair is touched at most once an hour.
create or replace function private.note_account_device(p_uid uuid, p_device text) returns void
language sql
security definer
set search_path = ''
as $$
    insert into private.account_devices as d (user_id, device, email)
    values (p_uid, p_device, (select lower(u.email) from auth.users u where u.id = p_uid))
    on conflict (user_id, device) do update
        set last_seen = now(), email = coalesce(excluded.email, d.email)
        where d.last_seen < now() - interval '1 hour';
$$;
revoke all on function private.note_account_device(uuid, text) from public, anon, authenticated;

create or replace function public.note_device(p_device text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform private.note_account_device(private.require_uid(), private.ai_check_device(p_device));
end;
$$;
revoke all on function public.note_device(text) from public, anon;
grant execute on function public.note_device(text) to authenticated;

-- As migration 26, plus the long-lived note above (older apps only report the device on the AI screen).
create or replace function private.ai_note_device(p_uid uuid, p_device text) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    insert into private.ai_user_devices as d (user_id, device, last_seen) values (p_uid, p_device, current_date)
    on conflict (user_id, device) do update set last_seen = current_date
        where d.last_seen <> current_date;
    perform private.note_account_device(p_uid, p_device);
end;
$$;
revoke all on function private.ai_note_device(uuid, text) from public, anon, authenticated;

-- How many other accounts were seen on this account's devices.
create or replace function private.device_account_count(p_uid uuid) returns int
language sql
stable
security definer
set search_path = ''
as $$
    select count(distinct o.user_id)::int
    from private.account_devices m
    join private.account_devices o on o.device = m.device and o.user_id <> m.user_id
    where m.user_id = p_uid
$$;
revoke all on function private.device_account_count(uuid) from public, anon, authenticated;

-- [{user_id, email, state, first_seen, last_seen, devices}, ...]: newest together first. first/last seen are the
-- other account's on the shared devices.
create or replace function public.admin_device_accounts(p_user_id uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select coalesce(jsonb_agg(jsonb_build_object(
               'user_id', x.user_id,
               'email', coalesce(lower(u.email), x.email),
               'state', case when u.id is null then 'deleted' else private.account_state(x.user_id) end,
               'first_seen', x.first_seen, 'last_seen', x.last_seen, 'devices', x.devices)
             order by x.last_seen desc), '[]'::jsonb)
    from (
        select o.user_id, max(o.email) as email, min(o.first_seen) as first_seen, max(o.last_seen) as last_seen,
               count(distinct o.device)::int as devices
        from private.account_devices m
        join private.account_devices o on o.device = m.device and o.user_id <> m.user_id
        where m.user_id = p_user_id
        group by o.user_id
    ) x
    left join auth.users u on u.id = x.user_id
$$;
revoke all on function public.admin_device_accounts(uuid) from public, anon, authenticated;
grant execute on function public.admin_device_accounts(uuid) to service_role;

-- One account as the panel shows it (migration 27, plus device_accounts).
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id),
        'banned_until', u.banned_until,
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id),
        'device_accounts', private.device_account_count(u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

-- ------------------------------------------------------------------------------------------- retention
create or replace function private.purge_account_devices() returns int
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_count int;
begin
    delete from private.account_devices where last_seen < now() - interval '1 year';
    get diagnostics v_count = row_count;
    return v_count;
end;
$$;
revoke all on function private.purge_account_devices() from public, anon, authenticated;

do $$
begin
    if exists (select 1 from pg_extension where extname = 'pg_cron') then
        perform cron.unschedule(jobid) from cron.job where jobname = 'travelplanner-purge-account-devices';
        perform cron.schedule('travelplanner-purge-account-devices', '37 3 * * *', 'select private.purge_account_devices()');
    else
        raise notice 'pg_cron missing: old device notes are not purged. Enable it in Database -> Extensions and re-run this file.';
    end if;
end $$;

notify pgrst, 'reload schema';
