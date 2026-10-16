using System.Globalization;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Applies the language chosen in Settings (TP-301). Turkish is the app's neutral language; English
    /// comes from AppResources.en.resx. Must run before the first page is created, because XAML reads
    /// its {x:Static} strings when a page is built - that's why a change fully applies after a restart.
    /// </summary>
    public static class LocalizationHelper
    {
        public static void Apply(string language)
        {
            CultureInfo? culture = language switch
            {
                AppLanguages.Turkish => new CultureInfo("tr-TR"),
                AppLanguages.English => new CultureInfo("en-US"),
                _ => null // follow the device
            };

            if (culture is null)
            {
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
