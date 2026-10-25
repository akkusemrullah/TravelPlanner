// TravelPlanner - plan-trip: the OpenAI call and the request-body guard (SEC-02).
// No Deno APIs (fetch is passed in), so it can be unit-tested with plain Node (see openai.test.ts).

export const OPENAI_URL = "https://api.openai.com/v1/chat/completions";

/**
 * An OpenAI call that gave no usable answer. `billed` says whether OpenAI (probably) charged for it:
 *   false - no answer at all: network error before a response, or an HTTP error status (OpenAI doesn't bill those)
 *   true  - OpenAI worked on it: HTTP 200 (cut off, empty, not JSON) or our timeout while it was still generating
 * Only an unbilled failure may give the user's plan back (public.ai_plan_refund).
 */
export class AiCallError extends Error {
  readonly billed: boolean;
  /** For the AI plan log (TP-54): unreachable | timeout | http_<status> | unreadable | cut_off | empty | not_json */
  readonly kind: string;
  constructor(message: string, billed: boolean, kind = "ai_error") {
    super(message);
    this.billed = billed;
    this.kind = kind;
  }
}

/** Tokens of every call of one plan request, added up (TP-54). */
export interface TokenTally {
  prompt: number;
  completion: number;
  reasoning: number;
}

export interface AskOptions {
  apiKey: string;
  model: string;
  messages: unknown;
  name: string;
  schema: unknown;
  maxTokens: number;
  timeoutMs: number;
  effort: string;
  /** Adds this call's token counts, when OpenAI reports them. */
  tally?: TokenTally;
}

type FetchFn = (input: string, init?: RequestInit) => Promise<Response>;

/** One structured-output call: the parsed JSON answer; throws AiCallError on any failure. */
export async function askOpenAi(fetchFn: FetchFn, o: AskOptions, log: (line: string) => void = console.log): Promise<unknown> {
  let res: Response;
  try {
    res = await fetchFn(OPENAI_URL, {
      method: "POST",
      headers: { Authorization: `Bearer ${o.apiKey}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        model: o.model,
        messages: o.messages,
        response_format: { type: "json_schema", json_schema: { name: o.name, strict: true, schema: o.schema } },
        // older chat models (gpt-4.1, gpt-4o) refuse the parameter
        ...(isReasoningModel(o.model) ? { reasoning_effort: o.effort } : {}),
        max_completion_tokens: o.maxTokens,
      }),
      signal: AbortSignal.timeout(o.timeoutMs),
    });
  } catch (e) {
    // the answer comes in one piece at the end: a timeout means OpenAI was generating it all along (billed);
    // anything else (DNS, connection refused/reset) means it never got the request
    const timedOut = e instanceof Error && (e.name === "TimeoutError" || e.name === "AbortError");
    throw new AiCallError(`OpenAI ${timedOut ? "timed out" : "unreachable"}: ${String(e)}`, timedOut, timedOut ? "timeout" : "unreachable");
  }

  if (!res.ok) {
    const detail = await res.text().catch(() => "");
    throw new AiCallError(`OpenAI ${res.status}: ${detail.slice(0, 500)}`, false, `http_${res.status}`);
  }

  // HTTP 200: from here on every failure is billed
  let body;
  try {
    body = await res.json();
  } catch (e) {
    throw new AiCallError(`OpenAI answer unreadable: ${String(e)}`, true, "unreadable");
  }
  const usage = body?.usage;
  if (o.tally) {
    const n = (v: unknown) => typeof v === "number" && Number.isFinite(v) ? v : 0;
    o.tally.prompt += n(usage?.prompt_tokens);
    o.tally.completion += n(usage?.completion_tokens);
    o.tally.reasoning += n(usage?.completion_tokens_details?.reasoning_tokens);
  }
  log(`openai usage ${o.name}: max ${o.maxTokens}, prompt ${usage?.prompt_tokens}, completion ${usage?.completion_tokens}` +
    ` (reasoning ${usage?.completion_tokens_details?.reasoning_tokens}), finish ${body?.choices?.[0]?.finish_reason}`);

  const choice = body?.choices?.[0];
  if (choice?.finish_reason === "length") throw new AiCallError(`answer cut off ${JSON.stringify(usage)}`, true, "cut_off");
  const content = choice?.message?.content;
  if (!content) throw new AiCallError("empty answer", true, "empty");
  try {
    return JSON.parse(content);
  } catch {
    throw new AiCallError("answer is not JSON", true, "not_json");
  }
}

export const DEFAULT_MODEL = "gpt-5-mini";
export const REASONING_EFFORTS = ["minimal", "low", "medium", "high"];

/** gpt-5 family and the o-series think before answering and take reasoning_effort; the others don't. */
export function isReasoningModel(model: string): boolean {
  return /^(gpt-5|o\d)/.test(model);
}

/**
 * The model and reasoning effort of a plan. The admin panel's choice (public.ai_model_settings, migration 31) comes
 * first, then the secrets AI_MODEL / AI_REASONING, then gpt-5-mini with "minimal" for nano models and "low" otherwise.
 * `source` says where the model came from, for the function log (TP-47).
 */
export function chooseModel(
  panel: { model?: unknown; reasoning?: unknown } | null,
  env: { model?: string; reasoning?: string },
): { model: string; effort: string; source: "panel" | "secret" | "default" } {
  const text = (v: unknown) => typeof v === "string" && v.trim() ? v.trim().toLowerCase() : undefined;
  const fromPanel = text(panel?.model);
  const fromEnv = text(env.model);
  const model = fromPanel ?? fromEnv ?? DEFAULT_MODEL;
  const source = fromPanel ? "panel" : fromEnv ? "secret" : "default";
  const asked = text(panel?.reasoning) ?? text(env.reasoning);
  const effort = asked && REASONING_EFFORTS.includes(asked) ? asked : model.includes("nano") ? "minimal" : "low";
  return { model, effort, source };
}

export class BodyTooLarge extends Error {}

/**
 * Reads at most `maxBytes` of a request body as JSON. Throws BodyTooLarge past the limit (checked on
 * Content-Length first, then while reading, so a lying header doesn't help), SyntaxError on bad JSON.
 */
export async function readJsonBody(req: Request, maxBytes: number): Promise<unknown> {
  const declared = Number(req.headers.get("Content-Length") ?? "0");
  if (declared > maxBytes) throw new BodyTooLarge(`body over ${maxBytes} bytes`);
  if (!req.body) return JSON.parse("");

  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > maxBytes) {
      await reader.cancel().catch(() => {});
      throw new BodyTooLarge(`body over ${maxBytes} bytes`);
    }
    chunks.push(value);
  }
  const all = new Uint8Array(size);
  let at = 0;
  for (const c of chunks) {
    all.set(c, at);
    at += c.byteLength;
  }
  return JSON.parse(new TextDecoder().decode(all));
}

/** A Supabase access token looks like a JWT: three base64url parts. Anything else is refused before the body is read. */
export function looksLikeBearerJwt(authorization: string): boolean {
  return /^Bearer [A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$/.test(authorization) && authorization.length < 8192;
}
