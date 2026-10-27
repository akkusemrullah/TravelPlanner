using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-104: the prototype's Badge - a 24 high pill with 11.5 semibold text and an optional 12 icon, coloured by
    /// <see cref="Tone"/>: accent, success, warning, danger, info or neutral (default).
    /// </summary>
    public class Badge : ContentView
    {
        public static readonly BindableProperty TextProperty = BindableProperty.Create(
            nameof(Text), typeof(string), typeof(Badge), null, propertyChanged: (b, _, n) => ((Badge)b)._text.Text = n as string);

        public static readonly BindableProperty ToneProperty = BindableProperty.Create(
            nameof(Tone), typeof(string), typeof(Badge), "neutral", propertyChanged: (b, _, _) => ((Badge)b).UpdateTone());

        public static readonly BindableProperty IconProperty = BindableProperty.Create(
            nameof(Icon), typeof(string), typeof(Badge), null, propertyChanged: (b, _, _) => ((Badge)b).UpdateIcon());

        private readonly Border _pill;
        private readonly Label _icon;
        private readonly Label _text;

        public Badge()
        {
            _icon = new Label { FontFamily = "Lucide", FontSize = 12, VerticalOptions = LayoutOptions.Center, IsVisible = false };
            _text = new Label { FontFamily = "InterSemiBold", FontSize = 11.5, VerticalOptions = LayoutOptions.Center, VerticalTextAlignment = TextAlignment.Center };
            _pill = new Border
            {
                StrokeThickness = 0,
                Stroke = Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                Padding = new Thickness(8, 0),
                HeightRequest = 24,
                VerticalOptions = LayoutOptions.Center,
                HorizontalOptions = LayoutOptions.Start,
                Content = new HorizontalStackLayout { Spacing = 4, VerticalOptions = LayoutOptions.Center, Children = { _icon, _text } },
            };
            Content = _pill;
            HorizontalOptions = LayoutOptions.Start;
            VerticalOptions = LayoutOptions.Center;
            UpdateTone();
        }

        public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }

        private void UpdateIcon()
        {
            _icon.Text = Icon;
            _icon.IsVisible = !string.IsNullOrEmpty(Icon);
        }

        private void UpdateTone()
        {
            var (background, foreground) = Tone?.ToLowerInvariant() switch
            {
                "accent" => ("tp-accent-soft", "tp-accent-text"),
                "success" => ("tp-success-soft", "tp-success"),
                "warning" => ("tp-warning-soft", "tp-warning"),
                "danger" => ("tp-danger-soft", "tp-danger"),
                "info" => ("tp-info-soft", "tp-info"),
                _ => ("tp-surface-alt", "tp-text-2"),
            };
            _pill.SetDynamicResource(BackgroundColorProperty, background);
            _text.SetDynamicResource(Label.TextColorProperty, foreground);
            _icon.SetDynamicResource(Label.TextColorProperty, foreground);
        }
    }
}
