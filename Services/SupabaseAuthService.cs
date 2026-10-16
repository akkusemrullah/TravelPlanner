using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Authentication;
using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// <see cref="IAuthService"/> over Supabase Auth's REST API (G3) - plain HttpClient, no extra SDK.
    /// E-mail sign-up is confirmed with the code from the "Confirm signup" e-mail and password reset uses the
    /// code from the "Reset password" e-mail (both templates contain {{ .Token }}), so neither needs a link
    /// that has to find its way back into the app. Google uses the browser + PKCE.
    /// </summary>
    public class SupabaseAuthService : IAuthService
    {
        /// <summary>Name of the HttpClient registered in MauiProgram.</summary>
        public const string HttpClientName = "supabase-auth";

        internal const string StorageKey = "tp.auth.session";

        /// <summary>Tokens are renewed this long before they actually expire.</summary>
        internal static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

        private static readonly JsonSerializerOptions StoreJson = new(JsonSerializerDefaults.General);

        private readonly Func<HttpClient> _createClient;
        private readonly ISecureStorage _storage;
        private readonly IWebAuthenticator _webAuthenticator;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);

        public SupabaseAuthService(IHttpClientFactory httpClientFactory, ISecureStorage storage, IWebAuthenticator webAuthenticator)
            : this(() => httpClientFactory.CreateClient(HttpClientName), storage, webAuthenticator)
        {
        }

        /// <param name="createClient">Tests pass a client over a fake handler. BaseAddress defaults to the auth API.</param>
        internal SupabaseAuthService(Func<HttpClient> createClient, ISecureStorage storage, IWebAuthenticator webAuthenticator)
        {
            _createClient = createClient;
            _storage = storage;
            _webAuthenticator = webAuthenticator;
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public AuthSession? CurrentSession { get; private set; }

        public bool IsSignedIn => CurrentSession is not null;

        public event EventHandler? SessionChanged;

        // ------------------------------------------------------------------ session lifecycle

        public async Task InitializeAsync()
        {
            try
            {
                var json = await _storage.GetAsync(StorageKey);
                CurrentSession = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AuthSession>(json, StoreJson);
                if (CurrentSession is { UserId.Length: 0 })
                    CurrentSession = null;
            }
            catch (Exception ex)
            {
                // Unreadable secure storage (e.g. restored from another device's backup): start as a guest.
                System.Diagnostics.Debug.WriteLine($"[Auth] Could not read the stored session: {ex}");
                CurrentSession = null;
                TryRemoveStored();
            }
        }

        public async Task SignOutAsync()
        {
            var session = CurrentSession;
            await ClearSessionAsync(raise: true);

            if (session is null)
                return;

            try
            {
                // Best effort: revoke the refresh token on the server. Offline it simply expires.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await SendAsync(HttpMethod.Post, "logout", null, session.AccessToken, cts.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] Server-side sign-out failed (ignored): {ex.Message}");
            }
        }

        public async Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            var session = CurrentSession;
            if (session is null)
                return null;
            if (!session.IsExpired(UtcNow(), RefreshMargin))
                return session.AccessToken;

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                session = CurrentSession;
                if (session is null)
                    return null;
                if (!session.IsExpired(UtcNow(), RefreshMargin))
                    return session.AccessToken; // another caller refreshed meanwhile

                JsonElement response;
                try
                {
                    response = await SendAsync(HttpMethod.Post, "token?grant_type=refresh_token",
                        new Dictionary<string, object?> { ["refresh_token"] = session.RefreshToken }, null, cancellationToken);
                }
                catch (AuthException ex) when (ex.Kind is AuthErrorKind.Network or AuthErrorKind.RateLimited)
                {
                    return null; // offline / try again later - the session itself is still fine
                }
                catch (AuthException ex)
                {
                    // The server no longer accepts the refresh token (revoked, password changed, user deleted).
                    await ClearSessionAsync(raise: true);
                    throw new AuthException(AuthErrorKind.SessionExpired, ex.Message, ex);
                }

                var renewed = ParseSession(response);
                await SaveSessionAsync(renewed, raise: false);
                return renewed.AccessToken;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        // ------------------------------------------------------------------ e-mail + password

        public async Task<SignUpOutcome> SignUpAsync(string displayName, string email, string password)
        {
            var body = new Dictionary<string, object?>
            {
                ["email"] = NormalizeEmail(email),
                ["password"] = password,
                ["data"] = new Dictionary<string, object?> { ["full_name"] = displayName.Trim() },
            };

            var response = await SendAsync(HttpMethod.Post, "signup", body);

            // With "Confirm email" on, the answer is the (unconfirmed) user without tokens.
            if (response.ValueKind == JsonValueKind.Object && response.TryGetProperty("access_token", out _))
            {
                await SaveSessionAsync(ParseSession(response), raise: true);
                return SignUpOutcome.SignedIn;
            }

            return SignUpOutcome.ConfirmationCodeSent;
        }

        public async Task VerifySignUpCodeAsync(string email, string code)
        {
            JsonElement response;
            try
            {
                response = await VerifyAsync("email", email, code);
            }
            catch (AuthException ex) when (ex.Kind == AuthErrorKind.Unknown)
            {
                // Older auth servers want the legacy type for sign-up codes.
                response = await VerifyAsync("signup", email, code);
            }

            await SaveSessionAsync(ParseSession(response), raise: true);
        }

        public Task ResendSignUpCodeAsync(string email) =>
            SendAsync(HttpMethod.Post, "resend", new Dictionary<string, object?>
            {
                ["type"] = "signup",
                ["email"] = NormalizeEmail(email),
            });

        public async Task SignInWithPasswordAsync(string email, string password)
        {
            var response = await SendAsync(HttpMethod.Post, "token?grant_type=password", new Dictionary<string, object?>
            {
                ["email"] = NormalizeEmail(email),
                ["password"] = password,
            });
            await SaveSessionAsync(ParseSession(response), raise: true);
        }

        public Task SendPasswordResetCodeAsync(string email) =>
            SendAsync(HttpMethod.Post, "recover", new Dictionary<string, object?> { ["email"] = NormalizeEmail(email) });

        public async Task ResetPasswordWithCodeAsync(string email, string code, string newPassword)
        {
            var session = ParseSession(await VerifyAsync("recovery", email, code));
            await SendAsync(HttpMethod.Put, "user", new Dictionary<string, object?> { ["password"] = newPassword }, session.AccessToken);
            await SaveSessionAsync(session, raise: true);
        }

        private Task<JsonElement> VerifyAsync(string type, string email, string code) =>
            SendAsync(HttpMethod.Post, "verify", new Dictionary<string, object?>
            {
                ["type"] = type,
                ["email"] = NormalizeEmail(email),
                ["token"] = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()),
            });

        // ------------------------------------------------------------------ Google (browser + PKCE)

        public async Task SignInWithGoogleAsync()
        {
            var verifier = Pkce.CreateVerifier();
            var authorizeUrl = new Uri(
                $"{SupabaseConfig.AuthBaseUrl}authorize?provider=google" +
                $"&redirect_to={Uri.EscapeDataString(SupabaseConfig.AuthCallbackUrl)}" +
                $"&code_challenge={Pkce.CreateChallenge(verifier)}&code_challenge_method=s256");

            WebAuthenticatorResult result;
            try
            {
                result = await _webAuthenticator.AuthenticateAsync(new WebAuthenticatorOptions
                {
                    Url = authorizeUrl,
                    CallbackUrl = new Uri(SupabaseConfig.AuthCallbackUrl),
                    PrefersEphemeralWebBrowserSession = true,
                });
            }
            catch (OperationCanceledException ex)
            {
                throw new AuthException(AuthErrorKind.Cancelled, null, ex);
            }
            catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException or FeatureNotSupportedException)
            {
                throw new AuthException(AuthErrorKind.NotSupported, ex.Message, ex);
            }

            if (result?.Properties is null || !result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                string? error = null;
                string? errorCode = null;
                result?.Properties?.TryGetValue("error_description", out error);
                result?.Properties?.TryGetValue("error_code", out errorCode);
                // e.g. a banned account: ...auth-callback?error=access_denied&error_code=user_banned
                var kind = string.Equals(errorCode, "user_banned", StringComparison.OrdinalIgnoreCase) ||
                           error?.Contains("banned", StringComparison.OrdinalIgnoreCase) == true
                    ? AuthErrorKind.Banned
                    : AuthErrorKind.Unknown;
                throw new AuthException(kind, error ?? errorCode ?? "No authorization code returned.");
            }

            var response = await SendAsync(HttpMethod.Post, "token?grant_type=pkce", new Dictionary<string, object?>
            {
                ["auth_code"] = code,
                ["code_verifier"] = verifier,
            });
            await SaveSessionAsync(ParseSession(response), raise: true);
        }

        // ------------------------------------------------------------------ plumbing

        private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string? bearer = null, CancellationToken cancellationToken = default)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Add("apikey", SupabaseConfig.PublishableKey);
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null)
                request.Content = JsonContent.Create(body);

            var client = _createClient();
            client.BaseAddress ??= new Uri(SupabaseConfig.AuthBaseUrl);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new AuthException(AuthErrorKind.Network, ex.Message, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AuthException(AuthErrorKind.Network, "Timed out.", ex); // HttpClient timeout
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw MapError((int)response.StatusCode, text);

                if (string.IsNullOrWhiteSpace(text))
                    return default;

                try
                {
                    using var document = JsonDocument.Parse(text);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    return default;
                }
            }
        }

        /// <summary>Turns a Supabase Auth error body into an <see cref="AuthException"/> the UI can explain.</summary>
        internal static AuthException MapError(int status, string? body)
        {
            string? code = null;
            string? message = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    code = GetString(root, "error_code") ?? GetString(root, "error");
                    message = GetString(root, "msg") ?? GetString(root, "message") ?? GetString(root, "error_description");
                }
            }
            catch (JsonException)
            {
                // not JSON (proxy error page …)
            }

            var kind = code switch
            {
                "invalid_credentials" or "invalid_grant" => AuthErrorKind.InvalidCredentials,
                "email_not_confirmed" => AuthErrorKind.EmailNotConfirmed,
                "user_already_exists" or "email_exists" => AuthErrorKind.UserAlreadyExists,
                "weak_password" => AuthErrorKind.WeakPassword,
                "email_address_invalid" or "email_address_not_authorized" or "validation_failed" => AuthErrorKind.InvalidEmail,
                "otp_expired" or "otp_disabled" or "bad_code_verifier" or "flow_state_expired" or "flow_state_not_found" => AuthErrorKind.InvalidCode,
                "over_email_send_rate_limit" or "over_request_rate_limit" or "over_sms_send_rate_limit" => AuthErrorKind.RateLimited,
                "user_banned" => AuthErrorKind.Banned,
                "refresh_token_not_found" or "refresh_token_already_used" or "session_not_found" or "session_expired" or "user_not_found"
                    => AuthErrorKind.SessionExpired,
                _ => status == 429 ? AuthErrorKind.RateLimited : status >= 500 ? AuthErrorKind.Network : AuthErrorKind.Unknown,
            };

            // Older servers answer "Email not confirmed" as a generic invalid_grant.
            if (kind == AuthErrorKind.InvalidCredentials && message?.Contains("not confirmed", StringComparison.OrdinalIgnoreCase) == true)
                kind = AuthErrorKind.EmailNotConfirmed;

            return new AuthException(kind, message ?? code ?? $"HTTP {status}");
        }

        internal AuthSession ParseSession(JsonElement root)
        {
            if (root.ValueKind != JsonValueKind.Object || GetString(root, "access_token") is not { Length: > 0 } accessToken)
                throw new AuthException(AuthErrorKind.Unknown, "The auth server returned no session.");

            DateTime expiresAt;
            if (root.TryGetProperty("expires_at", out var expiresAtValue) && expiresAtValue.ValueKind == JsonValueKind.Number)
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresAtValue.GetInt64()).UtcDateTime;
            else if (root.TryGetProperty("expires_in", out var expiresIn) && expiresIn.ValueKind == JsonValueKind.Number)
                expiresAt = UtcNow().AddSeconds(expiresIn.GetDouble());
            else
                expiresAt = UtcNow().AddHours(1);

            var session = new AuthSession
            {
                AccessToken = accessToken,
                RefreshToken = GetString(root, "refresh_token") ?? string.Empty,
                ExpiresAtUtc = expiresAt,
            };

            if (root.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object)
            {
                session.UserId = GetString(user, "id") ?? string.Empty;
                session.Email = GetString(user, "email");
                if (user.TryGetProperty("user_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    session.DisplayName = GetString(meta, "full_name") ?? GetString(meta, "name");
                    session.AvatarUrl = GetString(meta, "avatar_url") ?? GetString(meta, "picture");
                }
                if (user.TryGetProperty("app_metadata", out var app) && app.ValueKind == JsonValueKind.Object)
                    session.Provider = GetString(app, "provider");
            }

            if (session.UserId.Length == 0)
                throw new AuthException(AuthErrorKind.Unknown, "The auth server returned no user.");

            return session;
        }

        private async Task SaveSessionAsync(AuthSession session, bool raise)
        {
            CurrentSession = session;
            try
            {
                await _storage.SetAsync(StorageKey, JsonSerializer.Serialize(session, StoreJson));
            }
            catch (Exception ex)
            {
                // Still signed in for this run; the next start just asks again.
                System.Diagnostics.Debug.WriteLine($"[Auth] Could not store the session: {ex}");
            }

            if (raise)
                SessionChanged?.Invoke(this, EventArgs.Empty);
        }

        private Task ClearSessionAsync(bool raise)
        {
            var hadSession = CurrentSession is not null;
            CurrentSession = null;
            TryRemoveStored();
            if (raise && hadSession)
                SessionChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        private void TryRemoveStored()
        {
            try
            {
                _storage.Remove(StorageKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Auth] Could not remove the stored session: {ex}");
            }
        }

        private static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

        private static string? GetString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>OAuth PKCE helpers (RFC 7636): a random verifier and its S256 challenge.</summary>
    public static class Pkce
    {
        public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

        public static string CreateChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
