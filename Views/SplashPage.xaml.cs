using TravelPlanner.Controls;
using TravelPlanner.Helpers;

namespace TravelPlanner.Views
{
    /// <summary>
    /// Animated start screen. Plays once (~3 s) and then hands over to <see cref="App.ShowMainShell"/>.
    /// Not DI-registered: App creates it directly in CreateWindow.
    /// </summary>
    public partial class SplashPage : ContentPage
    {
        /// <summary>Whole sequence, including the final fade-out (~3 s).</summary>
        public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(3000);

        private bool _started;

        public SplashPage()
        {
            InitializeComponent();

            // The pin sits exactly where the drawn route ends (SplashRouteView's stage coordinates).
            Pin.Size = SplashRouteView.PinSize;
            Pin.Margin = new Thickness(SplashRouteView.PinLeft, SplashRouteView.PinTop, 0, 0);
            Pin.AnchorX = 0.5;
            Pin.AnchorY = LogoMark.TipRatio; // pops up from its tip, where it "lands"
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
            // 1) The globe fades in (0 - 400 ms) while the plane takes off and flies the dashed route into where
            //    the pin will be (150 - 1350 ms).
            var scene = new Animation
            {
                { 0, 0.30, new Animation(v => Scene.MapReveal = v, 0, 1, Easing.CubicOut) },
                { 0.11, 1, new Animation(v => Scene.Flight = v, 0, 1, Easing.CubicInOut) },
            };
            var flown = new TaskCompletionSource();
            scene.Commit(this, "tp-splash-scene", length: 1350, finished: (_, _) => flown.TrySetResult());
            await Task.Delay(1150);

            // 2) The pin pops up as the plane arrives, with a ring on the ground (1150 - 1750 ms).
            new Animation(v => Scene.Landing = v, 0, 1, Easing.CubicOut).Commit(this, "tp-splash-ring", length: 700);
            await Task.WhenAll(
                Pin.FadeToAsync(1, 200, Easing.CubicOut),
                Pin.ScaleToAsync(1, 450, Easing.SpringOut));
            await flown.Task;

            // 3) Wordmark, tagline and publisher (1600 - 2100 ms).
            await Task.WhenAll(
                Wordmark.FadeToAsync(1, 450, Easing.CubicOut),
                Wordmark.TranslateToAsync(0, 0, 450, Easing.CubicOut),
                Tagline.FadeToAsync(1, 500, Easing.CubicOut),
                Footer.FadeToAsync(1, 500, Easing.CubicOut));

            // 4) Hold, then fade everything out (2100 - 3000 ms).
            await Task.Delay(550);
            await Root.FadeToAsync(0, 300, Easing.CubicIn);
        }
    }
}
