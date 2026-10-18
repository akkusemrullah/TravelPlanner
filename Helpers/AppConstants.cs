namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Central place for configuration values used across the app.
    /// Keeping these here avoids magic strings scattered through Services/ViewModels.
    /// </summary>
    public static class AppConstants
    {
        // ---------- Database ----------
        public const string DatabaseFilename = "travelplanner.db3";

        /// <summary>
        /// Full path to the SQLite database file inside the app's sandboxed data directory.
        /// Same call works on Android, iOS, and Windows because FileSystem.AppDataDirectory
        /// resolves to the correct platform-specific location.
        /// </summary>
        public static string DatabasePath =>
            Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename);

        // ---------- Weather API (free, no API key) ----------
        /// <summary>
        /// Open-Meteo's free weather forecast endpoint. No API key/billing account needed,
        /// unlike OpenWeatherMap which requires a registered key even on its free tier.
        /// Docs: https://open-meteo.com/en/docs
        /// </summary>
        public const string OpenMeteoForecastBaseUrl = "https://api.open-meteo.com/v1/forecast";

        /// <summary>
        /// Open-Meteo's free historical weather endpoint (ERA5 reanalysis), same provider and terms as the
        /// forecast API above. Used for trip days beyond the 16-day forecast range: the same calendar days of
        /// the previous <see cref="ClimateAverageYears"/> years are averaged (TP-402).
        /// Docs: https://open-meteo.com/en/docs/historical-weather-api
        /// </summary>
        public const string OpenMeteoArchiveBaseUrl = "https://archive-api.open-meteo.com/v1/archive";

        /// <summary>How many days ahead Open-Meteo's daily forecast reaches (today + 15).</summary>
        public const int ForecastHorizonDays = 16;

        /// <summary>How many past days the forecast endpoint still serves (older trip days use the archive).</summary>
        public const int ForecastPastDays = 30;

        /// <summary>Number of past years averaged for trip days beyond the forecast range.</summary>
        public const int ClimateAverageYears = 3;

        // ---------- Exchange rates (free, no API key) ----------
        /// <summary>
        /// Frankfurter - a free, open-source, keyless API for the European Central Bank's daily reference
        /// rates (TRY, EUR, USD, GBP and ~25 more). No quotas, commercial use allowed. Used to convert an
        /// expense entered in another currency into the trip's budget currency (TP-403).
        /// Docs: https://frankfurter.dev
        /// </summary>
        public const string FrankfurterBaseUrl = "https://api.frankfurter.dev/v1";

        // ---------- Geocoding (free, no API key) ----------
        /// <summary>
        /// Open-Meteo's free geocoding endpoint (same provider as the weather API above).
        /// No API key/billing account needed, unlike Microsoft.Maui.Controls.Maps which requires
        /// a paid Google Maps key on Android. Switched from OpenStreetMap's Nominatim because
        /// Nominatim's strict usage policy (1 req/sec, blocks heavy/shared-IP traffic - which
        /// includes corporate NATs and emulator hosts) made it unreliable in practice; Open-Meteo
        /// doesn't have that restriction and needing only one external provider is simpler.
        /// Docs: https://open-meteo.com/en/docs/geocoding-api
        /// </summary>
        public const string OpenMeteoGeocodingBaseUrl = "https://geocoding-api.open-meteo.com/v1/search";

        /// <summary>
        /// Photon (by komoot) - a free, keyless search over OpenStreetMap data, used for PlaceFormPage's
        /// "Yer Adı" autocomplete. Unlike Open-Meteo's geocoder above (a populated-places gazetteer -
        /// cities, towns, districts), Photon indexes OSM's full points-of-interest data, so it also
        /// matches monuments, museums, historic sites and other named landmarks (e.g. "Anıtkabir").
        /// It's built specifically for search-as-you-type use, so - unlike raw OpenStreetMap Nominatim,
        /// which this app avoids elsewhere for reliability reasons - it's a reasonable fit here.
        /// Docs: https://photon.komoot.io
        /// </summary>
        public const string PhotonSearchBaseUrl = "https://photon.komoot.io/api/";

        /// <summary>Photon's reverse geocoder (same free service): names the point long-pressed on the map (TP-505).</summary>
        public const string PhotonReverseBaseUrl = "https://photon.komoot.io/reverse";

        /// <summary>User-Agent sent to the free web APIs above (they ask clients to identify themselves).</summary>
        public const string HttpUserAgent = "TravelPlanner/1.0";

        // ---------- Notifications ----------
        // Which reminders a trip gets, and when: Services/TripReminderPlanner.

        // ---------- Budget alerts (TP-404) ----------
        /// <summary>Budget usage (percent) at which a "getting close" notification is sent.</summary>
        public const int BudgetWarningPercent = 80;

        /// <summary>Budget usage (percent) at which a "budget exceeded" notification is sent.</summary>
        public const int BudgetExceededPercent = 100;

        // ---------- Trip length ----------
        /// <summary>
        /// Longest trip the app accepts (start and end day included). The trip form enforces it, and every
        /// day-based screen (day plan tabs, trip-day weather, daily spending chart) is built for at most this
        /// many days - so a mistyped year can't create thousands of tabs/requests, and nothing the user enters
        /// can land on a day those screens don't show. Older trips saved before the limit existed may still be
        /// longer; those screens then show the first <see cref="MaxTripDays"/> days with a notice.
        /// </summary>
        public const int MaxTripDays = 90;

        // ---------- Itinerary (TP-401) ----------
        /// <summary>Upper bound on the day tabs built for one trip - same as <see cref="MaxTripDays"/>.</summary>
        public const int MaxItineraryDays = MaxTripDays;

        // ---- Row limits (same as the server's, supabase migration 13) ----
        /// <summary>Trips an account may own; a sample trip from Keşfet isn't added past it (TP-33).</summary>
        public const int MaxTrips = 200;

        /// <summary>Places / activities per trip.</summary>
        public const int MaxItemsPerTrip = 500;

        // ---------- Contact / data controller (legal texts, help centre) ----------
        // Shown in the privacy policy, the KVKK notice, the terms of use and the help centre ({email}, {controller} …
        // in those texts - see Helpers/DocumentText). Change them here only.

        /// <summary>Where users write for support, data requests (KVKK article 11) and reports of AI content.</summary>
        public const string SupportEmail = "emrullahakkus@gmail.com";

        /// <summary>Data controller ("veri sorumlusu") under KVKK - the publisher, a natural person.</summary>
        public const string DataControllerName = "Emrullah AKKUŞ";

        public const string DataControllerAddress = "Erciş / Van, Türkiye";

        public const string DataControllerPhone = "";

        /// <summary>Publisher name shown in the app (About, terms of use).</summary>
        public const string PublisherName = "EAKSOFT";

        // ---------- Misc ----------

        // The greeting name now comes from Settings (ISettingsService.UserName, TP-305).
    }
}
