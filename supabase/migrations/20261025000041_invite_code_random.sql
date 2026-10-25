-- =====================================================================================================
-- TravelPlanner - TP-64 / 41: every character of an invite code is fully random.
--
-- new_invite_code (migration 4) took the first 8 bytes of a random UUID. In a v4 UUID byte 6 carries the version
-- (0x4_), so the 7th character could only be one of 16 letters, and "byte % 31" favoured the first 8 letters a
-- little. Now only the 14 fully random bytes of each UUID are used (not byte 6, not byte 8 with the variant bits),
-- and a byte is used only when it is below 248 (= 8 x 31), so each of the 31 characters is equally likely.
-- Same alphabet and length as before; codes already handed out stay valid. Safe to run again.
-- =====================================================================================================

create or replace function private.new_invite_code() returns text
language plpgsql
volatile
set search_path = ''
as $$
declare
    v_alphabet constant text := 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';  -- 31 letters/digits
    v_bytes bytea;
    v_code  text := '';
    v_byte  int;
    i int;
begin
    while length(v_code) < 8 loop
        v_bytes := uuid_send(gen_random_uuid());
        for i in 0..15 loop
            continue when i in (6, 8);              -- version and variant bits
            v_byte := get_byte(v_bytes, i);
            continue when v_byte >= 248;            -- no modulo bias
            v_code := v_code || substr(v_alphabet, v_byte % 31 + 1, 1);
            exit when length(v_code) = 8;
        end loop;
    end loop;
    return v_code;
end;
$$;

revoke all on function private.new_invite_code() from public, anon, authenticated;
