using TravelPlanner.Models;

namespace TravelPlanner.Helpers.Charts
{
    /// <summary>
    /// Colours for the expense charts (TP-404). Mid-tone hues chosen to stay readable on both the light and
    /// the dark surface; category colours are fixed so a category looks the same everywhere.
    /// </summary>
    public static class ChartPalette
    {
        public static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

        // TP-104: the prototype's category colours (shared with the list tiles, Helpers/CategoryColorConverter) and
        // the theme tokens for bars, the budget line and the track, so the charts follow light/dark like the rest.
        public static Color ForCategory(ExpenseCategory category) => Color.FromArgb(CategoryColorConverter.ToHex(category.ToString()));

        public static Color Bar => Token("tp-accent", "#2563EB");
        public static Color BarOverBudget => Token("tp-danger", "#C42B2B");
        public static Color Text => Token("tp-text-3", "#6B7280");
        public static Color Strong => Token("tp-text", "#14161B");
        public static Color Grid => Token("tp-divider", "#ECEEF1");
        public static Color Axis => Token("tp-border-strong", "#C9CED6");
        public static Color BudgetLine => Token("tp-warning", "#A15C00");
        public static Color Track => Token("tp-surface-sunken", "#EEF0F3");

        private static Color Token(string key, string fallback) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color ? color : Color.FromArgb(fallback);
    }
}
