using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages.Users;

public sealed class DetailModel(SupabaseAdminClient supabase, ILogger<DetailModel> log) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    /// <summary>Daily AI plans of this account; empty = the default limit.</summary>
    [BindProperty, Range(0, 100)] public int? DailyLimit { get; set; }

    public JsonNode? Account { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        try
        {
            Account = await supabase.RpcAsync("admin_user", new { p_user_id = Id }, ct);
            if (Account is null)
                return NotFound();
            DailyLimit = Models.J.N(Account, "ai_custom_limit") is { } l ? (int)l : null;
        }
        catch (SupabaseException e)
        {
            Error = e.Message;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostLimitAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
        {
            TempData["Message"] = "Günlük hak 0-100 arası olmalı.";
            return RedirectToPage(new { Id });
        }
        return await RunAsync("admin_set_ai_limit", new { p_user_id = Id, p_daily_limit = DailyLimit },
            DailyLimit is null ? "Varsayılan hakka dönüldü." : $"Günlük AI hakkı: {DailyLimit}", ct);
    }

    public Task<IActionResult> OnPostResetAsync(CancellationToken ct) =>
        RunAsync("admin_reset_ai_today", new { p_user_id = Id }, "Bugünkü AI kullanımı sıfırlandı.", ct);

    private async Task<IActionResult> RunAsync(string function, object args, string done, CancellationToken ct)
    {
        try
        {
            await supabase.RpcAsync(function, args, ct);
            log.LogInformation("{Admin}: {Function} for {User}", User.Identity?.Name, function, Id);
            TempData["Message"] = done;
        }
        catch (SupabaseException e)
        {
            TempData["Message"] = e.Message;
        }
        return RedirectToPage(new { Id });
    }
}
