// TravelPlanner - plan-trip: plans made in the background (TP-57, migration 39).
//
// The app sends { background: true, jobId: <uuid it keeps on the phone> }. plan-trip then claims the job
// (public.ai_plan_job_begin, with the user's token, before the quota: one plan at a time per account), answers at
// once with { jobId, status: "running", remaining } and makes the plan after the answer (EdgeRuntime.waitUntil),
// writing the same answer it would have sent into the job (public.ai_plan_job_finish). The app asks for it with
// public.ai_plan_job until it is ready - also after being closed and opened again.
// Without the migration (or without waitUntil) the plan is made as before and sent in the answer.
// No Deno APIs, so it can be unit-tested with plain Node (see jobs.test.ts).

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** The job id of a background request, or null for a plan sent in the answer (older apps). */
export function jobIdOf(body: unknown): string | null {
  if (typeof body !== "object" || body === null) return null;
  const b = body as Record<string, unknown>;
  if (b.background !== true || typeof b.jobId !== "string") return null;
  const id = b.jobId.trim().toLowerCase();
  return UUID.test(id) ? id : null;
}

/**
 * What ai_plan_job_begin said: "ok", "busy" (this account already has a plan being made), "exists" (the id is
 * taken) - or "unavailable" (migration 39 not run, a server hiccup, a refused token): then the plan is made the old
 * way, and the quota check that follows answers for a bad token.
 */
export type BeginResult = "ok" | "busy" | "exists" | "unavailable";

export function beginResult(ok: boolean, body: unknown): BeginResult {
  return ok && (body === "ok" || body === "busy" || body === "exists") ? body : "unavailable";
}

/** The ai_plan_job_finish arguments for the answer the request would have sent. */
export function finishArgs(jobId: string, status: number, body: unknown): Record<string, unknown> {
  const b = typeof body === "object" && body !== null ? body as Record<string, unknown> : {};
  const remaining = typeof b.remaining === "number" && Number.isFinite(b.remaining) ? Math.trunc(b.remaining) : null;
  if (status === 200 && typeof b.plan === "object" && b.plan !== null) {
    return { p_id: jobId, p_status: "done", p_result: b, p_error: null, p_remaining: remaining };
  }
  return { p_id: jobId, p_status: "failed", p_result: null, p_error: typeof b.error === "string" ? b.error : "ai_failed", p_remaining: remaining };
}

/** EdgeRuntime.waitUntil of Supabase's runtime, or null where there is none (then the plan is sent in the answer). */
export function waitUntilOf(runtime: unknown): ((work: Promise<unknown>) => void) | null {
  const r = runtime as { waitUntil?: unknown } | undefined;
  return r && typeof r.waitUntil === "function" ? (work) => (r.waitUntil as (p: Promise<unknown>) => void).call(r, work) : null;
}
