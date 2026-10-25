// TravelPlanner - plan-trip: one audit log row per plan request (TP-54, written through public.audit_log_write,
// migration 34). Summary only: who, how it ended, model and reasoning, how long it took, tokens, the error kind and a
// short message. Never the plan, the destination or the user's notes.
// No Deno APIs, so it can be unit-tested with plain Node (see planlog.test.ts).

import type { TokenTally } from "./openai.ts";

/**
 * How a request ended:
 *   ok              the plan was sent
 *   error           the AI failed and the plan was not given back
 *   refunded        the AI failed (OpenAI never answered) and the plan went back to the quota
 *   quota_exceeded  no plans left today
 *   paused          AI planning is in maintenance (admin panel)
 *   busy            TP-57: the account already has a plan being made (nothing was spent)
 *   account_disabled, not_configured, quota_check_failed
 */
export type PlanStatus =
  | "ok" | "error" | "refunded" | "quota_exceeded" | "paused" | "busy" | "account_disabled" | "not_configured"
  | "quota_check_failed";

export interface PlanLog {
  requestId: string;
  userId: string | null;
  status: PlanStatus;
  model?: string;
  reasoning?: string;
  modelSource?: string;
  startedAt: number;
  endedAt: number;
  tokens?: TokenTally;
  errorKind?: string;
  errorMessage?: string;
  days?: number;
  places?: number;
  replaced?: number;
  dropped?: number;
  truncatedDays?: boolean;
  device?: string | null;
  ip?: string | null;
}

const MAX_MESSAGE = 300;

/** The account id ("sub") of a Supabase access token, read without checking it: ai_plan_quota checked the token. */
export function tokenSubject(authorization: string): string | null {
  const payload = authorization.replace(/^Bearer /, "").split(".")[1];
  if (!payload) return null;
  try {
    const text = atob(payload.replace(/-/g, "+").replace(/_/g, "/").padEnd(Math.ceil(payload.length / 4) * 4, "="));
    const sub = JSON.parse(text)?.sub;
    return typeof sub === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(sub) ? sub : null;
  } catch {
    return null;
  }
}

/** Keys, tokens and e-mail addresses out of an error text, which is cut to 300 characters. */
export function shortMessage(text: string | undefined): string | undefined {
  if (!text) return undefined;
  const clean = text
    .replace(/Bearer\s+[A-Za-z0-9._-]+/g, "Bearer ***")
    .replace(/sk-[A-Za-z0-9_-]{8,}/g, "sk-***")
    .replace(/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/g, "***@***")
    .replace(/\s+/g, " ")
    .trim();
  return clean.length <= MAX_MESSAGE ? clean : clean.slice(0, MAX_MESSAGE - 1) + "…";
}

/** The audit_log_write argument of one request. */
export function planLogEntry(l: PlanLog): Record<string, unknown> {
  const detail: Record<string, unknown> = {
    status: l.status,
    duration_s: Math.max(0, Math.round((l.endedAt - l.startedAt) / 100) / 10),
  };
  if (l.model) detail.model = l.model;
  if (l.reasoning) detail.reasoning = l.reasoning;
  if (l.modelSource) detail.model_source = l.modelSource;
  if (l.tokens && l.tokens.prompt + l.tokens.completion > 0) {
    detail.tokens = { prompt: l.tokens.prompt, completion: l.tokens.completion, reasoning: l.tokens.reasoning };
  }
  if (l.errorKind) detail.error_kind = l.errorKind;
  const message = shortMessage(l.errorMessage);
  if (message) detail.error = message;
  if (l.days !== undefined) detail.days = l.days;
  if (l.places !== undefined) detail.places = l.places;
  if (l.replaced !== undefined) detail.replaced = l.replaced;
  if (l.dropped !== undefined) detail.dropped = l.dropped;
  if (l.truncatedDays) detail.truncated_days = true;
  return {
    actor_type: "user",
    actor_id: l.userId,
    action: "ai.plan",
    result: l.status === "ok" ? "ok" : "error",
    target_type: "ai_plan",
    target_id: l.requestId,
    detail,
    device: l.device ?? null,
    ip: l.ip ? l.ip.split(",")[0].trim() : null,
  };
}
