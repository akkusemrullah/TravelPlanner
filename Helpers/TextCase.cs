using System.Globalization;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// First-letter capitalisation applied when user text is saved (trip names, to-dos, places, people...), in
    /// the current culture so Turkish gets it right ("istanbul" → "İstanbul", "ılgaz" → "Ilgaz"). Only the
    /// first letter is raised; the rest is left exactly as typed.
    /// </summary>
    public static class TextCase
    {
        /// <summary>
        /// "pasaport al" → "Pasaport al". Leading spaces are kept. A first word that already contains an upper-case
        /// letter ("iPhone", "eSIM") is left unchanged, as is text that doesn't start with a letter.
        /// </summary>
        public static string? CapitalizeFirst(string? text, CultureInfo? culture = null)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var start = 0;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
            if (start >= text.Length || !char.IsLetter(text[start]))
                return text;

            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
                end++;
            for (var i = start; i < end; i++)
            {
                if (char.IsUpper(text[i]))
                    return text; // already capitalised, or a mixed-case word like "iPhone"
            }

            culture ??= CultureInfo.CurrentCulture;
            return text[..start] + text.Substring(start, 1).ToUpper(culture) + text[(start + 1)..];
        }

        /// <summary>"new york" → "New York", "emrullah akkuş" → "Emrullah Akkuş" (each word via <see cref="CapitalizeFirst"/>).</summary>
        public static string? CapitalizeWords(string? text, CultureInfo? culture = null)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            var words = text.Split(' ');
            for (var i = 0; i < words.Length; i++)
                words[i] = CapitalizeFirst(words[i], culture) ?? words[i];
            return string.Join(' ', words);
        }
    }
}
