using System.Globalization;
using System.Runtime.CompilerServices;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// Runs before any test: pins culture and UI culture to Turkish so assertions on localized text,
    /// month names and number formats give the same result on every machine and CI agent.
    /// </summary>
    internal static class TestCulture
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            var turkish = new CultureInfo("tr-TR");
            CultureInfo.DefaultThreadCurrentCulture = turkish;
            CultureInfo.DefaultThreadCurrentUICulture = turkish;
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;
            AppResources.Culture = turkish;
        }
    }
}
