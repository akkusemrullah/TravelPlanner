namespace TravelPlanner.Models
{
    /// <summary>
    /// A signed-in account (G3), kept in SecureStorage so the app opens signed in - also offline.
    /// The access token is short-lived (≈1 h) and renewed with the refresh token when needed.
    /// </summary>
    public sealed class AuthSession
    {
        public string AccessToken { get; set; } = string.Empty;

        public string RefreshToken { get; set; } = string.Empty;

        public DateTime ExpiresAtUtc { get; set; }

        /// <summary>Supabase auth uid - the CreatedBy/UpdatedBy value and the per-account folder name.</summary>
        public string UserId { get; set; } = string.Empty;

        public string? Email { get; set; }

        public string? DisplayName { get; set; }

        public string? AvatarUrl { get; set; }

        /// <summary>"email" or "google".</summary>
        public string? Provider { get; set; }

        public bool IsExpired(DateTime utcNow, TimeSpan margin) => ExpiresAtUtc <= utcNow + margin;
    }

    /// <summary>What happened after "Hesap oluştur".</summary>
    public enum SignUpOutcome
    {
        /// <summary>Signed in straight away (e-mail confirmation is switched off).</summary>
        SignedIn,

        /// <summary>A code was e-mailed; the account works once it is entered.</summary>
        ConfirmationCodeSent
    }

    public enum AuthErrorKind
    {
        Unknown,
        Network,
        Cancelled,
        InvalidCredentials,
        EmailNotConfirmed,
        UserAlreadyExists,
        WeakPassword,
        InvalidEmail,
        InvalidCode,
        RateLimited,
        NotSupported,
        SessionExpired,
        /// <summary>The account was banned by the app's admin (Supabase "user_banned").</summary>
        Banned
    }

    /// <summary>A sign-in/sign-up failure the UI can explain (see AuthViewModel's messages).</summary>
    public sealed class AuthException : Exception
    {
        public AuthException(AuthErrorKind kind, string? detail = null, Exception? inner = null)
            : base(detail ?? kind.ToString(), inner)
        {
            Kind = kind;
        }

        public AuthErrorKind Kind { get; }
    }
}
