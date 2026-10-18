using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Models;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages.Explore;

public sealed class IndexModel(SupabaseAdminClient supabase) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public List<JsonNode> Trips { get; private set; } = [];
    public string? Error { get; private set; }
    public SupabaseAdminClient Supabase => supabase;

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Trips = J.Items(await supabase.RpcAsync("admin_explore_list", ct: ct))
                .Where(t => string.IsNullOrEmpty(Status) || J.S(t, "status") == Status)
                .ToList();
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
    }

    public async Task<IActionResult> OnPostStatusAsync(string slug, string newStatus, CancellationToken ct)
    {
        try
        {
            await supabase.RpcAsync("admin_explore_set_status", new { p_slug = slug, p_status = newStatus }, ct);
            TempData["Message"] = $"{slug}: {J.StatusText(newStatus)}";
        }
        catch (SupabaseException e)
        {
            TempData["Message"] = $"{slug}: {e.Message}";
        }
        return RedirectToPage(new { Status });
    }
}
