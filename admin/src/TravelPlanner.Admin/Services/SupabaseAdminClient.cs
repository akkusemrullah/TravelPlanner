using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace TravelPlanner.Admin.Services;

/// <summary>A Supabase call failed; <see cref="Exception.Message"/> is the server's message (shown to the admin).</summary>
public sealed class SupabaseException(string message) : Exception(message);

/// <summary>
/// Talks to Supabase with the service key: the admin_* RPCs (migration 25), the password check at sign-in and
/// Storage uploads of Keşfet images. Lives on the server only.
/// </summary>
public sealed class SupabaseAdminClient(HttpClient http, IOptions<SupabaseOptions> options)
{
    public const string ImageBucket = "explore-images";
    public const long MaxImageBytes = 1024 * 1024;

    private readonly SupabaseOptions _options = options.Value;

    /// <summary>Calls public.&lt;function&gt; and returns its JSON result (null for void / SQL null).</summary>
    public async Task<JsonNode?> RpcAsync(string function, object? args = null, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Post, $"rest/v1/rpc/{function}");
        request.Content = new StringContent(JsonSerializer.Serialize(args ?? new { }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new SupabaseException(ErrorMessage(body, response));
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    /// <summary>
    /// Checks an e-mail + password with Supabase Auth. The service key skips the sign-up CAPTCHA, which a server
    /// can't solve. Returns the account's e-mail, or null when the pair is wrong. No session is kept.
    /// </summary>
    public async Task<string?> CheckPasswordAsync(string email, string password, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Post, "auth/v1/token?grant_type=password");
        request.Content = new StringContent(JsonSerializer.Serialize(new { email, password }), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (Text(TryParse(body)?["error_code"]) == "invalid_credentials")
                return null;
            throw new SupabaseException(ErrorMessage(body, response));
        }
        return Text(TryParse(body)?["user"]?["email"]);
    }

    /// <summary>Uploads (or replaces) an image in the explore-images bucket.</summary>
    public async Task UploadImageAsync(string path, Stream content, string contentType, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Post, $"storage/v1/object/{ImageBucket}/{path}");
        request.Headers.Add("x-upsert", "true");
        request.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new SupabaseException(ErrorMessage(await response.Content.ReadAsStringAsync(ct), response));
    }

    /// <summary>Address an image path (bucket path or https URL) is shown from.</summary>
    public string? ImageUrl(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null
        : path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? path
        : $"{_options.Url.TrimEnd('/')}/storage/v1/object/public/{ImageBucket}/{path}";

    private HttpRequestMessage NewRequest(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_options.Url.TrimEnd('/') + "/"), relative));
        request.Headers.Add("apikey", _options.ServiceKey);
        // a legacy service_role key is a JWT and goes in Authorization too; a new secret key (sb_secret_...) must not
        if (_options.ServiceKey.StartsWith("eyJ", StringComparison.Ordinal))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ServiceKey);
        return request;
    }

    internal static string ErrorMessage(string body, HttpResponseMessage response)
    {
        var json = TryParse(body);
        var message = Text(json?["message"]) ?? Text(json?["msg"]) ?? Text(json?["error_description"]) ?? Text(json?["error"]);
        return string.IsNullOrWhiteSpace(message) ? $"Supabase: HTTP {(int)response.StatusCode}" : message;
    }

    private static JsonNode? TryParse(string body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
