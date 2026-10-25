// TravelPlanner - AI trip planner Edge Function ("plan-trip").
//
//   POST /functions/v1/plan-trip   (headers: apikey = publishable key, Authorization = Bearer <user access token>)
//   body: { deviceId, language, destination, startDate, days, interests[], pace, budget, companions, notes, existingPlaces[] }
//   200 → { plan: TripPlan, remaining: number, truncatedDays: boolean, requestId: string }
//   400 invalid_request · 401 not_signed_in · 403 account_disabled · 413 body_too_large · 429 quota_exceeded
//   503 ai_paused (AI is in maintenance, set in the admin panel)
//   502 ai_failed
//
// The OpenAI key lives only here, as the secret OPENAI_API_KEY. The user's own token pays for one plan of the
// daily quota first (public.ai_plan_quota: per account, per device, lower for new accounts, and a global daily
// ceiling for everyone - migrations 9 and 12) - which also refuses banned or deleted accounts.
// SEC-02: the plan is given back (public.ai_plan_refund, migrations 10/12; at most 2 a day per account) ONLY when
// OpenAI gave no answer at all (network error, HTTP error). An answer that was cut off, empty, not JSON or an
// empty plan - and our own timeout - is billed by OpenAI, so it costs the user the plan too.
// The model and its reasoning effort are chosen in the admin panel (Ayarlar, migration 31: public.ai_model_settings,
// read with the service role key). Left empty there (or unreadable - logged as a warning), the optional secrets AI_MODEL (default gpt-5-mini - nano made
// plans that repeated places and mixed far-apart areas) and AI_REASONING (minimal | low | medium | high; default low,
// minimal for nano models) decide. AI_MAX_TOKENS: upper bound of max_completion_tokens for the plan, default 16000 -
// see planTokenBudget in planner.ts.
//
// Every place of the plan is checked on the map (OpenStreetMap, see places.ts): meals are planned at real venues
// found around the destination, places that can't be found are replaced (once more by the model) or dropped,
// and the places sent back carry the map's coordinates (verified: true).
//
// TP-54: every request that got past the token check leaves one audit log row (action "ai.plan", planlog.ts):
// status, model, reasoning, duration, tokens, error kind - never the plan or what the user typed. Its requestId goes
// back to the app, which reports how long the user took to save the plan (public.ai_plan_saved, migration 36).

import {
  buildMessages, buildReplacementMessages, MAX_BODY_BYTES, MAX_PLAN_TOKENS, parseRequest, PLAN_SCHEMA, planTokenBudget,
  REPLACEMENT_SCHEMA, RequestError, sanitizePlan, sanitizeReplacements,
} from "./planner.ts";
import { AiCallError, askOpenAi, BodyTooLarge, chooseModel, looksLikeBearerJwt, readJsonBody, type TokenTally } from "./openai.ts";
import { type PlanLog, planLogEntry, type PlanStatus, tokenSubject } from "./planlog.ts";
import { findCenter, foodVenues, type Point, type Venue, verifyPlan } from "./places.ts";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

function fail(status: number, error: string, detail?: string): Response {
  return json(status, { error, detail });
}

type QuotaResult =
  | { ok: true; remaining: number; ticket: string | null }
  | { ok: false; response: Response; status?: PlanStatus };

async function useQuota(authorization: string, apikey: string, deviceId: string): Promise<QuotaResult> {
  const url = `${Deno.env.get("SUPABASE_URL")}/rest/v1/rpc/ai_plan_quota`;
  const res = await fetch(url, {
    method: "POST",
    headers: { apikey, Authorization: authorization, "Content-Type": "application/json" },
    body: JSON.stringify({ p_device: deviceId }),
  });
  if (res.ok) {
    const body = await res.json();
    // ticket: a one-time receipt to give the plan back if the AI fails (migration 10; null before it ran)
    return { ok: true, remaining: Number(body?.remaining ?? 0), ticket: typeof body?.ticket === "string" ? body.ticket : null };
  }

  const text = await res.text();
  if (res.status === 429) return { ok: false, response: fail(429, "quota_exceeded"), status: "quota_exceeded" };
  // AI paused from the admin panel (migration 29)
  if (res.status === 503) return { ok: false, response: fail(503, "ai_paused"), status: "paused" };
  if (res.status === 401) return { ok: false, response: fail(401, "not_signed_in") };
  if (text.includes("account disabled")) return { ok: false, response: fail(403, "account_disabled"), status: "account_disabled" };
  if (text.includes("not signed in")) return { ok: false, response: fail(401, "not_signed_in") };
  console.error("quota check failed", res.status, text);
  return { ok: false, response: fail(502, "quota_check_failed"), status: "quota_check_failed" };
}

/**
 * The panel's model choice (migration 31). Null when it can't be read (no service key, migration not run, network):
 * then the secrets decide, so a settings problem never stops the planner.
 */
async function panelModel(): Promise<{ model?: unknown; reasoning?: unknown } | null> {
  const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!key) return null;
  try {
    const res = await fetch(`${Deno.env.get("SUPABASE_URL")}/rest/v1/rpc/ai_model_settings`, {
      method: "POST",
      headers: { apikey: key, Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
      body: "{}",
      signal: AbortSignal.timeout(5_000),
    });
    if (!res.ok) {
      console.warn("model settings not read", res.status, (await res.text()).slice(0, 200));
      return null;
    }
    const body = await res.json();
    return body && typeof body === "object" ? body : null;
  } catch (e) {
    console.warn("model settings not read", String(e));
    return null;
  }
}

/** One "ai.plan" audit log row (TP-54). Never throws and never holds up the answer for more than 5 s. */
async function writePlanLog(entry: PlanLog): Promise<void> {
  const key = Deno.env.get("SUPABASE_SERVICE_ROLE_KEY");
  if (!key) return;
  try {
    const res = await fetch(`${Deno.env.get("SUPABASE_URL")}/rest/v1/rpc/audit_log_write`, {
      method: "POST",
      headers: { apikey: key, Authorization: `Bearer ${key}`, "Content-Type": "application/json" },
      body: JSON.stringify({ p: planLogEntry(entry) }),
      signal: AbortSignal.timeout(5_000),
    });
    if (!res.ok) console.warn("plan log not written", res.status, (await res.text()).slice(0, 200));
  } catch (e) {
    console.warn("plan log not written", String(e));
  }
}

/** Gives back the plan of a request OpenAI never answered (public.ai_plan_refund). Never throws. */
async function refundQuota(authorization: string, apikey: string, ticket: string | null): Promise<boolean> {
  if (!ticket) return false;
  try {
    const res = await fetch(`${Deno.env.get("SUPABASE_URL")}/rest/v1/rpc/ai_plan_refund`, {
      method: "POST",
      headers: { apikey, Authorization: authorization, "Content-Type": "application/json" },
      body: JSON.stringify({ p_ticket: ticket }),
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) {
      console.error("quota refund failed", res.status, (await res.text()).slice(0, 300));
      return false;
    }
    return (await res.json()) === true;
  } catch (e) {
    console.error("quota refund failed", String(e));
    return false;
  }
}

Deno.serve(async (req) => {
  if (req.method !== "POST") return fail(405, "method_not_allowed");

  // cheap checks first: without a session token the body isn't even read (the function runs --no-verify-jwt;
  // the token itself is checked by ai_plan_quota)
  const authorization = req.headers.get("Authorization") ?? "";
  const apikey = req.headers.get("apikey") ?? "";
  if (!looksLikeBearerJwt(authorization) || !apikey || apikey.length > 4096) return fail(401, "not_signed_in");
  const requestId = crypto.randomUUID();
  const startedAt = Date.now();
  const tokens: TokenTally = { prompt: 0, completion: 0, reasoning: 0 };
  const log: PlanLog = {
    requestId, userId: tokenSubject(authorization), status: "error", startedAt, endedAt: startedAt, tokens,
    device: req.headers.get("User-Agent"), ip: req.headers.get("X-Forwarded-For"),
  };
  /** Writes the log row, then gives the response. */
  const done = async (response: Response, status: PlanStatus, more: Partial<PlanLog> = {}) => {
    Object.assign(log, more, { status, endedAt: Date.now() });
    await writePlanLog(log);
    return response;
  };

  let parsed;
  try {
    parsed = parseRequest(await readJsonBody(req, MAX_BODY_BYTES));
  } catch (e) {
    if (e instanceof BodyTooLarge) return fail(413, "body_too_large");
    return fail(400, "invalid_request", e instanceof RequestError ? e.message : "body is not JSON");
  }

  const openAiKey = Deno.env.get("OPENAI_API_KEY");
  if (!openAiKey) {
    console.error("OPENAI_API_KEY secret is not set");
    return await done(fail(502, "ai_not_configured"), "not_configured", { errorKind: "not_configured" });
  }

  const { request, truncatedDays } = parsed;
  // the quota first: a request without plans left (or without a valid session) costs the map services nothing.
  // The panel's model choice is read at the same time.
  const [quota, panel] = await Promise.all([useQuota(authorization, apikey, request.deviceId), panelModel()]);
  if (!quota.ok) return quota.status ? await done(quota.response, quota.status) : quota.response;

  // the plan needs some thinking (grouping sights by area, one visit per place); the replacement question doesn't
  const { model, effort: planEffort, source } = chooseModel(panel, { model: Deno.env.get("AI_MODEL"), reasoning: Deno.env.get("AI_REASONING") });
  // TP-47: the panel decides; falling back to the secret or the default is kept (a settings problem never stops the
  // planner) but stands out in the log
  Object.assign(log, { model, reasoning: planEffort, modelSource: source, days: request.days });
  if (source === "panel") console.log(`model ${model}, reasoning ${planEffort} (panel)`);
  else console.warn(`model ${model}, reasoning ${planEffort} (${source}: panel ${panel ? "has no model" : "not read"})`);

  const planTokens = planTokenBudget(request.days, request.pace, planEffort, Number(Deno.env.get("AI_MAX_TOKENS") ?? MAX_PLAN_TOKENS));

  /** One structured-output call: the parsed JSON answer; throws AiCallError on any failure. */
  const started = Date.now();
  const ask = (messages: unknown, name: string, schema: unknown, maxTokens: number, timeoutMs: number, effort: string) =>
    askOpenAi(fetch, { apiKey: openAiKey, model, messages, name, schema, maxTokens, timeoutMs, effort, tally: tokens });

  // the AI failed. The plan goes back to the quota only when OpenAI never answered (nothing was billed); an answer
  // that came back unusable was paid for, so it counts - otherwise one could loop "cut off" answers for free.
  const aiFailed = async (billed: boolean, detail: string, errorKind: string, errorMessage?: string) => {
    const refunded = !billed && await refundQuota(authorization, apikey, quota.ticket);
    return await done(json(502, { error: "ai_failed", detail, remaining: refunded ? quota.remaining + 1 : quota.remaining, requestId }),
                      refunded ? "refunded" : "error", { errorKind, errorMessage });
  };

  // the destination and the real restaurants around it
  let center: Point | null = request.center;
  let venues: Venue[] = [];
  try {
    center ??= await findCenter(fetch, request.destination);
    venues = center ? await foodVenues(fetch, center) : [];
  } catch (e) {
    console.warn("map lookup failed", String(e));
  }

  let plan;
  try {
    plan = sanitizePlan(await ask(buildMessages(request, venues), "trip_plan", PLAN_SCHEMA, planTokens, 100_000, planEffort), request.days);
  } catch (e) {
    // anything else than AiCallError (a bug in sanitizePlan) happened after a billed answer
    const billed = !(e instanceof AiCallError) || e.billed;
    console.error(`OpenAI call failed (${billed ? "billed, no refund" : "not billed, refund"})`, String(e));
    return await aiFailed(billed, billed ? "ai_answer_unusable" : "ai_unreachable",
                          e instanceof AiCallError ? e.kind : "parse_error", String(e));
  }
  if (plan.days.length === 0 && plan.places.length === 0) return await aiFailed(true, "empty plan", "empty_plan");

  let checked;
  try {
    // the function has about 150 s: the second question only when there's time left for it
    const canAskAgain = Date.now() - started < 85_000;
    const result = await verifyPlan(plan, center, venues, fetch, canAskAgain
      ? async (missing, used, v) => sanitizeReplacements(
        await ask(buildReplacementMessages(request, missing, used, v), "replacements", REPLACEMENT_SCHEMA, 6000, 35_000, "minimal"))
      : null,
      started + 125_000); // the map check stops in time; places it couldn't check are left to the app
    console.log(`places: ${result.places.length - result.unchecked} on the map, ${result.unchecked} unchecked (map service didn't answer), ` +
      `${result.replaced} replaced, ${result.dropped} dropped; ${venues.length} venues`);
    const { replaced, dropped, unchecked: _u, ...rest } = result;
    Object.assign(log, { replaced, dropped });
    checked = rest;
  } catch (e) {
    // the map services failing must not cost the user the plan: send it unchecked
    console.error("map check failed", String(e));
    checked = { ...plan, places: plan.places.map((p) => ({ ...p, verified: false })) };
  }

  return await done(json(200, { plan: checked, remaining: quota.remaining, truncatedDays, requestId }), "ok",
                    { places: checked.places.length, truncatedDays });
});
