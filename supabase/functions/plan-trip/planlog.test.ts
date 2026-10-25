// Run: node --test supabase/functions/plan-trip/planlog.test.ts   (Node 22.18+, TypeScript types are stripped)
// TP-54: the AI plan log row - summary only, no plan text.
import { test } from "node:test";
import assert from "node:assert/strict";
import { planLogEntry, shortMessage, tokenSubject } from "./planlog.ts";

const uid = "0f8fad5b-d9cb-469f-a165-70867728950e";
const token = (payload: unknown) =>
  "Bearer x." + Buffer.from(JSON.stringify(payload)).toString("base64url") + ".sig";

test("the account id comes from the token", () => {
  assert.equal(tokenSubject(token({ sub: uid, role: "authenticated" })), uid);
  assert.equal(tokenSubject(token({ sub: "not-a-uuid" })), null);
  assert.equal(tokenSubject(token({})), null);
  assert.equal(tokenSubject("Bearer a.%%%.c"), null);
  assert.equal(tokenSubject("Bearer nothing"), null);
});

test("a good plan: status, model, duration, tokens", () => {
  const e = planLogEntry({
    requestId: "r1", userId: uid, status: "ok", model: "gpt-5-mini", reasoning: "low", modelSource: "panel",
    startedAt: 1_000, endedAt: 43_260, tokens: { prompt: 1200, completion: 5400, reasoning: 2000 },
    days: 3, places: 12, replaced: 1, dropped: 0, ip: "203.0.113.4, 10.0.0.1", device: "TravelPlanner/1.0",
  });
  assert.equal(e.action, "ai.plan");
  assert.equal(e.actor_type, "user");
  assert.equal(e.actor_id, uid);
  assert.equal(e.result, "ok");
  assert.equal(e.target_type, "ai_plan");
  assert.equal(e.target_id, "r1");
  assert.equal(e.ip, "203.0.113.4");
  assert.deepEqual(e.detail, {
    status: "ok", duration_s: 42.3, model: "gpt-5-mini", reasoning: "low", model_source: "panel",
    tokens: { prompt: 1200, completion: 5400, reasoning: 2000 }, days: 3, places: 12, replaced: 1, dropped: 0,
  });
});

test("a failure: error kind and a short, clean message", () => {
  const e = planLogEntry({
    requestId: "r2", userId: null, status: "refunded", startedAt: 0, endedAt: 500,
    tokens: { prompt: 0, completion: 0, reasoning: 0 }, errorKind: "http_500",
    errorMessage: "OpenAI 500: Bearer sk-abcdefghijklmnop failed for ali@example.com",
  });
  const d = e.detail as Record<string, unknown>;
  assert.equal(e.result, "error");
  assert.equal(d.status, "refunded");
  assert.equal(d.error_kind, "http_500");
  assert.equal(d.tokens, undefined);
  assert.equal(d.error, "OpenAI 500: Bearer *** failed for ***@***");
});

test("long messages are cut", () => {
  assert.equal(shortMessage("x".repeat(1000))!.length, 300);
  assert.equal(shortMessage(undefined), undefined);
  assert.equal(shortMessage("sk-proj_1234567890abc leaked"), "sk-*** leaked");
});
