using System.Globalization;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-106: Turkish case endings on a place name, for the Home hero ("Roma'ya 12 gün kaldı", "Paris'te 3. gün"). Vowel
    /// harmony follows the last vowel; the ending is joined with an apostrophe, as with every proper noun. English, or
    /// a name without a vowel ("NYC"), gets the name unchanged.
    /// </summary>
    public static class PlaceSuffix
    {
        private const string BackVowels = "aıouâû";
        private const string FrontVowels = "eiöüîéè";
        private const string HardConsonants = "fstkçşhp"; // "fıstıkçı şahap": -ta / -te after these

        /// <summary>"Roma" → "Roma'ya", "Paris" → "Paris'e", "İstanbul" → "İstanbul'a".</summary>
        public static string Dative(string place, CultureInfo? culture = null) =>
            Apply(place, culture, (last, back, endsInVowel) => (endsInVowel ? "y" : string.Empty) + (back ? "a" : "e"));

        /// <summary>"Roma" → "Roma'da", "Paris" → "Paris'te", "Berlin" → "Berlin'de".</summary>
        public static string Locative(string place, CultureInfo? culture = null) =>
            Apply(place, culture, (last, back, _) => (HardConsonants.Contains(last) ? "t" : "d") + (back ? "a" : "e"));

        private static string Apply(string place, CultureInfo? culture, Func<char, bool, bool, string> ending)
        {
            var name = (place ?? string.Empty).Trim();
            culture ??= TextCase.UiCulture;
            if (name.Length == 0 || culture.TwoLetterISOLanguageName != "tr")
                return name;

            var lower = name.ToLower(culture);
            var vowel = lower.LastOrDefault(c => BackVowels.Contains(c) || FrontVowels.Contains(c));
            var last = lower[^1];
            if (vowel == default || !char.IsLetter(last))
                return name;

            var endsInVowel = BackVowels.Contains(last) || FrontVowels.Contains(last);
            return name + "'" + ending(last, BackVowels.Contains(vowel), endsInVowel);
        }
    }
}
