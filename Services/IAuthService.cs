using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Account sign-in against Supabase Auth (G3): e-mail + password (confirmed with an e-mailed code) and Google.
    /// Only talks to the auth server and keeps the session; switching the local data to the account is
    /// <see cref="IAccountService"/>'s job. Every method throws <see cref="AuthException"/> on failure.
    /// </summary>
    public interface IAuthService
    {
        /// <summary>The signed-in account, or null for a guest.</summary>
        AuthSession? CurrentSession { get; }

        bool IsSignedIn { get; }

        /// <summary>Raised after sign-in, sign-out and when a stored session turns out to be revoked.</summary>
        event EventHandler? SessionChanged;

        /// <summary>Loads the stored session (no network needed - works offline).</summary>
        Task InitializeAsync();

        Task<SignUpOutcome> SignUpAsync(string displayName, string email, string password);

        /// <summary>Confirms a new account with the code from the "Confirm signup" e-mail and signs in.</summary>
        Task VerifySignUpCodeAsync(string email, string code);

        Task ResendSignUpCodeAsync(string email);

        Task SignInWithPasswordAsync(string email, string password);

        /// <summary>Opens Google sign-in in the system browser (PKCE) and signs in with the result.</summary>
        Task SignInWithGoogleAsync();

        /// <summary>E-mails a password reset code.</summary>
        Task SendPasswordResetCodeAsync(string email);

        /// <summary>Checks the reset code, sets the new password and signs in.</summary>
        Task ResetPasswordWithCodeAsync(string email, string code, string newPassword);

        /// <summary>Forgets the session on this device (and revokes it on the server when online).</summary>
        Task SignOutAsync();

        /// <summary>
        /// A valid access token for API calls, refreshed when it is about to expire; null for a guest or when
        /// the token can't be refreshed right now (offline). Throws <see cref="AuthException"/> with
        /// <see cref="AuthErrorKind.SessionExpired"/> when the server says the session is gone.
        /// </summary>
        Task<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    }
}
