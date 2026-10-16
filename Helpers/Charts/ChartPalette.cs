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

        // Muted, corporate series colours (graphite + coral design system). Fixed per category so a
        // category reads the same in every chart; each has a lighter variant for the dark theme.
        public static Color ForCategory(ExpenseCategory category) => Color.FromArgb(category switch
        {
            ExpenseCategory.Transportation => IsDark ? "#7EAEE0" : "#2E5A88",
            ExpenseCategory.Accommodation => IsDark ? "#F07A66" : "#DC4A33",
            ExpenseCategory.Food => IsDark ? "#4CC27E" : "#1B7543",
            ExpenseCategory.Entertainment => IsDark ? "#E6B04F" : "#B87A12",
            ExpenseCategory.Shopping => IsDark ? "#A99BE0" : "#5F4FA0",
            _ => IsDark ? "#858C99" : "#8A919E",
        });

        public static Color Bar => Color.FromArgb(IsDark ? "#B3B9C4" : "#1E2128");
        public static Color BarOverBudget => Color.FromArgb(IsDark ? "#FF6B6B" : "#C42B2B");
        public static Color Text => Color.FromArgb(IsDark ? "#B3B9C4" : "#4A505C");
        public static Color Grid => Color.FromArgb(IsDark ? "#272B33" : "#E2E5EA");
        public static Color BudgetLine => Color.FromArgb(IsDark ? "#FF8670" : "#DC4A33");
        public static Color Track => Color.FromArgb(IsDark ? "#1C2026" : "#EEF0F3");
    }
}
