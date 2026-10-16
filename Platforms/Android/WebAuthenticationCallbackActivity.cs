using Android.App;
using Android.Content;
using Android.Content.PM;

namespace TravelPlanner
{
    /// <summary>
    /// G3: receives the browser's redirect back to com.eaksoft.travelplanner://auth-callback after Google sign-in
    /// and hands it to MAUI's WebAuthenticator.
    /// </summary>
    [Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
    [IntentFilter(
        new[] { Intent.ActionView },
        Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
        DataScheme = Helpers.SupabaseConfig.CallbackScheme,
        DataHost = "auth-callback")]
    public class WebAuthenticationCallbackActivity : Microsoft.Maui.Authentication.WebAuthenticatorCallbackActivity
    {
    }
}
