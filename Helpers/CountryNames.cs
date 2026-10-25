using TravelPlanner.Services;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// One country written in different ways - "Türkiye", "Türkiye Cumhuriyeti" (from the city search), "Turkey" -
    /// is one country on the dashboard's "Gezilen ülke" count.
    /// </summary>
    public static class CountryNames
    {
        private const string TurkiyeKey = "turkiye";

        // the official form's extra words: "Türkiye Cumhuriyeti", "Republic of Korea", "Fransa Cumhuriyeti"
        private static readonly HashSet<string> OfficialWords = new(StringComparer.Ordinal)
        {
            "cumhuriyeti", "devleti", "republic", "of", "the",
        };

        // any of these words means Türkiye (geocoders sometimes answer in another language)
        private static readonly HashSet<string> TurkiyeWords = new(StringComparer.Ordinal)
        {
            "turkiye", "turkey", "turkei", "turquie", "turquia", "turchia", "turcja", "turkije", "turkiet", "tyrkiet",
        };

        /// <summary>Comparison key: case, Turkish letters and the official form's extra words don't matter.</summary>
        public static string Key(string? country)
        {
            var words = PackingPlanMatcher.Fold(country ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Any(TurkiyeWords.Contains) && !words.Contains("kibris")) // "Kuzey Kıbrıs Türk Cumhuriyeti" is not Türkiye
                return TurkiyeKey;
            var core = words.Where(w => !OfficialWords.Contains(w)).ToArray();
            return string.Join(' ', core.Length > 0 ? core : words);
        }

        /// <summary>The name to save for a country picked from the city search: "Türkiye Cumhuriyeti" → "Türkiye".</summary>
        public static string Tidy(string? country)
        {
            var text = (country ?? string.Empty).Trim();
            return text.Length > 0 && Key(text) == TurkiyeKey ? "Türkiye" : text;
        }
    }
}
