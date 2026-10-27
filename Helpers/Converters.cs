using System.Globalization;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>True when the bound string is non-empty; used to hide optional fields like a trip's Description.</summary>
    public class StringNotEmptyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            !string.IsNullOrWhiteSpace(value as string);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Converts a 0-100 percentage (double) into the 0.0-1.0 fraction ProgressBar expects, clamped to 100%.</summary>
    public class PercentageToFractionConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var percent = value is double d ? d : 0;
            return Math.Clamp(percent / 100.0, 0.0, 1.0);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Strikes through a completed to-do item's title.</summary>
    public class CompletedToStrikethroughConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? TextDecorations.Strikethrough : TextDecorations.None;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>True when the bound object is non-null; used to show/hide a weather panel once data has loaded.</summary>
    public class ObjectNotNullConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not null;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Renders a Place's IsVisited flag as friendly, localized text on the Map legend.</summary>
    public class VisitedTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? AppResources.Place_Visited : AppResources.Place_NotVisited;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Formats a decimal in the user's default currency (MoneyFormatter). Replaces StringFormat='{0:C}',
    /// which used the device region's currency. ConverterParameter "0" drops the decimals.
    /// </summary>
    public class CurrencyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var amount = value switch
            {
                decimal d => d,
                double dbl => (decimal)dbl,
                int i => i,
                long l => l,
                _ => 0m
            };

            var decimals = parameter is string p && int.TryParse(p, out var digits) ? digits : 2;
            return MoneyFormatter.Format(amount, decimals);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Maps an ExpenseCategory (or the ExpenseListViewModel "All" filter sentinel) to an icon + localized
    /// label, e.g. Transportation -> "🚗 Ulaşım". Accepts either the enum or its string name so it works for
    /// both the enum-typed Category Picker on the form and the string-typed filter Picker on the list.
    /// </summary>
    public class ExpenseCategoryDisplayConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ToDisplayText(value?.ToString() ?? string.Empty);

        public static string ToDisplayText(string key) =>
            AppResources.ResourceManager.GetString($"Category_{key}", AppResources.Culture) ?? key;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Maps a trip's TripStatus to its status pill's background/foreground colour, matching the Frame Design
    /// System's tag tones, in both light and dark theme. Pass ConverterParameter "Foreground" for the text
    /// colour, anything else (or omit it) for the background.
    /// </summary>
    public class TripTagColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var status = value is TripStatus s ? s : (TripStatus?)null;
            var wantForeground = string.Equals(parameter as string, "Foreground", StringComparison.OrdinalIgnoreCase);
            var dark = Application.Current?.RequestedTheme == AppTheme.Dark;

            // TP-97 (prototype): upcoming = info teal, ongoing = success green, completed = neutral grey - the
            // tp-info / tp-success / tp-surface-alt (+ their text colours) tokens of Resources/Themes.
            var hex = (status, wantForeground, dark) switch
            {
                (TripStatus.Ongoing, true, false) => "#1B7543",
                (TripStatus.Ongoing, false, false) => "#E7F4EC",
                (TripStatus.Ongoing, true, true) => "#4CC27E",
                (TripStatus.Ongoing, false, true) => "#13281C",
                (TripStatus.Upcoming, true, false) => "#0E7490",
                (TripStatus.Upcoming, false, false) => "#E3F3F6",
                (TripStatus.Upcoming, true, true) => "#22B8CF",
                (TripStatus.Upcoming, false, true) => "#0F2A30",
                (_, true, false) => "#4A505C",
                (_, false, false) => "#EEF0F3",
                (_, true, true) => "#B3B9C4",
                _ => "#1C2026",
            };

            return Color.FromArgb(hex);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Maps the weather service's condition emoji onto a Lucide icon-font glyph, so weather is drawn with the
    /// same line-icon set as the rest of the UI (the service keeps its emoji for share text).
    /// </summary>
    public class WeatherGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ToGlyph(value as string);

        public static string ToGlyph(string? emoji) => (emoji ?? string.Empty).Replace("\ufe0f", string.Empty) switch
        {
            "\u2600" => "\ue17c",                         // clear
            "\U0001F324" or "\u26c5" => "\ue216",     // mainly clear / partly cloudy
            "\u2601" => "\ue08c",                       // overcast
            "\U0001F32B" => "\ue214",                // fog
            "\U0001F326" => "\ue08e",            // drizzle
            "\U0001F327" => "\ue092",               // rain
            "\u2744" or "\U0001F328" => "\ue094",   // snow
            "\u26c8" => "\ue090",              // thunderstorm
            _ => "\ue186",
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Maps an ExpenseCategory (enum or name) to its Lucide icon-font glyph.</summary>
    public class CategoryGlyphConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            ToGlyph(value?.ToString());

        public static string ToGlyph(string? category) => category switch
        {
            // TP-104: the prototype's set - bus, bed-double, utensils, music, shopping-bag, ellipsis
            nameof(ExpenseCategory.Transportation) => "\ue1d4",
            nameof(ExpenseCategory.Accommodation) => "\ue912",
            nameof(ExpenseCategory.Food) => "\ue2f6",
            nameof(ExpenseCategory.Entertainment) => "\ue91e",
            nameof(ExpenseCategory.Shopping) => "\ue15f",
            "All" => "\ue104",
            _ => "\ue927",
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// TP-104: the prototype's category colours (the same in both themes). ConverterParameter "Soft" gives the tile
    /// background (the colour at 13 % opacity), anything else the icon colour.
    /// </summary>
    public class CategoryColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var color = Color.FromArgb(ToHex(value?.ToString()));
            return string.Equals(parameter as string, "Soft", StringComparison.OrdinalIgnoreCase) ? color.WithAlpha(0.13f) : color;
        }

        public static string ToHex(string? category) => category switch
        {
            nameof(ExpenseCategory.Transportation) => "#2F6FED",
            nameof(ExpenseCategory.Accommodation) => "#0E9F8E",
            nameof(ExpenseCategory.Food) => "#E08A1E",
            nameof(ExpenseCategory.Entertainment) => "#D9467A",
            nameof(ExpenseCategory.Shopping) => "#7A5AF8",
            _ => "#8A94A6",
        };

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Negates a bool (for IsVisible="{Binding X, Converter=...}").</summary>
    public class InvertBoolConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not true;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not true;
    }

    /// <summary>
    /// TP-102: a grouped list row's outline (see <see cref="ListPositions"/>): 16 corners on top for the first row, at
    /// the bottom for the last, all round for a single row, square in between. Parameter = radius (default 16).
    /// </summary>
    public class ListPositionShapeConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var r = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 16;
            var corners = value switch
            {
                ListPosition.First => new CornerRadius(r, r, 0, 0),
                ListPosition.Middle => new CornerRadius(0),
                ListPosition.Last => new CornerRadius(0, 0, r, r),
                _ => new CornerRadius(r),
            };
            return new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = corners };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// TP-102: a grouped list row's margin: the side margin from the parameter (default 16), and -1 on top for every
    /// row after the first so its border lies on the row above (one hairline between rows).
    /// </summary>
    public class ListPositionMarginConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var side = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 16;
            var overlap = value is ListPosition.Middle or ListPosition.Last ? -1 : 0;
            return new Thickness(side, overlap, side, 0);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>TP-102: true when a row has a row above it - shows the divider of rows inside one card.</summary>
    public class ListPositionHasAboveConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is ListPosition position && ListPositions.HasRowAbove(position);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
