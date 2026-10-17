// TravelPlanner - G7: removes the files of deleted attachments from Storage (see migration 16).
// Kept free of Deno APIs so it runs under `node --test` too; index.ts only wires it to the request.

export const BUCKET = "trip-files";
/** Paths per round (the Storage API deletes at most 1000 per call). */
export const BATCH_SIZE = 500;
/** Rounds per call, so one call can't run past the Edge Function's time limit. */
export const MAX_ROUNDS = 20;

export type CleanupConfig = { supabaseUrl: string; serviceKey: string };
export type CleanupResult = { deleted: number; rounds: number; failed: boolean };
type Log = (...args: unknown[]) => void;

/** Constant-time comparison of the caller's "Authorization: Bearer <secret>" with the configured secret. */
export function isAuthorized(header: string | null, secret: string | undefined): boolean {
  if (!secret || secret.length < 16 || !header) return false;
  const match = /^Bearer (.+)$/.exec(header);
  if (!match) return false;
  const a = new TextEncoder().encode(match[1]);
  const b = new TextEncoder().encode(secret);
  let diff = a.length ^ b.length;
  for (let i = 0; i < b.length; i++) diff |= (a[i] ?? 0) ^ b[i];
  return diff === 0;
}

function headers(cfg: CleanupConfig): Record<string, string> {
  return { apikey: cfg.serviceKey, Authorization: `Bearer ${cfg.serviceKey}`, "Content-Type": "application/json" };
}

async function rpc(fetchFn: typeof fetch, cfg: CleanupConfig, name: string, args: unknown): Promise<unknown> {
  const res = await fetchFn(`${cfg.supabaseUrl}/rest/v1/rpc/${name}`, {
    method: "POST", headers: headers(cfg), body: JSON.stringify(args),
  });
  if (!res.ok) throw new Error(`${name} failed: ${res.status} ${(await res.text()).slice(0, 300)}`);
  return await res.json();
}

/**
 * Claims a batch (public.file_cleanup_claim), deletes it through the Storage API and confirms it
 * (public.file_cleanup_done), until the queue is empty or MAX_ROUNDS. A batch the Storage API refused stays
 * claimed; the queue hands it out again after 15 minutes (at most 10 tries).
 */
export async function runCleanup(fetchFn: typeof fetch, cfg: CleanupConfig, log: Log = console.error): Promise<CleanupResult> {
  let deleted = 0;
  let rounds = 0;
  while (rounds < MAX_ROUNDS) {
    const claimed = await rpc(fetchFn, cfg, "file_cleanup_claim", { p_limit: BATCH_SIZE });
    const paths = Array.isArray(claimed) ? claimed.filter((p): p is string => typeof p === "string") : [];
    if (paths.length === 0) break;
    rounds++;

    // Missing objects are not an error: they are simply not in the answer.
    const res = await fetchFn(`${cfg.supabaseUrl}/storage/v1/object/${BUCKET}`, {
      method: "DELETE", headers: headers(cfg), body: JSON.stringify({ prefixes: paths }),
    });
    if (!res.ok) {
      log("storage delete failed", res.status, (await res.text()).slice(0, 300));
      return { deleted, rounds, failed: true };
    }
    await res.body?.cancel();

    await rpc(fetchFn, cfg, "file_cleanup_done", { p_paths: paths });
    deleted += paths.length;
    if (paths.length < BATCH_SIZE) break;
  }
  return { deleted, rounds, failed: false };
}
