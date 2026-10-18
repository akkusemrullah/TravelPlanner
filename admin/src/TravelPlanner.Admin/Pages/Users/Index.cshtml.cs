using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Models;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages.Users;

public sealed class IndexModel(SupabaseAdminClient supabase) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }

    public List<JsonNode> Users { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            var query = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim()[..Math.Min(Q.Trim().Length, 100)];
            Users = J.Items(await supabase.RpcAsync("admin_users", new { p_query = query, p_limit = 50 }, ct)).ToList();
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
    }
}
