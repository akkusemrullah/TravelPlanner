using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Models;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages.Explore;

/// <summary>The whole plan as one explore_upsert JSON: for pasting a prepared plan or bulk edits.</summary>
public sealed class JsonModel(SupabaseAdminClient supabase) : PageModel
{
    [BindProperty] public string Json { get; set; } = "";
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? slug, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(slug))
        {
            Json = ExploreForm.Pretty(new ExploreForm { Slug = "yeni-plan", Title = "Yeni plan" }.ToJson());
            return Page();
        }
        try
        {
            if (await supabase.RpcAsync("admin_explore_get", new { p_slug = slug }, ct) is not JsonObject json)
                return NotFound();
            Json = ExploreForm.Pretty(json);
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(Json) as JsonObject;
        }
        catch (JsonException e)
        {
            Error = $"JSON okunamadı: {e.Message}";
            return Page();
        }
        if (json is null || string.IsNullOrWhiteSpace(J.S(json, "slug")))
        {
            Error = "\"slug\" alanı olan bir JSON nesnesi gerekli.";
            return Page();
        }
        try
        {
            await supabase.RpcAsync("admin_explore_upsert", new { p = json }, ct);
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
            return Page();
        }
        TempData["Message"] = "Kaydedildi.";
        return RedirectToPage("Edit", new { slug = J.S(json, "slug") });
    }
}
