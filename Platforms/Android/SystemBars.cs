using Android.App;
using AndroidX.Core.View;

namespace TravelPlanner
{
    /// <summary>
    /// Keeps the Android status and navigation bars in the app's surface colour (white / graphite in dark mode)
    /// with matching icon contrast, so the system chrome blends into the app bars instead of showing the
    /// splash colour.
    /// </summary>
    internal static class SystemBars
    {
        /// <summary>Bars in an explicit colour (the splash screen's royal blue). lightContent = white icons.</summary>
        public static void ApplyColor(Activity? activity, string hexColor, bool lightContent)
        {
            var window = activity?.Window;
            if (window is null)
                return;

            try
            {
                var color = Android.Graphics.Color.ParseColor(hexColor);
#pragma warning disable CA1422 // still honoured below Android 15; harmless no-op above
                window.SetStatusBarColor(color);
                window.SetNavigationBarColor(color);
#pragma warning restore CA1422

                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                if (controller is not null)
                {
                    controller.AppearanceLightStatusBars = !lightContent;
                    controller.AppearanceLightNavigationBars = !lightContent;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemBars] {ex}");
            }
        }

        public static void Apply(Activity? activity, bool dark)
        {
            var window = activity?.Window;
            if (window is null)
                return;

            try
            {
                var color = Android.Graphics.Color.ParseColor(dark ? "#16191E" : "#FFFFFF");
#pragma warning disable CA1422 // still honoured below Android 15; harmless no-op above
                window.SetStatusBarColor(color);
                window.SetNavigationBarColor(color);
#pragma warning restore CA1422

                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                if (controller is not null)
                {
                    controller.AppearanceLightStatusBars = !dark;
                    controller.AppearanceLightNavigationBars = !dark;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SystemBars] {ex}");
            }
        }
    }
}
