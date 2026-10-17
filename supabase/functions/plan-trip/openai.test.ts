// Run: node --test supabase/functions/plan-trip/openai.test.ts   (Node 22.18+, TypeScript types are stripped)
// SEC-02: which failures are billed (no refund) and the request-body guard.
import { test } from "node:test";
import assert from "node:assert/strict";
import { AiCallError, askOpenAi, BodyTooLarge, looksLikeBearerJwt, readJsonBody } from "./openai.ts";
import { MAX_DAYS, MAX_PLAN_TOKENS, planTokenBudget } from "./planner.ts";

const opts = { apiKey: "k", model: "m", messages: [], name: "trip_plan", schema: {}, maxTokens: 1000, timeoutMs: 1000, effort: "low" };
const quiet = () => {};

function answer(finish: string, content: string | null, status = 200): typeof fetch {
  return (async () => new Response(JSON.stringify({
    choices: [{ finish_reason: finish, message: { content } }],
    usage: { prompt_tokens: 10, completion_tokens: 1000 },
  }), { status })) as typeof fetch;
}

async function failure(fetchFn: typeof fetch): Promise<AiCallError> {
  try {
    await askOpenAi(fetchFn, opts, quiet);
  } catch (e) {
    assert.ok(e instanceof AiCallError, String(e));
    return e;
  }
  assert.fail("no error");
}

test("a good answer is parsed", async () => {
  assert.deepEqual(await askOpenAi(answer("stop", '{"a":1}'), opts, quiet), { a: 1 });
});

test("an answer cut off at max tokens is billed: no refund", async () => {
  assert.equal((await failure(answer("length", '{"a":'))).billed, true);
});

test("an empty or broken answer is billed: no refund", async () => {
  assert.equal((await failure(answer("stop", null))).billed, true);
  assert.equal((await failure(answer("stop", "not json"))).billed, true);
  const unreadable = (async () => new Response("<html>", { status: 200 })) as typeof fetch;
  assert.equal((await failure(unreadable)).billed, true);
});

test("our timeout is billed (OpenAI was generating): no refund", async () => {
  const slow = (async () => { throw new DOMException("timed out", "TimeoutError"); }) as typeof fetch;
  assert.equal((await failure(slow)).billed, true);
});

test("no answer at all is not billed: refund", async () => {
  const offline = (async () => { throw new TypeError("fetch failed: connection refused"); }) as typeof fetch;
  assert.equal((await failure(offline)).billed, false);
  assert.equal((await failure(answer("stop", null, 500))).billed, false);
  assert.equal((await failure(answer("stop", null, 503))).billed, false);
  assert.equal((await failure(answer("stop", null, 429))).billed, false);
});

test("the usage of every answer is logged", async () => {
  const lines: string[] = [];
  await askOpenAi(answer("length", "{"), opts, (l) => lines.push(l)).catch(() => {});
  assert.match(lines[0], /completion 1000/);
  assert.match(lines[0], /finish length/);
});

test("the token budget fits the trip and never passes the cap", () => {
  const small = planTokenBudget(1, "relaxed", "low");
  const big = planTokenBudget(MAX_DAYS, "packed", "low");
  assert.ok(small < 6000, `1 relaxed day: ${small}`);
  assert.ok(big <= MAX_PLAN_TOKENS && big >= 12000, `10 packed days: ${big}`);
  assert.ok(planTokenBudget(5, "balanced", "low") < planTokenBudget(5, "packed", "low"));
  assert.ok(planTokenBudget(3, "balanced", "minimal") < planTokenBudget(3, "balanced", "low"));
  assert.equal(planTokenBudget(MAX_DAYS, "packed", "high", 9000), 9000);   // AI_MAX_TOKENS lowers the cap
  assert.equal(planTokenBudget(MAX_DAYS, "packed", "low", NaN), MAX_PLAN_TOKENS); // a bad secret falls back
  assert.equal(planTokenBudget(500, "packed", "low"), big);                 // days are capped like the plan
  assert.ok(MAX_PLAN_TOKENS < 32000);
});

function req(body: string, headers: Record<string, string> = {}): Request {
  return new Request("https://x/functions/v1/plan-trip", { method: "POST", body, headers });
}

test("the body is read up to 16 KB", async () => {
  assert.deepEqual(await readJsonBody(req('{"a":1}'), 16384), { a: 1 });
  await assert.rejects(readJsonBody(req("x".repeat(20000)), 16384), BodyTooLarge);
  await assert.rejects(readJsonBody(req("{}", { "Content-Length": "999999" }), 16384), BodyTooLarge);
  await assert.rejects(readJsonBody(req("{bad"), 16384), SyntaxError);
});

test("only a JWT-shaped bearer token gets past the door", () => {
  assert.equal(looksLikeBearerJwt("Bearer eyJhbGciOi.eyJzdWIiOi.c2lnbmF0dXJl"), true);
  assert.equal(looksLikeBearerJwt(""), false);
  assert.equal(looksLikeBearerJwt("Bearer "), false);
  assert.equal(looksLikeBearerJwt("Bearer sb_publishable_abc"), false);
  assert.equal(looksLikeBearerJwt("Bearer a.b.c" + "d".repeat(9000)), false);
});
