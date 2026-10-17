// Run: node --test supabase/functions/purge-files/cleanup.test.ts   (Node 22.18+, TypeScript types are stripped)
// G7: who may call the clean-up, and how a batch moves from the queue through the Storage API.
import { test } from "node:test";
import assert from "node:assert/strict";
import { BATCH_SIZE, isAuthorized, MAX_ROUNDS, runCleanup } from "./cleanup.ts";

const cfg = { supabaseUrl: "https://x.supabase.co", serviceKey: "service-key" };
const secret = "0123456789abcdef-secret";
const quiet = () => {};

type Call = { method: string; url: string; body: any; auth: string | null };

/** A fake Supabase: the queue hands out `batches` one after another; storageStatus answers the delete. */
function fakeSupabase(batches: string[][], storageStatus = 200) {
  const calls: Call[] = [];
  const queue = [...batches];
  const fetchFn = (async (input: string | URL | Request, init?: RequestInit) => {
    const url = String(input);
    const body = init?.body ? JSON.parse(String(init.body)) : null;
    calls.push({ method: init?.method ?? "GET", url, body, auth: new Headers(init?.headers).get("Authorization") });
    if (url.endsWith("/rpc/file_cleanup_claim")) return Response.json(queue.shift() ?? []);
    if (url.endsWith("/rpc/file_cleanup_done")) return Response.json(body.p_paths.length);
    if (url.endsWith("/storage/v1/object/trip-files")) {
      return storageStatus === 200 ? Response.json([]) : new Response("boom", { status: storageStatus });
    }
    return new Response("not found", { status: 404 });
  }) as typeof fetch;
  return { fetchFn, calls };
}

test("only the configured secret is accepted", () => {
  assert.equal(isAuthorized(`Bearer ${secret}`, secret), true);
  assert.equal(isAuthorized(`Bearer ${secret}x`, secret), false);
  assert.equal(isAuthorized(`Bearer ${secret.slice(0, -1)}`, secret), false);
  assert.equal(isAuthorized(secret, secret), false, "Bearer prefix is required");
  assert.equal(isAuthorized(null, secret), false);
});

test("without a (long enough) secret nobody gets in", () => {
  assert.equal(isAuthorized("Bearer ", undefined), false);
  assert.equal(isAuthorized("Bearer ", ""), false);
  assert.equal(isAuthorized("Bearer short", "short"), false);
});

test("an empty queue deletes nothing", async () => {
  const { fetchFn, calls } = fakeSupabase([]);
  assert.deepEqual(await runCleanup(fetchFn, cfg, quiet), { deleted: 0, rounds: 0, failed: false });
  assert.equal(calls.length, 1);
});

test("a claimed batch is deleted through the Storage API, then confirmed", async () => {
  const paths = ["trips/t/a.jpg", "trips/t/b.pdf"];
  const { fetchFn, calls } = fakeSupabase([paths]);
  assert.deepEqual(await runCleanup(fetchFn, cfg, quiet), { deleted: 2, rounds: 1, failed: false });

  assert.deepEqual(calls.map((c) => `${c.method} ${new URL(c.url).pathname}`), [
    "POST /rest/v1/rpc/file_cleanup_claim",
    "DELETE /storage/v1/object/trip-files",
    "POST /rest/v1/rpc/file_cleanup_done",
  ]);
  assert.deepEqual(calls[0].body, { p_limit: BATCH_SIZE });
  assert.deepEqual(calls[1].body, { prefixes: paths });
  assert.deepEqual(calls[2].body, { p_paths: paths });
  assert.ok(calls.every((c) => c.auth === "Bearer service-key"), "service role key on every call");
});

test("full batches keep going until a short one", async () => {
  const full = Array.from({ length: BATCH_SIZE }, (_, i) => `trips/t/${i}.jpg`);
  const { fetchFn } = fakeSupabase([full, full, ["trips/t/last.jpg"]]);
  assert.deepEqual(await runCleanup(fetchFn, cfg, quiet), { deleted: 2 * BATCH_SIZE + 1, rounds: 3, failed: false });
});

test("one call stops after MAX_ROUNDS", async () => {
  const full = Array.from({ length: BATCH_SIZE }, (_, i) => `trips/t/${i}.jpg`);
  const { fetchFn } = fakeSupabase(Array.from({ length: MAX_ROUNDS + 5 }, () => full));
  const result = await runCleanup(fetchFn, cfg, quiet);
  assert.equal(result.rounds, MAX_ROUNDS);
});

test("a refused delete is not confirmed, so the queue retries it later", async () => {
  const { fetchFn, calls } = fakeSupabase([["trips/t/a.jpg"]], 500);
  assert.deepEqual(await runCleanup(fetchFn, cfg, quiet), { deleted: 0, rounds: 1, failed: true });
  assert.ok(!calls.some((c) => c.url.endsWith("/file_cleanup_done")));
});

test("a failing claim throws (the function answers 502)", async () => {
  const fetchFn = (async () => new Response("nope", { status: 401 })) as typeof fetch;
  await assert.rejects(runCleanup(fetchFn, cfg, quiet), /file_cleanup_claim failed: 401/);
});
