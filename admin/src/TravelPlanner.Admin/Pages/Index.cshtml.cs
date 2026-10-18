using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages;

public sealed class IndexModel(SupabaseAdminClient supabase) : PageModel
{
    public JsonNode? Overview { get; private set; }
    public string? Error { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try
        {
            Overview = await supabase.RpcAsync("admin_overview", ct: ct);
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
    }
}
