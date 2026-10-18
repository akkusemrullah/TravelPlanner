namespace TravelPlanner.Admin.Services;

/// <summary>"Supabase" section. The service key is a secret: user-secrets or an environment variable only.</summary>
public sealed class SupabaseOptions
{
    public string Url { get; set; } = "";
    public string ServiceKey { get; set; } = "";
}

/// <summary>"Admin" section: the e-mail addresses that may sign in to the panel.</summary>
public sealed class AdminOptions
{
    public string[] Emails { get; set; } = [];

    public bool IsAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && Emails.Any(e => string.Equals(e.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}
