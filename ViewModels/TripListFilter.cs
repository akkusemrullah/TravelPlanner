using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TravelPlanner.Models;

namespace TravelPlanner.ViewModels
{
    /// <summary>Sort orders offered on the trip list (TP-406).</summary>
    public enum TripSortOption
    {
        DateAscending = 0,
        DateDescending = 1,
        BudgetDescending = 2,
        BudgetAscending = 3,
        NameAscending = 4
    }

    /// <summary>One status chip on the trip list: "Tümü" (null) or a specific <see cref="TripStatus"/>.</summary>
    public partial class TripStatusFilterOption : ObservableObject
    {
        public TripStatusFilterOption(TripStatus? status, string label)
        {
            Status = status;
            Label = label;
        }

        public TripStatus? Status { get; }
        public string Label { get; }

        [ObservableProperty]
        private bool isSelected;
    }

    /// <summary>
    /// Pure search / filter / sort logic for the trip list (TP-406), kept out of the ViewModel so it can be
    /// unit-tested without any UI.
    /// </summary>
    public static class TripListFilter
    {
        /// <param name="budgetInDefaultCurrency">
        /// Converts a trip's budget into one comparable currency, since trips can have different currencies.
        /// When null, raw budget amounts are compared.
        /// </param>
        public static List<Trip> Apply(
            IEnumerable<Trip> trips,
            string? searchText,
            TripStatus? status,
            TripSortOption sort,
            Func<Trip, decimal>? budgetInDefaultCurrency = null,
            CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            var query = searchText?.Trim() ?? string.Empty;

            var filtered = trips.Where(t =>
                (status is null || t.Status == status) &&
                (query.Length == 0 || Matches(t.Name, query, culture) || Matches(t.City, query, culture) || Matches(t.Country, query, culture)));

            var budget = budgetInDefaultCurrency ?? (t => t.PlannedBudget);

            return (sort switch
            {
                TripSortOption.DateDescending => filtered.OrderByDescending(t => t.StartDate).ThenBy(t => t.Name, StringComparer.Create(culture, true)),
                TripSortOption.BudgetDescending => filtered.OrderByDescending(budget).ThenBy(t => t.StartDate),
                TripSortOption.BudgetAscending => filtered.OrderBy(budget).ThenBy(t => t.StartDate),
                TripSortOption.NameAscending => filtered.OrderBy(t => t.Name, StringComparer.Create(culture, true)).ThenBy(t => t.StartDate),
                _ => filtered.OrderBy(t => t.StartDate).ThenBy(t => t.Name, StringComparer.Create(culture, true)),
            }).ToList();
        }

        /// <summary>
        /// Case- and accent-insensitive "contains", so "istanbul" / "ISTANBUL" find "İstanbul" and "munih" finds
        /// "Münih". Culture-aware collation alone isn't enough here: in Turkish, ü/ö/ç/ş/ğ/ı are letters of their
        /// own rather than accented variants, but people often type without them.
        /// </summary>
        public static bool Matches(string? source, string query, CultureInfo culture) =>
            !string.IsNullOrEmpty(source) && Fold(source, culture).Contains(Fold(query, culture), StringComparison.Ordinal);

        /// <summary>Lower-cases (culture-aware), maps Turkish letters to their Latin base and strips diacritics.</summary>
        public static string Fold(string text, CultureInfo culture)
        {
            var lower = text.Trim().ToLower(culture);
            var decomposed = lower.Normalize(System.Text.NormalizationForm.FormD);
            var builder = new System.Text.StringBuilder(decomposed.Length);

            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                    continue; // "ü" -> "u", "é" -> "e", the dot of "i̇" (from "İ" lower-cased outside Turkish) ...

                builder.Append(c switch
                {
                    'ı' => 'i',
                    'ß' => 's',
                    'ø' => 'o',
                    'æ' => 'a',
                    'ł' => 'l',
                    _ => c
                });
            }

            return builder.ToString();
        }
    }
}
