using TravelPlanner.Helpers;

namespace TravelPlanner.Views
{
    /// <summary>
    /// Animated start screen. Plays once (~4 s) and then hands over to <see cref="App.ShowMainShell"/>.
    /// Not DI-registered: App creates it directly in CreateWindow.
    /// </summary>
    public partial class SplashPage : ContentPage
    {
        /// <summary>Whole sequence, including the final fade-out (~4 s).</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(4000);

        private bool _started;

        public SplashPage()
        {
            InitializeComponent();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
#if ANDROID
            // Royal-blue status/navigation bars while the splash is up (MainActivity sets the surface colour).
            SystemBars.ApplyColor(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, "#1E3A8A", lightContent: true);
#endif
            if (_started)
                return;
            _started = true;

            RunAsync().FireAndForget();
        }

        private async Task RunAsync()
        {
            var started = DateTime.UtcNow;
            try
            {
                await AnimateAsync();
            }
            catch (Exception ex)
            {
                // An animation hiccup must never keep the user on the splash.
                System.Diagnostics.Debug.WriteLine($"[SplashPage] {ex}");
            }

            // Animations finish instantly when the OS has them turned off - still keep the screen up for the full time.
            var remaining = Duration - (DateTime.UtcNow - started);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining);

            (Application.Current as App)?.ShowMainShell(Window);
        }

        private async Task AnimateAsync()
        {
            // 1) The logo drops in (0 - 700 ms); it starts hopping by itself - the same loading animation as
            //    everywhere else in the app - and keeps hopping while the splash is up.
            await Task.WhenAll(
                Logo.FadeToAsync(1, 300, Easing.CubicOut),
                Logo.TranslateToAsync(0, 0, 700, Easing.CubicOut));

            // 2) Wordmark slides in (700 - 1300 ms).
            await Task.WhenAll(
                Wordmark.FadeToAsync(1, 600, Easing.CubicOut),
                Wordmark.TranslateToAsync(0, 0, 600, Easing.CubicOut));

            // 3) Tagline + publisher (1300 - 1800 ms).
            await Task.WhenAll(
                Tagline.FadeToAsync(1, 500, Easing.CubicOut),
                Footer.FadeToAsync(1, 500, Easing.CubicOut));

            // 4) Hold while the logo hops, then fade everything out (1800 - 4000 ms).
            await Task.Delay(1900);
            await Root.FadeToAsync(0, 300, Easing.CubicIn);
        }
    }
}
