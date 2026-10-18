-- =====================================================================================================
-- TravelPlanner - admin / 26: AI rights given in the admin panel really reach the user.
--
-- The daily AI quota is counted per account AND per device (migration 9): all accounts on one phone share the
-- phone's plans. So a higher limit or "reset today" from the panel (migration 25) did nothing when the phone's own
-- count was already at the limit - e.g. after testing with other (or since deleted) accounts on the same phone.
--
--   1. An account the admin gave a limit of its own (private.ai_user_limits) is no longer held back by the device
--      count: the admin decided for this account. Everyone else is counted as before.
--   2. The app asks ai_plan_quota_status with its device on every quota check, so the devices of an account are
--      remembered (private.ai_user_devices, kept 31 days). admin_reset_ai_today resets today's count of every
--      device the account used, not only the ones it planned on today.
--   3. The panel sees the highest device count of the account today (admin_user / admin_users: ai_device_used_today).
--
-- The global daily ceiling still applies to everyone. Safe to run again.
-- =====================================================================================================

create table if not exists private.ai_user_devices (
    user_id   uuid not null references auth.users (id) on delete cascade,
    device    text not null check (device ~ '^[0-9a-f]{64}$'),
    last_seen date not null default current_date,
    primary key (user_id, device)
);
alter table private.ai_user_devices enable row level security;
revoke all on table private.ai_user_devices from public, anon, authenticated;

-- Remembers that this account uses this device (at most once a day per pair).
create or replace function private.ai_note_device(p_uid uuid, p_device text) returns void
language sql
security definer
set search_path = ''
as $$
    insert into private.ai_user_devices as d (user_id, device, last_seen) values (p_uid, p_device, current_date)
    on conflict (user_id, device) do update set last_seen = current_date
        where d.last_seen <> current_date;
$$;
revoke all on function private.ai_note_device(uuid, text) from public, anon, authenticated;

-- True when the admin set a limit of its own for the account.
create or replace function private.ai_has_own_limit(p_uid uuid) returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    select exists (select 1 from private.ai_user_limits where user_id = p_uid)
$$;
revoke all on function private.ai_has_own_limit(uuid) from public, anon, authenticated;

-- Same as migration 12, plus: notes the device, and an account with its own limit ignores the device count.
-- (Not "stable" any more: it writes the device note.)
create or replace function public.ai_plan_quota_status(p_device text) returns jsonb
language plpgsql
volatile
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
begin
    perform private.ai_note_device(v_uid, v_device);
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date;
    select count into v_global from private.ai_global_usage where day = current_date;
    v_user := coalesce(v_user, 0);
    v_dev := case when v_own then 0 else coalesce(v_dev, 0) end;
    return jsonb_build_object(
        'limit', v_limit,
        'remaining', case when coalesce(v_global, 0) >= private.ai_global_daily_limit() then 0
                          else greatest(v_limit - greatest(v_user, v_dev), 0) end);
end;
$$;

-- Same as migration 12, plus: notes the device, and an account with its own limit ignores the device count
-- (the device is still counted, so other accounts on the phone keep sharing it).
create or replace function public.ai_plan_quota(p_device text) returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
    v_uid    uuid := private.require_uid();
    v_device text := private.ai_check_device(p_device);
    v_limit  int  := private.ai_limit_for(v_uid);
    v_own    boolean := private.ai_has_own_limit(v_uid);
    v_user   int;
    v_dev    int;
    v_global int;
    v_ticket uuid;
begin
    perform private.ai_note_device(v_uid, v_device);

    insert into private.ai_usage (user_id, day, count) values (v_uid, current_date, 0)
    on conflict (user_id, day) do nothing;
    insert into private.ai_device_usage (device, day, count) values (v_device, current_date, 0)
    on conflict (device, day) do nothing;
    insert into private.ai_global_usage (day, count) values (current_date, 0)
    on conflict (day) do nothing;

    -- lock the rows (always in this order: account, device, global), so two requests at once can't both take
    -- the last plan
    select count into v_user from private.ai_usage where user_id = v_uid and day = current_date for update;
    select count into v_dev from private.ai_device_usage where device = v_device and day = current_date for update;
    select count into v_global from private.ai_global_usage where day = current_date for update;

    if v_user >= v_limit or (not v_own and v_dev >= v_limit) then
        raise exception 'daily AI planning limit reached' using errcode = 'PT429';
    end if;
    if v_global >= private.ai_global_daily_limit() then
        raise exception 'global daily AI planning limit reached' using errcode = 'PT429';
    end if;

    update private.ai_usage set count = count + 1 where user_id = v_uid and day = current_date;
    update private.ai_device_usage set count = count + 1 where device = v_device and day = current_date;
    update private.ai_global_usage set count = count + 1 where day = current_date;

    insert into private.ai_quota_ticket (user_id, device, day) values (v_uid, v_device, current_date)
    returning id into v_ticket;

    -- keep the tables small
    delete from private.ai_usage where day < current_date - 31;
    delete from private.ai_device_usage where day < current_date - 31;
    delete from private.ai_global_usage where day < current_date - 366;
    delete from private.ai_quota_ticket where created_at < now() - interval '2 days';
    delete from private.ai_user_devices where last_seen < current_date - 31;

    return jsonb_build_object('limit', v_limit,
                              'remaining', v_limit - (case when v_own then v_user else greatest(v_user, v_dev) end) - 1,
                              'ticket', v_ticket);
end;
$$;

revoke all on function public.ai_plan_quota(text) from public, anon;
revoke all on function public.ai_plan_quota_status(text) from public, anon;
grant execute on function public.ai_plan_quota(text) to authenticated;
grant execute on function public.ai_plan_quota_status(text) to authenticated;

-- ------------------------------------------------------------------------------------------- panel
-- Today's AI plans of the account start again, and of every device it used (today's tickets + noted devices).
create or replace function public.admin_reset_ai_today(p_user_id uuid) returns void
language plpgsql
security definer
set search_path = ''
as $$
begin
    if not exists (select 1 from auth.users where id = p_user_id) then
        raise exception 'no such account' using errcode = 'P0002';
    end if;
    -- same lock order as ai_plan_quota: account, then device
    update private.ai_usage set count = 0 where user_id = p_user_id and day = current_date;
    update private.ai_device_usage d set count = 0
     where d.day = current_date
       and (d.device in (select t.device from private.ai_quota_ticket t where t.user_id = p_user_id and t.day = current_date)
            or d.device in (select u.device from private.ai_user_devices u where u.user_id = p_user_id));
end;
$$;

-- One account as the panel shows it (migration 25, plus the highest device count of today).
create or replace function private.admin_user_row(p_uid uuid) returns jsonb
language sql
stable
security definer
set search_path = ''
as $$
    select jsonb_build_object(
        'id', u.id, 'email', u.email, 'display_name', p.display_name, 'created_at', u.created_at,
        'last_sign_in_at', u.last_sign_in_at, 'state', private.account_state(u.id),
        'trips', (select count(*) from public.trips t where t.owner_id = u.id and t.deleted_at is null),
        'ai_used_today', coalesce((select a.count from private.ai_usage a where a.user_id = u.id and a.day = current_date), 0),
        'ai_device_used_today', coalesce((
            select max(d.count) from private.ai_device_usage d
            where d.day = current_date
              and d.device in (select x.device from private.ai_user_devices x where x.user_id = u.id)), 0),
        'ai_limit', private.ai_limit_for(u.id),
        'ai_custom_limit', (select l.daily_limit from private.ai_user_limits l where l.user_id = u.id))
    from auth.users u
    left join public.profiles p on p.id = u.id
    where u.id = p_uid
$$;
revoke all on function private.admin_user_row(uuid) from public, anon, authenticated;

revoke all on function public.admin_reset_ai_today(uuid) from public, anon, authenticated;
grant execute on function public.admin_reset_ai_today(uuid) to service_role;
