using System.Globalization;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-106: an upper-case resource string in the app language - <c>Text="{helpers:Upper Ticket_Departure}"</c> gives
    /// "GİDİŞ". Replaces TextTransform="Uppercase", which MAUI does with the invariant culture ("GIDIŞ").
    /// </summary>
    [ContentProperty(nameof(Key))]
    [AcceptEmptyServiceProvider]
    public sealed class UpperExtension : IMarkupExtension<string>
    {
        /// <summary>The AppResources key, e.g. "Ticket_Departure".</summary>
        public string Key { get; set; } = string.Empty;

        public string ProvideValue(IServiceProvider serviceProvider) =>
            TextCase.Upper(AppResources.ResourceManager.GetString(Key, AppResources.Culture) ?? Key);

        object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
    }

    /// <summary>TP-106: the bound text upper-cased in the app language (see <see cref="UpperExtension"/>).</summary>
    public class UpperTextConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            TextCase.Upper(value?.ToString());

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
