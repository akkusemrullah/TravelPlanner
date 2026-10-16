using Android.App;
using Android.Content.PM;
using Android.OS;

namespace TravelPlanner
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Status/navigation bars in the app's surface colour instead of the splash colour.
            SystemBars.Apply(this, Microsoft.Maui.Controls.Application.Current?.RequestedTheme == Microsoft.Maui.ApplicationModel.AppTheme.Dark);
        }
    }
}
