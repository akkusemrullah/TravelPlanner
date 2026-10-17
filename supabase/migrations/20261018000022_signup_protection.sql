-- =====================================================================================================
-- TravelPlanner - TP-15 / 22: no accounts with throw-away e-mail addresses.
--
-- Bots could open hundreds of accounts with disposable addresses and use up the AI quota and the storage.
-- public.hook_before_user_created is a Supabase Auth "Before User Created" hook: it refuses a new account whose
-- e-mail domain (or a parent domain) is in private.blocked_email_domains. The message carries the marker
-- "disposable_email", which the app turns into its own explanation.
--
-- AFTER RUNNING THIS FILE (Dashboard, once):
--   * Authentication -> Hooks -> "Before User Created" -> Postgres -> public.hook_before_user_created -> Enable.
--   * Authentication -> Providers -> Email: "Confirm email" on; Password requirements: minimum length 8,
--     "Letters and digits" (the app asks for the same since TP-15).
--   * Authentication -> Attack Protection: CAPTCHA on, provider Cloudflare Turnstile, with the widget's secret key
--     (the app already sends Turnstile tokens - SupabaseConfig.CaptchaSiteKey).
-- Add a domain later:  insert into private.blocked_email_domains (domain) values ('example.org') on conflict do nothing;
-- Safe to run again.
-- =====================================================================================================

create table if not exists private.blocked_email_domains (
    domain text primary key check (domain = lower(domain) and domain !~ '\s')
);
revoke all on private.blocked_email_domains from public, anon, authenticated;

insert into private.blocked_email_domains (domain) values
    ('10minutemail.com'), ('10minutemail.net'), ('20minutemail.com'), ('33mail.com'), ('anonbox.net'),
    ('burnermail.io'), ('discard.email'), ('dispostable.com'), ('dropmail.me'), ('emailondeck.com'),
    ('fakeinbox.com'), ('fakemail.net'), ('getairmail.com'), ('getnada.com'), ('guerrillamail.biz'),
    ('guerrillamail.com'), ('guerrillamail.de'), ('guerrillamail.info'), ('guerrillamail.net'), ('guerrillamail.org'),
    ('guerrillamailblock.com'), ('harakirimail.com'), ('inboxkitten.com'), ('incognitomail.org'), ('jetable.org'),
    ('mail.tm'), ('mail-temp.com'), ('mailcatch.com'), ('maildrop.cc'), ('mailinator.com'),
    ('mailinator.net'), ('mailnesia.com'), ('mailsac.com'), ('mintemail.com'), ('mohmal.com'),
    ('moakt.com'), ('mytemp.email'), ('mytrashmail.com'), ('nada.email'), ('sharklasers.com'),
    ('spam4.me'), ('spambox.us'), ('spamgourmet.com'), ('temp-mail.io'), ('temp-mail.org'),
    ('tempail.com'), ('tempmail.com'), ('tempmail.net'), ('tempmail.plus'), ('tempmailo.com'),
    ('tempr.email'), ('throwawaymail.com'), ('trashmail.com'), ('trashmail.de'), ('trashmail.net'),
    ('yopmail.com'), ('yopmail.fr'), ('yopmail.net'), ('emailfake.com'), ('minuteinbox.com')
on conflict (domain) do nothing;

-- true when the address's domain or one of its parent domains is blocked (x.mailinator.com too)
create or replace function private.is_disposable_email(p_email text) returns boolean
language sql
stable
security definer
set search_path = ''
as $$
    with d as (select lower(trim(split_part(coalesce(p_email, ''), '@', 2))) as domain)
    select exists (
        select 1
        from d, private.blocked_email_domains b
        where d.domain <> '' and (d.domain = b.domain or d.domain like '%.' || b.domain));
$$;

revoke all on function private.is_disposable_email(text) from public, anon, authenticated;

create or replace function public.hook_before_user_created(event jsonb) returns jsonb
language plpgsql
stable
security definer
set search_path = ''
as $$
begin
    if private.is_disposable_email(event -> 'user' ->> 'email') then
        return jsonb_build_object('error', jsonb_build_object(
            'http_code', 400,
            'message', 'disposable_email: accounts cannot be created with throw-away e-mail addresses'));
    end if;
    return '{}'::jsonb;
end;
$$;

revoke all on function public.hook_before_user_created(jsonb) from public, anon, authenticated;
do $$ begin
    if exists (select 1 from pg_roles where rolname = 'supabase_auth_admin') then
        execute 'grant execute on function public.hook_before_user_created(jsonb) to supabase_auth_admin';
    end if;
end $$;
