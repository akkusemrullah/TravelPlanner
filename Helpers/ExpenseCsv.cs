using System.Globalization;
using System.Text;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// A trip's expenses as a CSV file that Excel, Numbers and Google Sheets open directly (expense export).
    /// Pure, so it is unit-tested; ExpenseListViewModel writes the result to a file and shares it.
    /// - The separator follows the culture: ";" where the decimal separator is a comma (Turkish Excel), "," otherwise.
    /// - Amounts are plain numbers in that culture (no currency symbol), so they can be summed in the sheet.
    /// - Text that a spreadsheet would run as a formula (=, +, -, @ …) is prefixed with an apostrophe.
    /// </summary>
    public static class ExpenseCsv
    {
        /// <summary>UTF-8 with a BOM: without it Excel shows "ÄŸ" instead of "ğ".</summary>
        public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        public static string SeparatorFor(CultureInfo culture) =>
            culture.NumberFormat.NumberDecimalSeparator == "," ? ";" : ",";

        /// <param name="expenses">The rows, in the order they should appear.</param>
        /// <param name="tripCurrency">Currency of the stored amounts (the trip's budget currency).</param>
        /// <param name="payerNames">Participant id → name, for the "paid by" column (TP-504); may be empty.</param>
        public static string Build(IEnumerable<Expense> expenses, string tripCurrency, IReadOnlyDictionary<int, string> payerNames,
                                   CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            var separator = SeparatorFor(culture);
            var text = new StringBuilder();

            var columns = AppResources.ExpenseCsv_Columns.Split('|');
            text.Append(string.Join(separator, columns.Select(c => Field(c, separator)))).Append("\r\n");

            foreach (var expense in expenses)
            {
                var originalCurrency = string.IsNullOrWhiteSpace(expense.OriginalCurrencyCode)
                    ? tripCurrency
                    : expense.OriginalCurrencyCode!.ToUpperInvariant();
                var originalAmount = string.IsNullOrWhiteSpace(expense.OriginalCurrencyCode) ? expense.Amount : expense.OriginalAmount;
                var payer = expense.PaidByParticipantId is int id && payerNames.TryGetValue(id, out var name) ? name : string.Empty;

                var fields = new[]
                {
                    expense.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ExpenseCategoryDisplayConverter.ToDisplayText(expense.Category.ToString()),
                    expense.Description ?? string.Empty,
                    expense.Amount.ToString("0.00", culture),
                    tripCurrency,
                    originalAmount.ToString("0.00", culture),
                    originalCurrency,
                    expense.ExchangeRate.ToString("0.######", culture),
                    payer,
                };

                text.Append(string.Join(separator, fields.Select(f => Field(f, separator)))).Append("\r\n");
            }

            return text.ToString();
        }

        /// <summary>One CSV field: neutralised if it looks like a formula, quoted when it contains the separator, a quote or a line break.</summary>
        internal static string Field(string? value, string separator)
        {
            var text = value ?? string.Empty;

            // A value starting with = + - @ (or a tab / CR) would be run as a formula by spreadsheet apps. Numbers
            // are never negative here, and dates start with a digit, so only user text is affected.
            if (text.Length > 0 && "=+-@\t\r".Contains(text[0]))
                text = "'" + text;

            var needsQuotes = text.Contains(separator, StringComparison.Ordinal) || text.Contains('"') ||
                              text.Contains('\n') || text.Contains('\r');
            return needsQuotes ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }

        /// <summary>"Roma Tatili" → "Roma Tatili - harcamalar.csv", with characters a file name can't hold removed.</summary>
        public static string FileName(string? tripName)
        {
            var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
            var clean = new string((tripName ?? string.Empty).Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
            if (clean.Length > 60)
                clean = clean[..60].TrimEnd();
            if (clean.Length == 0)
                clean = "TravelPlanner";
            return $"{clean} - {AppResources.ExpenseCsv_FileSuffix}.csv";
        }
    }
}
