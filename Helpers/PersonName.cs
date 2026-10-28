using System.Globalization;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// The user's "Ad Soyad" (Settings → Hesap) split into the bits the UI needs: the first name for the
    /// dashboard greeting and the avatar initials (first letter of the first and the last name).
    /// </summary>
    public static class PersonName
    {
        private static string[] Parts(string? fullName) =>
            (fullName ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>"Emrullah Can Akkuş" → "Emrullah"; empty when no name is set.</summary>
        public static string FirstName(string? fullName) => Parts(fullName).FirstOrDefault() ?? string.Empty;

        /// <summary>
        /// "Emrullah Akkuş" → "EA", "Emrullah Can Akkuş" → "EA" (first + last name), "Emrullah" → "E",
        /// empty when no name is set. Upper-cased in the current culture ("ismail" → "İ" in Turkish).
        /// </summary>
        public static string Initials(string? fullName, CultureInfo? culture = null)
        {
            var parts = Parts(fullName).Where(p => char.IsLetterOrDigit(p[0])).ToArray();
            if (parts.Length == 0)
                return string.Empty;

            culture ??= CultureInfo.CurrentCulture;
            var first = TextCase.ToUpper(parts[0][..1], culture);
            return parts.Length == 1 ? first : first + TextCase.ToUpper(parts[^1][..1], culture);
        }

        /// <summary>The prototype's avatar tones, picked by <see cref="ToneIndex"/>.</summary>
        public static readonly string[] AvatarTones = { "accent", "success", "warning", "info", "danger" };

        /// <summary>
        /// TP-104: the prototype's avatar colour - the sum of the name's character codes modulo the five tones, so
        /// a name always gets the same colour.
        /// </summary>
        public static int ToneIndex(string? name)
        {
            var text = string.IsNullOrEmpty(name) ? "?" : name;
            var sum = 0;
            foreach (var c in text)
                sum += c;
            return sum % AvatarTones.Length;
        }
    }
}
