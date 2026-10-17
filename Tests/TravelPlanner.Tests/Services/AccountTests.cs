using System.Net;
using Microsoft.Maui.Authentication;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>SecureStorage backed by a dictionary - the real one needs a device.</summary>
    public sealed class InMemorySecureStorage : ISecureStorage
    {
        public Dictionary<string, string> Values { get; } = new();

        public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public bool Remove(string key) => Values.Remove(key);

        public void RemoveAll() => Values.Clear();
    }

    internal static class AuthFakes
    {
        public const string UserId = "5a1f0c4e-1111-4222-8333-444455556666";

        public static string SessionJson(string accessToken = "access-1", string refreshToken = "refresh-1", long? expiresAt = null) => $$"""
            {
              "access_token": "{{accessToken}}",
              "token_type": "bearer",
              "expires_in": 3600,
              "expires_at": {{expiresAt ?? DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}},
              "refresh_token": "{{refreshToken}}",
              "user": {
                "id": "{{UserId}}",
                "email": "ali@example.com",
                "user_metadata": { "full_name": "Ali Yılmaz" },
                "app_metadata": { "provider": "email" }
              }
            }
            """;
    }

    public class SupabaseAuthServiceTests
    {
        private readonly FakeHttpMessageHandler _http = new();
        private readonly InMemorySecureStorage _storage = new();
        private readonly Mock<IWebAuthenticator> _web = new();

        private SupabaseAuthService CreateService()
        {
            var client = _http.CreateClient();
            return new SupabaseAuthService(() => client, _storage, _web.Object);
        }

        [Fact]
        public async Task SignIn_StoresTheSession_AndASecondStartRestoresItOffline()
        {
            _http.Respond("token?grant_type=password", AuthFakes.SessionJson());
            var auth = CreateService();
            var changed = 0;
            auth.SessionChanged += (_, _) => changed++;

            await auth.SignInWithPasswordAsync(" Ali@Example.com ", "secret1");

            Assert.True(auth.IsSignedIn);
            Assert.Equal(AuthFakes.UserId, auth.CurrentSession!.UserId);
            Assert.Equal("Ali Yılmaz", auth.CurrentSession.DisplayName);
            Assert.Equal(1, changed);

            var restarted = CreateService();
            await restarted.InitializeAsync();
            Assert.Equal("access-1", await restarted.GetAccessTokenAsync());
        }

        [Fact]
        public async Task SignUp_WithEmailConfirmation_AsksForTheCode()
        {
            _http.Respond("signup", """{ "id": "x", "email": "ali@example.com" }""");
            var auth = CreateService();

            Assert.Equal(SignUpOutcome.ConfirmationCodeSent, await auth.SignUpAsync("Ali", "ali@example.com", "secret1"));
            Assert.False(auth.IsSignedIn);
        }

        [Fact]
        public async Task ExpiredToken_IsRefreshed()
        {
            _http.Respond("token?grant_type=password", AuthFakes.SessionJson(expiresAt: DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds()))
                 .Respond("token?grant_type=refresh_token", AuthFakes.SessionJson("access-2", "refresh-2"));
            var auth = CreateService();
            await auth.SignInWithPasswordAsync("ali@example.com", "secret1");

            Assert.Equal("access-2", await auth.GetAccessTokenAsync());
            Assert.Equal("refresh-2", auth.CurrentSession!.RefreshToken);
        }

        [Fact]
        public async Task RevokedRefreshToken_SignsOut()
        {
            _http.Respond("token?grant_type=password", AuthFakes.SessionJson(expiresAt: DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds()))
                 .Respond("token?grant_type=refresh_token", """{ "error_code": "refresh_token_not_found", "msg": "Invalid Refresh Token" }""", HttpStatusCode.BadRequest);
            var auth = CreateService();
            await auth.SignInWithPasswordAsync("ali@example.com", "secret1");

            var ex = await Assert.ThrowsAsync<AuthException>(() => auth.GetAccessTokenAsync());

            Assert.Equal(AuthErrorKind.SessionExpired, ex.Kind);
            Assert.False(auth.IsSignedIn);
            Assert.Empty(_storage.Values);
        }

        [Fact]
        public async Task Offline_Refresh_KeepsTheSession()
        {
            var auth = new SupabaseAuthService(() => new HttpClient(new ThrowingHandler()), _storage, _web.Object);
            _storage.Values[SupabaseAuthService.StorageKey] = System.Text.Json.JsonSerializer.Serialize(new AuthSession
            {
                AccessToken = "old", RefreshToken = "r", UserId = AuthFakes.UserId, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-5)
            });
            await auth.InitializeAsync();

            Assert.Null(await auth.GetAccessTokenAsync());
            Assert.True(auth.IsSignedIn);
        }

        [Fact]
        public async Task Google_ExchangesTheReturnedCode()
        {
            _web.Setup(w => w.AuthenticateAsync(It.IsAny<WebAuthenticatorOptions>()))
                .ReturnsAsync(new WebAuthenticatorResult(new Dictionary<string, string> { ["code"] = "abc" }));
            _http.Respond("token?grant_type=pkce", AuthFakes.SessionJson());
            var auth = CreateService();

            await auth.SignInWithGoogleAsync();

            Assert.True(auth.IsSignedIn);
            _web.Verify(w => w.AuthenticateAsync(It.Is<WebAuthenticatorOptions>(o =>
                o.Url!.ToString().Contains("provider=google") && o.Url.ToString().Contains("code_challenge_method=s256"))));
        }

        [Fact]
        public async Task Google_ClosedBrowser_IsACancel()
        {
            _web.Setup(w => w.AuthenticateAsync(It.IsAny<WebAuthenticatorOptions>())).ThrowsAsync(new TaskCanceledException());
            var ex = await Assert.ThrowsAsync<AuthException>(() => CreateService().SignInWithGoogleAsync());
            Assert.Equal(AuthErrorKind.Cancelled, ex.Kind);
        }

        [Fact]
        public async Task Google_BannedAccount_SaysSo()
        {
            var result = new WebAuthenticatorResult(new Dictionary<string, string>
            {
                ["error"] = "access_denied",
                ["error_code"] = "user_banned",
                ["error_description"] = "User is banned",
            });
            _web.Setup(w => w.AuthenticateAsync(It.IsAny<WebAuthenticatorOptions>())).ReturnsAsync(result);

            var ex = await Assert.ThrowsAsync<AuthException>(() => CreateService().SignInWithGoogleAsync());

            Assert.Equal(AuthErrorKind.Banned, ex.Kind);
        }

        [Theory]
        [InlineData(400, """{"error_code":"invalid_credentials","msg":"Invalid login credentials"}""", AuthErrorKind.InvalidCredentials)]
        [InlineData(400, """{"error":"invalid_grant","error_description":"Email not confirmed"}""", AuthErrorKind.EmailNotConfirmed)]
        [InlineData(422, """{"error_code":"user_already_exists"}""", AuthErrorKind.UserAlreadyExists)]
        [InlineData(422, """{"error_code":"weak_password"}""", AuthErrorKind.WeakPassword)]
        [InlineData(403, """{"error_code":"otp_expired"}""", AuthErrorKind.InvalidCode)]
        [InlineData(429, "Too many", AuthErrorKind.RateLimited)]
        [InlineData(502, "<html>", AuthErrorKind.Network)]
        [InlineData(400, """{"error_code":"user_banned","msg":"User is banned"}""", AuthErrorKind.Banned)]
        [InlineData(400, """{"error_code":"captcha_failed","msg":"captcha protection: request disallowed (timeout-or-duplicate)"}""", AuthErrorKind.CaptchaFailed)]
        [InlineData(400, """{"msg":"captcha protection: request disallowed (invalid-input-response)"}""", AuthErrorKind.CaptchaFailed)]
        [InlineData(400, """{"error_code":"hook_payload_invalid","msg":"disposable_email: accounts cannot be created with throw-away e-mail addresses"}""", AuthErrorKind.DisposableEmail)]
        [InlineData(500, """{"msg":"disposable_email: accounts cannot be created with throw-away e-mail addresses"}""", AuthErrorKind.DisposableEmail)]
        public void ServerErrors_AreMapped(int status, string body, AuthErrorKind expected)
        {
            Assert.Equal(expected, SupabaseAuthService.MapError(status, body).Kind);
        }

        // ------------------------------------------------------------------ CAPTCHA (Cloudflare Turnstile)

        private sealed class FakeCaptcha : ICaptchaService
        {
            public int Calls { get; private set; }

            public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult<string?>($"captcha-{Calls}");
            }
        }

        private static string? CaptchaToken(string? body)
        {
            using var document = System.Text.Json.JsonDocument.Parse(body!);
            return document.RootElement.TryGetProperty("gotrue_meta_security", out var meta)
                ? meta.GetProperty("captcha_token").GetString()
                : null;
        }

        [Fact]
        public async Task Captcha_AFreshTokenGoesWithEveryProtectedEmailCall()
        {
            _http.Respond("token?grant_type=password", AuthFakes.SessionJson())
                 .Respond("signup", """{ "id": "x", "email": "ali@example.com" }""")
                 .Respond("resend", "{}")
                 .Respond("recover", "{}");
            var captcha = new FakeCaptcha();
            var client = _http.CreateClient();
            var auth = new SupabaseAuthService(() => client, _storage, _web.Object, captcha);

            await auth.SignInWithPasswordAsync("ali@example.com", "secret1");
            await auth.SignUpAsync("Ali", "ali@example.com", "secret1");
            await auth.ResendSignUpCodeAsync("ali@example.com");
            await auth.SendPasswordResetCodeAsync("ali@example.com");

            Assert.Equal(4, captcha.Calls);
            Assert.Equal(new[] { "captcha-1", "captcha-2", "captcha-3", "captcha-4" }, _http.Bodies.Select(CaptchaToken).ToArray());
        }

        [Fact]
        public async Task Captcha_NotAskedForGoogleCodesOrRefresh()
        {
            _web.Setup(w => w.AuthenticateAsync(It.IsAny<WebAuthenticatorOptions>()))
                .ReturnsAsync(new WebAuthenticatorResult(new Dictionary<string, string> { ["code"] = "abc" }));
            _http.Respond("token?grant_type=pkce", AuthFakes.SessionJson(expiresAt: DateTimeOffset.UtcNow.AddSeconds(10).ToUnixTimeSeconds()))
                 .Respond("token?grant_type=refresh_token", AuthFakes.SessionJson("access-2", "refresh-2"))
                 .Respond("verify", AuthFakes.SessionJson());
            var captcha = new FakeCaptcha();
            var client = _http.CreateClient();
            var auth = new SupabaseAuthService(() => client, _storage, _web.Object, captcha);

            await auth.SignInWithGoogleAsync();
            Assert.Equal("access-2", await auth.GetAccessTokenAsync());
            await auth.VerifySignUpCodeAsync("ali@example.com", "123456");

            Assert.Equal(0, captcha.Calls);
            Assert.All(_http.Bodies, body => Assert.Null(CaptchaToken(body)));
        }

        [Fact]
        public async Task Captcha_NotConfigured_SendsNoToken()
        {
            _http.Respond("token?grant_type=password", AuthFakes.SessionJson());

            await CreateService().SignInWithPasswordAsync("ali@example.com", "secret1");

            Assert.Null(CaptchaToken(_http.Bodies.Single()));
        }

        [Fact]
        public async Task Captcha_Cancelled_SendsNothing()
        {
            var captcha = new Mock<ICaptchaService>();
            captcha.Setup(c => c.GetTokenAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new AuthException(AuthErrorKind.Cancelled));
            var client = _http.CreateClient();
            var auth = new SupabaseAuthService(() => client, _storage, _web.Object, captcha.Object);

            var ex = await Assert.ThrowsAsync<AuthException>(() => auth.SignInWithPasswordAsync("ali@example.com", "secret1"));

            Assert.Equal(AuthErrorKind.Cancelled, ex.Kind);
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public void Captcha_Failure_HasItsOwnMessage()
        {
            Assert.Equal(TravelPlanner.Resources.Strings.AppResources.Auth_ErrorCaptcha, AuthViewModel.MessageFor(AuthErrorKind.CaptchaFailed));
        }

        [Fact]
        public void Pkce_MatchesTheRfcExample()
        {
            // RFC 7636, appendix B
            Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.CreateChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
            Assert.Equal(43, Pkce.CreateVerifier().Length);
        }

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("offline");
        }
    }

    /// <summary>Guest data, per-account data, and moving the guest's trips into an account.</summary>
    [Collection("SignedInUser")] // sets the static ChangeTracker.CurrentUserId
    public class AccountServiceTests : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-acc-" + Guid.NewGuid().ToString("N"));
        private readonly Mock<IAuthService> _auth = new();
        private readonly Mock<ISettingsService> _settings = new();
        private readonly Mock<IDialogService> _dialogs = new();
        private readonly FakeSupabaseClient _cloud = new();
        private DatabaseService _database = null!;
        private AttachmentService _attachments = null!;
        private TripService _trips = null!;
        private AccountService _account = null!;
        private AuthSession? _session;

        private string GuestDb => Path.Combine(_root, "travelplanner.db3");

        public Task InitializeAsync()
        {
            Directory.CreateDirectory(_root);
            _database = new DatabaseService(GuestDb);
            _attachments = new AttachmentService(_database, _root);
            _trips = new TripService(_database, new ExpenseService(_database), new Mock<INotificationService>().Object, _attachments);
            _auth.SetupGet(a => a.CurrentSession).Returns(() => _session);
            _auth.SetupGet(a => a.IsSignedIn).Returns(() => _session is not null);
            _auth.Setup(a => a.SignOutAsync()).Callback(() => _session = null).Returns(Task.CompletedTask);
            _settings.SetupProperty(s => s.UserName, string.Empty);
            _account = new AccountService(_auth.Object, _database, _attachments, _trips, new Mock<INotificationService>().Object,
                                          _settings.Object, _dialogs.Object, _root, GuestDb, _cloud);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            ChangeTracker.CurrentUserId = null;
            await _database.CloseAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private void AnswerMoveQuestion(bool move) =>
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(move);

        private async Task AddGuestTripAsync()
        {
            await _trips.SaveAsync(new Trip { Name = "Roma", City = "Roma", StartDate = DateTime.Today, EndDate = DateTime.Today });
            var trip = Assert.Single(await _trips.GetAllAsync());
            await _attachments.AddAsync(trip.Id, new MemoryStream(new byte[] { 1, 2 }), "bilet.pdf", "application/pdf");
        }

        private void SignIn() => _session = new AuthSession { UserId = AuthFakes.UserId, Email = "ali@example.com", DisplayName = "Ali Yılmaz" };

        [Fact]
        public async Task Guest_UsesTheOriginalDatabase()
        {
            await _account.InitializeAsync();

            Assert.Null(_account.ActiveUserId);
            Assert.Equal(GuestDb, _database.DatabasePath);
            Assert.Equal(_root, _attachments.RootDirectory);
        }

        [Fact]
        public async Task FirstSignIn_MovesTheGuestTrips_WhenTheUserAgrees()
        {
            await _account.InitializeAsync();
            await AddGuestTripAsync();
            AnswerMoveQuestion(true);
            var changed = 0;
            _account.AccountChanged += (_, _) => changed++;

            SignIn();
            await _account.ActivateSignedInAccountAsync();

            Assert.Equal(AuthFakes.UserId, _account.ActiveUserId);
            Assert.Equal(_account.UserDatabasePath(AuthFakes.UserId), _database.DatabasePath);
            Assert.Equal(AuthFakes.UserId, ChangeTracker.CurrentUserId);
            Assert.Equal(1, changed);

            var trip = Assert.Single(await _trips.GetAllAsync());
            var file = Assert.Single(await _attachments.GetByTripIdAsync(trip.Id));
            Assert.True(File.Exists(file.FullPath));
            Assert.StartsWith(_account.UserDirectory(AuthFakes.UserId), file.FullPath);
            Assert.Equal("Ali Yılmaz", _settings.Object.UserName);

            // The moved data is still queued for upload (the first sync sends it to the account).
            var db = await _database.Connection;
            Assert.True(await db.Table<PendingChange>().CountAsync() >= 2);

            // ... and the guest starts empty.
            await _account.SignOutAsync();
            Assert.Null(_account.ActiveUserId);
            Assert.Empty(await _trips.GetAllAsync());
        }

        [Fact]
        public async Task Switching_EndsTheRunningSyncRound_BeforeTheOpenDataChanges()
        {
            // TP-9: a round of the outgoing data must be over before another account's data (and name) is opened
            var sync = new Mock<ISyncService>();
            var openWhenStopped = new List<(string Database, string? User)>();
            sync.Setup(s => s.CancelCurrentRoundAsync())
                .Callback(() => openWhenStopped.Add((_database.DatabasePath, ChangeTracker.CurrentUserId)))
                .Returns(Task.CompletedTask);
            _account = new AccountService(_auth.Object, _database, _attachments, _trips, new Mock<INotificationService>().Object,
                                          _settings.Object, _dialogs.Object, _root, GuestDb, _cloud, () => sync.Object);
            await _account.InitializeAsync();

            SignIn();
            await _account.ActivateSignedInAccountAsync();
            await _account.SignOutAsync();

            Assert.Equal(new[] { (GuestDb, (string?)null), (_account.UserDatabasePath(AuthFakes.UserId), (string?)AuthFakes.UserId) },
                         openWhenStopped);
        }

        [Fact]
        public async Task FirstSignIn_KeepsTheGuestTrips_WhenTheUserDeclines()
        {
            await _account.InitializeAsync();
            await AddGuestTripAsync();
            AnswerMoveQuestion(false);

            SignIn();
            await _account.ActivateSignedInAccountAsync();
            Assert.Empty(await _trips.GetAllAsync());

            await _account.SignOutAsync();
            Assert.Single(await _trips.GetAllAsync());
        }

        [Fact]
        public async Task NextStart_OpensTheAccountsData()
        {
            await _account.InitializeAsync();
            SignIn();
            AnswerMoveQuestion(true);
            await _account.ActivateSignedInAccountAsync();
            await _trips.SaveAsync(new Trip { Name = "Paris", City = "Paris", StartDate = DateTime.Today, EndDate = DateTime.Today });

            // restart: a new service over the same folders, session restored by the auth service
            await _database.CloseAsync(); // (same instance reused - SwitchDatabaseAsync reopens the file)
            var restarted = new AccountService(_auth.Object, _database, _attachments, _trips, new Mock<INotificationService>().Object,
                                               _settings.Object, _dialogs.Object, _root, GuestDb);
            await restarted.InitializeAsync();

            Assert.Equal(AuthFakes.UserId, restarted.ActiveUserId);
            Assert.Equal("Paris", Assert.Single(await _trips.GetAllAsync()).Name);
        }

        private const string OwnedTrip = "11111111-1111-4111-8111-111111111111";
        private const string SharedTrip = "22222222-2222-4222-8222-222222222222";

        private async Task<string> SignInWithTripsAsync()
        {
            await _account.InitializeAsync();
            SignIn();
            AnswerMoveQuestion(false);
            await _account.ActivateSignedInAccountAsync();

            var db = await _database.Connection;
            await db.InsertAsync(new TripMembership { TripSyncId = OwnedTrip, Role = "owner" });
            await db.InsertAsync(new TripMembership { TripSyncId = SharedTrip, Role = "editor" });
            _cloud.Storage[$"trip-files/trips/{OwnedTrip}/a.jpg"] = new byte[] { 1 };
            _cloud.Storage[$"trip-files/trips/{OwnedTrip}/b.pdf"] = new byte[] { 2 };
            _cloud.Storage[$"trip-files/trips/{SharedTrip}/c.jpg"] = new byte[] { 3 };
            await _trips.SaveAsync(new Trip { Name = "Paris", City = "Paris", StartDate = DateTime.Today, EndDate = DateTime.Today });
            return _account.UserDirectory(AuthFakes.UserId);
        }

        [Fact]
        public async Task DeleteAccount_RemovesOwnedTripFiles_TheServerAccount_AndTheLocalCopy()
        {
            var userDirectory = await SignInWithTripsAsync();

            await _account.DeleteAccountAsync();

            Assert.Equal(new[] { $"trips/{OwnedTrip}/a.jpg", $"trips/{OwnedTrip}/b.pdf" }, _cloud.Deleted.OrderBy(p => p));
            Assert.True(_cloud.Storage.ContainsKey($"trip-files/trips/{SharedTrip}/c.jpg")); // someone else's trip keeps its files
            Assert.Contains(_cloud.Calls, c => c.Function == "delete_account");
            Assert.False(_account.IsSignedIn);
            Assert.Null(_account.ActiveUserId);           // continues as the guest
            Assert.Empty(await _trips.GetAllAsync());     // the guest's data, not the deleted account's
            Assert.False(Directory.Exists(userDirectory)); // the account's local copy is gone
        }

        [Fact]
        public async Task DeleteAccount_WithoutAConnection_DeletesNothingAndStaysSignedIn()
        {
            var userDirectory = await SignInWithTripsAsync();
            _cloud.Handler = (function, _) => function == "delete_account"
                ? throw new SyncException(SyncErrorKind.Network, "offline")
                : "[]";

            await Assert.ThrowsAsync<SyncException>(() => _account.DeleteAccountAsync());

            Assert.True(_account.IsSignedIn);
            Assert.Equal(AuthFakes.UserId, _account.ActiveUserId);
            Assert.True(Directory.Exists(userDirectory));
            Assert.Equal("Paris", Assert.Single(await _trips.GetAllAsync()).Name);
        }

        [Fact]
        public async Task DeleteAccount_StorageFails_StillDeletesTheAccount_TheServerCleansUpTheFiles()
        {
            var userDirectory = await SignInWithTripsAsync();
            _cloud.StorageError = new SyncException(SyncErrorKind.Network, "storage unreachable");

            await _account.DeleteAccountAsync();

            Assert.Empty(_cloud.Deleted);                 // left to delete_account's queue (migration 17)
            Assert.Contains(_cloud.Calls, c => c.Function == "delete_account");
            Assert.False(_account.IsSignedIn);
            Assert.Null(_account.ActiveUserId);
            Assert.False(Directory.Exists(userDirectory));
        }

        [Fact]
        public async Task DeleteAccount_Cancelled_DoesNotDeleteTheAccount()
        {
            var userDirectory = await SignInWithTripsAsync();
            _cloud.StorageError = new OperationCanceledException();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _account.DeleteAccountAsync());

            Assert.DoesNotContain(_cloud.Calls, c => c.Function == "delete_account");
            Assert.True(_account.IsSignedIn);
            Assert.True(Directory.Exists(userDirectory));
        }

        private void AccountStatusIs(string? state) =>
            _cloud.Handler = (function, _) => function == "account_status"
                ? (state is null ? throw new SyncException(SyncErrorKind.Network, "offline") : $"\"{state}\"")
                : "[]";

        [Fact]
        public async Task CheckAccount_DeletedOnTheServer_SignsOut_AndRemovesTheLocalCopy()
        {
            var userDirectory = await SignInWithTripsAsync();
            AccountStatusIs("deleted");

            var reason = await _account.CheckAccountAsync();

            Assert.Equal(AccountEndReason.Deleted, reason);
            Assert.False(_account.IsSignedIn);
            Assert.Null(_account.ActiveUserId);
            Assert.False(Directory.Exists(userDirectory));
            Assert.Equal(AuthFakes.UserId, _cloud.Calls.Single(c => c.Function == "account_status").Arguments["p_user_id"]!.GetValue<string>());
        }

        [Fact]
        public async Task CheckAccount_Banned_SignsOut_ButKeepsTheLocalCopy()
        {
            var userDirectory = await SignInWithTripsAsync();
            AccountStatusIs("banned");

            Assert.Equal(AccountEndReason.Banned, await _account.CheckAccountAsync());

            Assert.False(_account.IsSignedIn);
            Assert.Null(_account.ActiveUserId);
            Assert.True(Directory.Exists(userDirectory)); // the ban may be lifted
        }

        [Fact]
        public async Task CheckAccount_RevokedSession_SignsOut()
        {
            await SignInWithTripsAsync();
            AccountStatusIs("active");
            _auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
                 .Callback(() => _session = null)
                 .ThrowsAsync(new AuthException(AuthErrorKind.SessionExpired));

            Assert.Equal(AccountEndReason.SessionExpired, await _account.CheckAccountAsync());

            Assert.Null(_account.ActiveUserId);
        }

        [Theory]
        [InlineData("active")]
        [InlineData(null)] // offline: nothing changes, checked again next time
        public async Task CheckAccount_ActiveOrOffline_ChangesNothing(string? state)
        {
            await SignInWithTripsAsync();
            AccountStatusIs(state);
            _auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token");

            Assert.Equal(AccountEndReason.None, await _account.CheckAccountAsync());

            Assert.True(_account.IsSignedIn);
            Assert.Equal(AuthFakes.UserId, _account.ActiveUserId);
        }

        [Fact]
        public async Task TP14_CheckAccount_AsksWithTheSessionsOwnToken()
        {
            await SignInWithTripsAsync();
            AccountStatusIs("active");
            _auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token");

            await _account.CheckAccountAsync();

            Assert.Contains(_cloud.Calls, c => c.Function == "account_status");
            Assert.Equal(0, _cloud.PublicCallCount); // account_status is no longer open to anon
        }

        [Theory]
        [InlineData(AuthErrorKind.SessionExpired, AccountEndReason.SessionExpired)]
        [InlineData(AuthErrorKind.Banned, AccountEndReason.Banned)]
        public async Task TP14_CheckAccount_RefusedToken_EndsTheSession(AuthErrorKind authError, AccountEndReason expected)
        {
            await SignInWithTripsAsync();
            _cloud.Handler = (function, _) => function == "account_status"
                ? throw new SyncException(SyncErrorKind.NotSignedIn, "HTTP 401", status: 401)
                : "[]";
            _auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>()))
                 .Callback(() => _session = null)
                 .ThrowsAsync(new AuthException(authError));

            Assert.Equal(expected, await _account.CheckAccountAsync());
            Assert.Null(_account.ActiveUserId);
        }

        [Fact]
        public async Task TP14_CheckAccount_NoTokenWhileOffline_ChangesNothing()
        {
            await SignInWithTripsAsync();
            _cloud.Handler = (function, _) => function == "account_status"
                ? throw new SyncException(SyncErrorKind.NotSignedIn, "No access token (guest or offline).")
                : "[]";
            _auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

            Assert.Equal(AccountEndReason.None, await _account.CheckAccountAsync());
            Assert.Equal(AuthFakes.UserId, _account.ActiveUserId);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TP16_SignOut_DeletesTheLocalCopy_OnlyWhenAsked(bool deleteLocalData)
        {
            var userDirectory = await SignInWithTripsAsync();

            await _account.SignOutAsync(deleteLocalData);

            Assert.Null(_account.ActiveUserId);
            Assert.Equal(!deleteLocalData, Directory.Exists(userDirectory));
        }

        [Fact]
        public void UserIds_MustBeGuids()
        {
            Assert.Throws<ArgumentException>(() => _account.UserDirectory("../../etc"));
        }
    }

    public class AuthViewModelTests
    {
        private readonly Mock<IAuthService> _auth = new();
        private readonly Mock<IAccountService> _account = new();
        private int _closed;

        private AuthViewModel Create(bool online = true) =>
            new(_auth.Object, _account.Object, Fakes.Connectivity(online), Fakes.Dialogs().Object)
            {
                CloseAction = () => { _closed++; return Task.CompletedTask; }
            };

        [Fact]
        public async Task SignIn_ActivatesTheAccount_AndCloses()
        {
            var vm = Create();
            vm.Email = "ali@example.com";
            vm.Password = "secret1";

            await vm.SignInCommand.ExecuteAsync(null);

            _auth.Verify(a => a.SignInWithPasswordAsync("ali@example.com", "secret1"));
            _account.Verify(a => a.ActivateSignedInAccountAsync());
            Assert.Equal(1, _closed);
            Assert.False(vm.HasError);
        }

        [Fact]
        public async Task Offline_ExplainsWhy_AndCallsNothing()
        {
            var vm = Create(online: false);
            vm.Email = "ali@example.com";
            vm.Password = "secret1";

            await vm.SignInCommand.ExecuteAsync(null);

            Assert.True(vm.HasError);
            _auth.Verify(a => a.SignInWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task ShortPassword_AndBadEmail_AreCaughtLocally()
        {
            var vm = Create();
            vm.Email = "ali-at-example";
            vm.Password = "secret1";
            await vm.SignInCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);

            vm.Email = "ali@example.com";
            vm.Password = "123";
            await vm.SignInCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);

            _auth.Verify(a => a.SignInWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Theory]
        [InlineData("abc123", false)]     // 6 - the old minimum
        [InlineData("abcd123", false)]    // 7
        [InlineData("abcdefgh", false)]   // no digit
        [InlineData("12345678", false)]   // no letter
        [InlineData("abcd1234", true)]
        [InlineData("Şifre2026", true)]
        public async Task TP15_NewPasswords_Need8CharactersWithALetterAndADigit(string password, bool accepted)
        {
            _auth.Setup(a => a.SignUpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(SignUpOutcome.ConfirmationCodeSent);
            var vm = Create();
            vm.Mode = AuthMode.SignUp;
            vm.DisplayName = "Ali";
            vm.Email = "ali@example.com";
            vm.Password = password;

            await vm.SignUpCommand.ExecuteAsync(null);

            Assert.Equal(!accepted, vm.HasError);
            _auth.Verify(a => a.SignUpAsync("Ali", "ali@example.com", password), accepted ? Times.Once() : Times.Never());
            if (!accepted)
                Assert.Equal(string.Format(TravelPlanner.Resources.Strings.AppResources.Auth_PasswordRulesFormat, 8), vm.ErrorMessage);
        }

        [Fact]
        public async Task TP15_OlderAccounts_CanStillSignInWithA6CharacterPassword()
        {
            var vm = Create();
            vm.Email = "ali@example.com";
            vm.Password = "abc123";

            await vm.SignInCommand.ExecuteAsync(null);

            _auth.Verify(a => a.SignInWithPasswordAsync("ali@example.com", "abc123"));
            Assert.False(vm.HasError);
        }

        [Fact]
        public async Task SignUp_WithConfirmation_GoesToTheCodeStep_ThenVerifies()
        {
            _auth.Setup(a => a.SignUpAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(SignUpOutcome.ConfirmationCodeSent);
            var vm = Create();
            vm.Mode = AuthMode.SignUp;
            vm.DisplayName = "Ali";
            vm.Email = "ali@example.com";
            vm.Password = "secret12";

            await vm.SignUpCommand.ExecuteAsync(null);
            Assert.Equal(AuthMode.VerifySignUp, vm.Mode);
            Assert.Equal(0, _closed);

            vm.Code = "123 456";
            await vm.VerifyCodeCommand.ExecuteAsync(null);

            _auth.Verify(a => a.VerifySignUpCodeAsync("ali@example.com", "123 456"));
            Assert.Equal(1, _closed);
        }

        [Fact]
        public async Task UnconfirmedEmail_ResendsTheCode_AndAsksForIt()
        {
            _auth.Setup(a => a.SignInWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ThrowsAsync(new AuthException(AuthErrorKind.EmailNotConfirmed));
            var vm = Create();
            vm.Email = "ali@example.com";
            vm.Password = "secret1";

            await vm.SignInCommand.ExecuteAsync(null);

            _auth.Verify(a => a.ResendSignUpCodeAsync("ali@example.com"));
            Assert.Equal(AuthMode.VerifySignUp, vm.Mode);
            Assert.True(vm.HasInfo);
        }

        [Fact]
        public async Task WrongPassword_ShowsAMessage()
        {
            _auth.Setup(a => a.SignInWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ThrowsAsync(new AuthException(AuthErrorKind.InvalidCredentials));
            var vm = Create();
            vm.Email = "ali@example.com";
            vm.Password = "secret1";

            await vm.SignInCommand.ExecuteAsync(null);

            Assert.Equal(AuthViewModel.MessageFor(AuthErrorKind.InvalidCredentials), vm.ErrorMessage);
            Assert.Equal(0, _closed);
        }
    }
}
