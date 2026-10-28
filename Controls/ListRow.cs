using System.Windows.Input;
using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-102: the prototype's ListRow - a 40 tile (12 corners) with a 20 icon in one of the tones, a 15 medium title,
    /// an optional 13 subtitle, optional trailing content (count badge, toggle, value …) and a chevron when the row is
    /// tappable. At least 60 high, 16/12 padding. Put rows in a Card.List with a Divider.Inset between them.
    /// Tones: accent (default), success, warning, danger, info, neutral.
    /// </summary>
    [ContentProperty(nameof(Trailing))]
    public class ListRow : ContentView
    {
        public static readonly BindableProperty IconProperty = BindableProperty.Create(
            nameof(Icon), typeof(string), typeof(ListRow), null, propertyChanged: (b, _, _) => ((ListRow)b).UpdateIcon());

        public static readonly BindableProperty ToneProperty = BindableProperty.Create(
            nameof(Tone), typeof(string), typeof(ListRow), "accent", propertyChanged: (b, _, _) => ((ListRow)b).UpdateTone());

        public static readonly BindableProperty TitleProperty = BindableProperty.Create(
            nameof(Title), typeof(string), typeof(ListRow), null, propertyChanged: (b, _, n) => ((ListRow)b).UpdateTitle());

        public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
            nameof(Subtitle), typeof(string), typeof(ListRow), null, propertyChanged: (b, _, _) => ((ListRow)b).UpdateSubtitle());

        public static readonly BindableProperty CommandProperty = BindableProperty.Create(
            nameof(Command), typeof(ICommand), typeof(ListRow), null, propertyChanged: (b, _, _) => ((ListRow)b).UpdateChevron());

        public static readonly BindableProperty CommandParameterProperty = BindableProperty.Create(
            nameof(CommandParameter), typeof(object), typeof(ListRow));

        public static readonly BindableProperty ShowChevronProperty = BindableProperty.Create(
            nameof(ShowChevron), typeof(bool), typeof(ListRow), true, propertyChanged: (b, _, _) => ((ListRow)b).UpdateChevron());

        public static readonly BindableProperty IsDangerProperty = BindableProperty.Create(
            nameof(IsDanger), typeof(bool), typeof(ListRow), false, propertyChanged: (b, _, _) => ((ListRow)b).UpdateTone());

        public static readonly BindableProperty TrailingProperty = BindableProperty.Create(
            nameof(Trailing), typeof(View), typeof(ListRow), null, propertyChanged: (b, _, n) => ((ListRow)b)._trailingHost.Content = n as View);

        private readonly Border _tile;
        private readonly Label _icon;
        private readonly Label _title;
        private readonly Label _subtitle;
        private readonly Label _chevron;
        private readonly ContentView _trailingHost;

        public ListRow()
        {
            _icon = new Label
            {
                FontFamily = "Lucide",
                FontSize = 20,
                HorizontalOptions = LayoutOptions.Fill, // TP-106: fill the tile + centred text, exact on Android
                VerticalOptions = LayoutOptions.Fill,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
            _tile = new Border
            {
                WidthRequest = 40,
                HeightRequest = 40,
                StrokeThickness = 0,
                Stroke = Colors.Transparent,
                StrokeShape = new RoundRectangle { CornerRadius = 12 },
                VerticalOptions = LayoutOptions.Center,
                Content = _icon,
                IsVisible = false,
            };

            _title = new Label { FontFamily = "InterMedium", FontSize = 15, LineBreakMode = LineBreakMode.WordWrap };
            _subtitle = new Label { FontFamily = "InterRegular", FontSize = 13, LineBreakMode = LineBreakMode.WordWrap, Margin = new Thickness(0, 2, 0, 0), IsVisible = false };
            _subtitle.SetDynamicResource(Label.TextColorProperty, "tp-text-3");
            var texts = new VerticalStackLayout { Spacing = 0, VerticalOptions = LayoutOptions.Center, Children = { _title, _subtitle } };

            _trailingHost = new ContentView { VerticalOptions = LayoutOptions.Center };

            _chevron = new Label
            {
                FontFamily = "Lucide",
                FontSize = 18,
                VerticalOptions = LayoutOptions.Center,
                Text = Glyph("Ic.ChevronRight"),
            };
            _chevron.SetDynamicResource(Label.TextColorProperty, "tp-text-3");

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto),
                },
                ColumnSpacing = 12,
                Padding = new Thickness(16, 12),
                MinimumHeightRequest = 60,
                BackgroundColor = Colors.Transparent,
            };
            grid.Add(_tile, 0);
            grid.Add(texts, 1);
            grid.Add(_trailingHost, 2);
            grid.Add(_chevron, 3);

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (IsEnabled && Command?.CanExecute(CommandParameter) == true)
                    Command.Execute(CommandParameter);
            };
            grid.GestureRecognizers.Add(tap);

            Content = grid;
            UpdateTone();
            UpdateTitle();
            UpdateChevron();
        }

        public string? Icon { get => (string?)GetValue(IconProperty); set => SetValue(IconProperty, value); }
        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
        public string? Subtitle { get => (string?)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
        public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
        public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }
        public bool ShowChevron { get => (bool)GetValue(ShowChevronProperty); set => SetValue(ShowChevronProperty, value); }
        public bool IsDanger { get => (bool)GetValue(IsDangerProperty); set => SetValue(IsDangerProperty, value); }
        public View? Trailing { get => (View?)GetValue(TrailingProperty); set => SetValue(TrailingProperty, value); }

        internal static string Glyph(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true ? value as string ?? string.Empty : string.Empty;

        private void UpdateIcon()
        {
            _icon.Text = Icon;
            _tile.IsVisible = !string.IsNullOrEmpty(Icon);
        }

        private void UpdateTitle()
        {
            _title.Text = Title;
            SemanticProperties.SetDescription(this, Title);
        }

        private void UpdateSubtitle()
        {
            _subtitle.Text = Subtitle;
            _subtitle.IsVisible = !string.IsNullOrEmpty(Subtitle);
        }

        private void UpdateChevron() => _chevron.IsVisible = ShowChevron && Command is not null;

        private void UpdateTone()
        {
            var (background, foreground) = (IsDanger ? "danger" : Tone?.ToLowerInvariant()) switch
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
            _title.SetDynamicResource(Label.TextColorProperty, IsDanger ? "tp-danger" : "tp-text");
        }
    }
}
