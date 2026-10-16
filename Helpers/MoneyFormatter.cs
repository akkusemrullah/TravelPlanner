using System.Globalization;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Formats money using the current culture's number format - e.g. "₺1.234,56" in Turkish, "€1,234.56" in
    /// English. <see cref="Format"/> uses the user's default currency (Ayarlar → Varsayılan para birimi, TP-305);
    /// <see cref="FormatIn"/> formats in an explicit currency, e.g. a trip's own budget currency (TP-403).
    /// </summary>
    public static class MoneyFormatter
    {
        public const string DefaultCurrencyCode = "TRY";

        private static readonly IReadOnlyDictionary<string, string> Symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TRY"] = "₺",
            ["EUR"] = "€",
            ["USD"] = "$",
            ["GBP"] = "£",
            ["CHF"] = "CHF ",
            ["JPY"] = "¥",
            ["CAD"] = "C$",
            ["AUD"] = "A$",
            ["SEK"] = "kr ",
            ["NOK"] = "kr ",
            ["DKK"] = "kr ",
            ["PLN"] = "zł ",
            ["CZK"] = "Kč ",
            ["HUF"] = "Ft ",
            ["RON"] = "lei ",
            ["CNY"] = "CN¥",
        };

        /// <summary>
        /// Currency codes offered in Settings and on the trip/expense forms, in display order. All of them are
        /// published by the European Central Bank, so the free Frankfurter API can convert between any pair.
        /// </summary>
        public static IReadOnlyList<string> SupportedCurrencies { get; } = new[]
        {
            "TRY", "EUR", "USD", "GBP", "CHF", "JPY", "CAD", "AUD", "SEK", "NOK", "DKK", "PLN", "CZK", "HUF", "RON", "CNY"
        };

        private static string _currencyCode = DefaultCurrencyCode;

        /// <summary>ISO code of the user's default currency. Set by SettingsService at start-up and on change.</summary>
        public static string CurrencyCode
        {
            get => _currencyCode;
            set => _currencyCode = IsSupported(value) ? value!.ToUpperInvariant() : DefaultCurrencyCode;
        }

        public static bool IsSupported(string? currencyCode) =>
            !string.IsNullOrWhiteSpace(currencyCode) && Symbols.ContainsKey(currencyCode);

        /// <summary>Normalizes a code: upper-case when supported, otherwise the user's default currency.</summary>
        public static string Normalize(string? currencyCode) =>
            IsSupported(currencyCode) ? currencyCode!.Trim().ToUpperInvariant() : CurrencyCode;

        public static string SymbolFor(string? currencyCode) =>
            !string.IsNullOrWhiteSpace(currencyCode) && Symbols.TryGetValue(currencyCode, out var symbol) ? symbol : (currencyCode ?? string.Empty) + " ";

        /// <summary>Picker text for a currency, e.g. "€ Euro (EUR)" (localized), falling back to "CHF (CHF)".</summary>
        public static string DisplayName(string currencyCode) =>
            AppResources.ResourceManager.GetString($"Currency_{currencyCode}", AppResources.Culture)
            ?? $"{SymbolFor(currencyCode).Trim()} ({currencyCode})";

        /// <summary>Formats in the user's default currency.</summary>
        public static string Format(decimal amount, int decimals = 2, CultureInfo? culture = null) =>
            FormatIn(amount, CurrencyCode, decimals, culture);

        /// <summary>Formats in <paramref name="currencyCode"/>; null/unknown falls back to the default currency.</summary>
        public static string FormatIn(decimal amount, string? currencyCode, int decimals = 2, CultureInfo? culture = null)
        {
            var format = (NumberFormatInfo)(culture ?? CultureInfo.CurrentCulture).NumberFormat.Clone();
            format.CurrencySymbol = SymbolFor(Normalize(currencyCode));
            format.CurrencyDecimalDigits = decimals;
            return amount.ToString("C", format).Trim();
        }
    }
}
