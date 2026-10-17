// TravelPlanner - Edge Function "purge-files" (G7, migration 16): deletes the files of attachments that are
// gone for good from the "trip-files" bucket.
//
//   POST /functions/v1/purge-files   (header: Authorization = Bearer <FILE_CLEANUP_SECRET>)
//   200 → { deleted, rounds }   401 unauthorized · 405 method_not_allowed · 502 cleanup_failed
//
// Called every night by pg_cron (job "travelplanner-purge-files"), which reads the same secret from the Vault
// ("file_cleanup_secret"). Deploy with Verify JWT off: the caller is not a user, the shared secret is the check.
// Talks to the database and Storage with the service role key Supabase gives every Edge Function.

import { isAuthorized, runCleanup } from "./cleanup.ts";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json; charset=utf-8" } });
}

Deno.serve(async (req: Request) => {
  if (req.method !== "POST") return json(405, { error: "method_not_allowed" });
  if (!isAuthorized(req.headers.get("Authorization"), Deno.env.get("FILE_CLEANUP_SECRET"))) {
    return json(401, { error: "unauthorized" });
  }

  try {
    const result = await runCleanup(fetch, {
      supabaseUrl: Deno.env.get("SUPABASE_URL")!,
      serviceKey: Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
    });
    console.log("purge-files", JSON.stringify(result));
    if (result.failed) return json(502, { error: "cleanup_failed", deleted: result.deleted });
    return json(200, { deleted: result.deleted, rounds: result.rounds });
  } catch (e) {
    console.error("purge-files failed", e);
    return json(502, { error: "cleanup_failed" });
  }
});
