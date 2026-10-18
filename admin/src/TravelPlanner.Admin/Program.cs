using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using TravelPlanner.Admin.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<SupabaseOptions>()
    .Bind(builder.Configuration.GetSection("Supabase"))
    .Validate(o => Uri.TryCreate(o.Url, UriKind.Absolute, out var u) && u.Scheme == "https", "Supabase:Url must be an https address.")
    .Validate(o => !string.IsNullOrWhiteSpace(o.ServiceKey), "Supabase:ServiceKey is missing (dotnet user-secrets set Supabase:ServiceKey ...).")
    .ValidateOnStart();
builder.Services.AddOptions<AdminOptions>()
    .Bind(builder.Configuration.GetSection("Admin"))
    .Validate(o => o.Emails.Any(e => !string.IsNullOrWhiteSpace(e)), "Admin:Emails is empty (dotnet user-secrets set Admin:Emails:0 you@example.com).")
    .ValidateOnStart();

// Turkish letters as they are in the HTML, not as &#x...; entities
builder.Services.AddWebEncoders(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

builder.Services.AddHttpClient<SupabaseAdminClient>(c => c.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Login";
        o.LogoutPath = "/Logout";
        o.AccessDeniedPath = "/Login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.Cookie.Name = "tp_admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });

// Signed in AND still on the list: taking an address off Admin:Emails locks it out at once.
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(null)
    .AddPolicy("Admin", p => p.RequireAuthenticatedUser().RequireAssertion(ctx =>
        ctx.Resource is HttpContext http
            ? http.RequestServices.GetRequiredService<IOptionsMonitor<AdminOptions>>().CurrentValue
                .IsAdmin(ctx.User.FindFirstValue(ClaimTypes.Email))
            : ctx.User.Identity?.IsAuthenticated == true));

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/", "Admin");
    o.Conventions.AllowAnonymousToPage("/Login");
    o.Conventions.AllowAnonymousToPage("/Error");
});

// password guessing: 10 requests to the sign-in page per minute per address
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "DENY";
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' https: data:; style-src 'self'; script-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'self'";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
