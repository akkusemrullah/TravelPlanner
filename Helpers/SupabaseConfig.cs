namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Cloud backend (G3). The publishable key is meant to be shipped inside the app: every table is protected by
    /// Row Level Security and all writes go through checked RPCs (see supabase/README.md). The secret /
    /// service_role key must never appear here.
    /// </summary>
    public static class SupabaseConfig
    {
        public const string Url = "https://bobliztrprgqhnudnhrs.supabase.co";

        public const string PublishableKey = "sb_publishable_yiAjU7bdS3ObKRASN2bLcA_7LRNLbYh";

        /// <summary>Custom URL scheme the browser hands the Google sign-in result back to (Android intent filter, iOS URL type).</summary>
        public const string CallbackScheme = "com.eaksoft.travelplanner";

        /// <summary>Must also be in Supabase → Authentication → URL Configuration → Redirect URLs.</summary>
        public const string AuthCallbackUrl = CallbackScheme + "://auth-callback";

        /// <summary>
        /// Cloudflare Turnstile site key (public, meant to be shipped - the secret key goes only into Supabase →
        /// Authentication → Attack Protection). Empty = no CAPTCHA: the app works as before, so it can ship before
        /// CAPTCHA protection is switched on in Supabase. See supabase/README.md → "CAPTCHA".
        /// </summary>
        public const string CaptchaSiteKey = "0x4AAAAAAFZRqKQq7G-pl-tD";

        /// <summary>
        /// The Turnstile widget runs in a WebView as if on this page (nothing is loaded from there, the HTML is in
        /// the app). Its host - or a parent domain - must be in the Turnstile widget's "Hostnames" list.
        /// </summary>
        public const string CaptchaPageUrl = "https://travelplanner.emrullahakkus.com.tr/captcha";

        public static string AuthBaseUrl => Url + "/auth/v1/";

        public static string RestBaseUrl => Url + "/rest/v1/";
    }
}
