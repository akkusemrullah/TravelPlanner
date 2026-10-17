namespace TravelPlanner.Services
{
    /// <summary>
    /// CAPTCHA for the e-mail auth calls (sign-up, password sign-in, resend code, password reset): Supabase Auth
    /// refuses them without a valid token once CAPTCHA protection is on (Authentication → Attack Protection).
    /// Google sign-in, code verification and token refresh don't need one.
    /// </summary>
    public interface ICaptchaService
    {
        /// <summary>
        /// A fresh single-use token (valid ~5 minutes), or null when no CAPTCHA is configured in the app
        /// (the request then goes without one). Throws <see cref="Models.AuthException"/>:
        /// <see cref="Models.AuthErrorKind.Cancelled"/> when the user closes the check,
        /// <see cref="Models.AuthErrorKind.CaptchaFailed"/> when it can't be completed.
        /// </summary>
        Task<string?> GetTokenAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>No CAPTCHA (tests, and builds without a site key).</summary>
    public sealed class NoCaptcha : ICaptchaService
    {
        public static readonly NoCaptcha Instance = new();

        public Task<string?> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
