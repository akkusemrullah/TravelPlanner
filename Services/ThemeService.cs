using TravelPlanner.Resources.Themes;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Light/dark theming. Pages reference the tp- colour tokens with DynamicResource; switching theme writes
    /// the light or dark token values into Application.Resources, which recolours every open page in place.
    /// </summary>
    /// <remarks>
    /// Two things made the earlier version unreliable:
    /// <list type="bullet">
    /// <item>The theme was applied synchronously from wherever it was requested - the theme Picker's
    /// selection callback, or the Settings page's OnDisappearing while Shell was switching tabs. On Android
    /// <see cref="Application.UserAppTheme"/> switches AppCompat's night mode, which reconfigures the
    /// activity; doing that in the middle of a dialog dismissal or a tab fragment transaction could crash the
    /// app. Requests are now queued and applied on the next main-loop turn, outside those callbacks.</item>
    /// <item>The token dictionary was removed from and re-added to MergedDictionaries; the resolved theme
    /// was also read back from <see cref="Application.RequestedTheme"/>, which on Android can still report the
    /// previous value right after a change. The wanted theme is now derived from the option itself and the
    /// values are written to the top-level resources, the most direct way to update DynamicResource users.</item>
    /// </list>
    /// </remarks>
    public class ThemeService : IThemeService
    {
        private readonly ISettingsService _settings;
        private bool _initialized;
        private bool? _tokensDark;           // which token set is currently written to Application.Resources
        private AppThemeOption _option;      // the last requested option
        private bool _applyQueued;

        public ThemeService(ISettingsService settings)
        {
            _settings = settings;
        }

        public void Initialize()
        {
            var app = Application.Current;
            if (app is null || _initialized)
                return;

            _initialized = true;
            app.RequestedThemeChanged += OnRequestedThemeChanged;

            // Start-up: nothing is on screen yet, so apply synchronously - the first page is then built with
            // the right colours instead of flashing the light theme.
            _option = _settings.Theme;
            ApplyNow();
        }

        public void Apply(AppThemeOption option)
        {
            _option = option;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                ApplyNow();
                return;
            }

            // Coalesce rapid changes (e.g. scrolling through the picker) into one apply on the next loop turn.
            if (_applyQueued)
                return;

            _applyQueued = true;
            dispatcher.Dispatch(() =>
            {
                _applyQueued = false;
                ApplyNow();
            });
        }

        private void OnRequestedThemeChanged(object? sender, AppThemeChangedEventArgs e)
        {
            // Only matters when following the OS; an explicit Light/Dark choice is already applied.
            if (_option != AppThemeOption.System)
                return;

            Application.Current?.Dispatcher.Dispatch(() => SafeRun(() => WriteTokens(e.RequestedTheme == AppTheme.Dark)));
        }

        private void ApplyNow() => SafeRun(() =>
        {
            var app = Application.Current;
            if (app is null)
                return;

            var userTheme = _option switch
            {
                AppThemeOption.Light => AppTheme.Light,
                AppThemeOption.Dark => AppTheme.Dark,
                _ => AppTheme.Unspecified
            };

            if (app.UserAppTheme != userTheme)
                app.UserAppTheme = userTheme;

            var dark = _option switch
            {
                AppThemeOption.Light => false,
                AppThemeOption.Dark => true,
                _ => app.RequestedTheme == AppTheme.Dark   // UserAppTheme is Unspecified here, so this is the OS theme
            };

            WriteTokens(dark);
        });

        private void WriteTokens(bool dark)
        {
            var app = Application.Current;
            if (app is null || _tokensDark == dark)
                return;

            ResourceDictionary source = dark ? new DarkTheme() : new LightTheme();
            var resources = app.Resources;
            foreach (var entry in source)
                resources[entry.Key] = entry.Value;

            _tokensDark = dark;

#if ANDROID
            // Status/navigation bars follow the surface colour of the active theme.
            SystemBars.Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, dark);
#endif
        }

        private static void SafeRun(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                // A theme glitch must never take the app down.
                System.Diagnostics.Debug.WriteLine($"[ThemeService] {ex}");
            }
        }
    }
}
