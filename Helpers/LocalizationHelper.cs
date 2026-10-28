using System.Globalization;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Applies the language chosen in Settings (TP-301). Turkish is the app's neutral language; English
    /// comes from AppResources.en.resx. Must run before the first page is created, because XAML reads
    /// its {x:Static} strings when a page is built - TP-109: so after a change the App builds every page again
    /// (<see cref="ReopenPages"/>).
    /// </summary>
    public static class LocalizationHelper
    {
        // TP-109: the phone's own language, kept before the app sets one, so "Cihaz dili" can go back to it
        private static CultureInfo? s_deviceCulture;
        private static CultureInfo? s_deviceUiCulture;

        /// <summary>
        /// TP-109: set by the App - builds the pages again (in the language just applied); false when it couldn't,
        /// and then a restart does it.
        /// </summary>
        public static Func<Task<bool>>? ReopenPages { get; set; }

        public static void Apply(string language)
        {
            s_deviceCulture ??= CultureInfo.CurrentCulture;
            s_deviceUiCulture ??= CultureInfo.CurrentUICulture;

            var culture = language switch
            {
                AppLanguages.Turkish => new CultureInfo("tr-TR"),
                AppLanguages.English => new CultureInfo("en-US"),
                _ => null // follow the device
            };

            if (culture is null)
            {
                // back from a chosen language to the phone's: without this the chosen one stayed until a restart
                CultureInfo.DefaultThreadCurrentCulture = s_deviceCulture;
                CultureInfo.DefaultThreadCurrentUICulture = s_deviceUiCulture;
                CultureInfo.CurrentCulture = s_deviceCulture;
                CultureInfo.CurrentUICulture = s_deviceUiCulture;
                AppResources.Culture = null;
                return;
            }

            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            AppResources.Culture = culture;
        }

        /// <summary>Two-letter language code for web APIs that return localized names ("tr" or "en").</summary>
        public static string ApiLanguage =>
            (AppResources.Culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }
}
