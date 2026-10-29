using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-112: a square-ish shortcut tile of the trip page's grid - a 36 tinted icon tile, a 14 semibold title and an
    /// optional one-line 12 caption ("6 etkinlik"), on a bordered surface card. Same tones as <see cref="ListRow"/>.
    /// Put three in a row (Grid with ColumnSpacing/RowSpacing 8); keep titles short, they get one line.
    /// </summary>
    public class FeatureTile : ContentView
    {
        public static readonly BindableProperty IconProperty = BindableProperty.Create(
            nameof(Icon), typeof(string), typeof(FeatureTile), null, propertyChanged: (b, _, n) => ((FeatureTile)b)._icon.Text = n as string);

        public static readonly BindableProperty ToneProperty = BindableProperty.Create(
            nameof(Tone), typeof(string), typeof(FeatureTile), "accent", propertyChanged: (b, _, _) => ((FeatureTile)b).UpdateTone());

        public static readonly BindableProperty TitleProperty = BindableProperty.Create(
            nameof(Title), typeof(string), typeof(FeatureTile), null, propertyChanged: (b, _, _) => ((FeatureTile)b).UpdateTitle());

        public static readonly BindableProperty CaptionProperty = BindableProperty.Create(
            nameof(Caption), typeof(string), typeof(FeatureTile), null, propertyChanged: (b, _, _) => ((FeatureTile)b).UpdateCaption());

        public static readonly BindableProperty CommandProperty = BindableProperty.Create(
            nameof(Command), typeof(ICommand), typeof(FeatureTile));

        private readonly Border _tile;
        private readonly Label _icon;
        private readonly Label _title;
        private readonly Label _caption;

        public FeatureTile()
        {
            _icon = new Label
            {
                FontFamily = "Lucide",
                FontSize = 18,
                HorizontalOptions = LayoutOptions.Fill, // TP-106: fill the tile + centred text, exact on Android
                VerticalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            _tile = new Border
            {
                WidthRequest = 36,
                HeightRequest = 36,
                StrokeThickness = 0,
                Stroke = Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 10 },
                HorizontalOptions = LayoutOptions.Start,
                Content = _icon,
            };

            _title = new Label { FontFamily = "InterSemiBold", FontSize = 14, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 10, 0, 0) };
            _title.SetDynamicResource(Label.TextColorProperty, "tp-text");
            // the caption keeps its line even when empty, so tiles in a row stay the same height
            _caption = new Label { FontFamily = "InterRegular", FontSize = 12, MaxLines = 1, LineBreakMode = LineBreakMode.TailTruncation, Margin = new Thickness(0, 2, 0, 0), Text = " " };
            _caption.SetDynamicResource(Label.TextColorProperty, "tp-text-3");

            var card = new Border
            {
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Padding = new Thickness(12),
                Content = new VerticalStackLayout { Spacing = 0, Children = { _tile, _title, _caption } },
            };
            card.SetDynamicResource(Border.BackgroundColorProperty, "tp-surface");
            card.SetDynamicResource(Border.StrokeProperty, "tp-border");

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (IsEnabled && Command?.CanExecute(null) == true)
                    Command.Execute(null);
            };
            card.GestureRecognizers.Add(tap);

            Content = card;
            UpdateTone();
        }

        public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string? Caption { get => (string?)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
        public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

        private void UpdateTitle()
        {
            _title.Text = Title;
            SemanticProperties.SetDescription(this, Title);
        }

        private void UpdateCaption() => _caption.Text = string.IsNullOrEmpty(Caption) ? " " : Caption;

        private void UpdateTone()
        {
            var (background, foreground) = Tone?.ToLowerInvariant() switch
            {
                "success" => ("tp-success-soft", "tp-success"),
                "warning" => ("tp-warning-soft", "tp-warning"),
                "danger" => ("tp-danger-soft", "tp-danger"),
                "info" => ("tp-info-soft", "tp-info"),
                "neutral" => ("tp-surface-alt", "tp-text-2"),
                _ => ("tp-accent-soft", "tp-accent-text"),
            };
            _tile.SetDynamicResource(BackgroundColorProperty, background);
            _icon.SetDynamicResource(Label.TextColorProperty, foreground);
        }
    }
}
