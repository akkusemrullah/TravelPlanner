using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using TravelPlanner.Admin.Services;

namespace TravelPlanner.Admin.Pages;

[EnableRateLimiting("login")]
public sealed class LoginModel(SupabaseAdminClient supabase, IOptionsMonitor<AdminOptions> admins, ILogger<LoginModel> log) : PageModel
{
    [BindProperty, Required, EmailAddress] public string Email { get; set; } = "";
    [BindProperty, Required, DataType(DataType.Password)] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Page();

        string? email = null;
        // the password is checked only for listed addresses; the answer is the same either way
        if (admins.CurrentValue.IsAdmin(Email))
        {
            try
            {
                email = await supabase.CheckPasswordAsync(Email.Trim(), Password, ct);
            }
            catch (SupabaseException e)
            {
                Error = e.Message;
                return Page();
            }
        }
        if (email is null || !admins.CurrentValue.IsAdmin(email))
        {
            log.LogWarning("Admin sign-in refused for {Email}", Email);
            Error = "E-posta veya şifre hatalı.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        log.LogInformation("Admin signed in: {Email}", email);
        return LocalRedirect(Url.IsLocalUrl(ReturnUrl) ? ReturnUrl! : "/");
    }
}
