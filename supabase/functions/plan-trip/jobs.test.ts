// Run: node --test supabase/functions/plan-trip/jobs.test.ts   (Node 22.18+, TypeScript types are stripped)
// TP-57: plans made in the background.
import { test } from "node:test";
import assert from "node:assert/strict";
import { beginResult, finishArgs, jobIdOf, waitUntilOf } from "./jobs.ts";

const id = "0f8fad5b-d9cb-469f-a165-70867728950e";

test("only a background request with a uuid has a job", () => {
  assert.equal(jobIdOf({ background: true, jobId: id.toUpperCase() }), id);
  assert.equal(jobIdOf({ background: true, jobId: "not-a-uuid" }), null);
  assert.equal(jobIdOf({ jobId: id }), null);           // older apps: the plan comes in the answer
  assert.equal(jobIdOf({ background: "yes", jobId: id }), null);
  assert.equal(jobIdOf(null), null);
});

test("anything but a known answer of ai_plan_job_begin makes the plan the old way", () => {
  assert.equal(beginResult(true, "ok"), "ok");
  assert.equal(beginResult(true, "busy"), "busy");
  assert.equal(beginResult(true, "exists"), "exists");
  assert.equal(beginResult(false, "ok"), "unavailable");   // 404: migration 39 not run
  assert.equal(beginResult(true, { code: "PGRST" }), "unavailable");
});

test("the finished job holds the answer, or the error code and what is left", () => {
  const answer = { plan: { days: [] }, remaining: 3, truncatedDays: false, requestId: "r1" };
  assert.deepEqual(finishArgs(id, 200, answer), { p_id: id, p_status: "done", p_result: answer, p_error: null, p_remaining: 3 });
  assert.deepEqual(finishArgs(id, 502, { error: "ai_failed", remaining: 4 }),
                   { p_id: id, p_status: "failed", p_result: null, p_error: "ai_failed", p_remaining: 4 });
  assert.deepEqual(finishArgs(id, 500, null), { p_id: id, p_status: "failed", p_result: null, p_error: "ai_failed", p_remaining: null });
});

test("waitUntil is used only where the runtime has it", () => {
  const seen: Promise<unknown>[] = [];
  const runtime = { waitUntil(p: Promise<unknown>) { seen.push(p); } };
  const wait = waitUntilOf(runtime);
  assert.ok(wait);
  wait!(Promise.resolve(1));
  assert.equal(seen.length, 1);
  assert.equal(waitUntilOf(undefined), null);
  assert.equal(waitUntilOf({}), null);
});
