using Microsoft.Maui.Controls.Shapes;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-106: the floating bottom menu - a rounded surface bar 16 from the screen edges with Ana Sayfa, Seyahatler,
    /// Keşfet and Profil, and a round blue "+" (new trip) standing out of its middle. It sits at the bottom of each
    /// tab root page (the Shell's own tab bar is hidden there); <see cref="Current"/> is the page's Shell route.
    /// A tap selects the tab through <see cref="TabNavigation"/>.
    /// </summary>
    public class FloatingTabBar : ContentView
    {
        /// <summary>Room a tab page leaves under its content so the last row isn't hidden behind the bar.</summary>
        public const double ContentInset = 104;

        public static readonly BindableProperty CurrentProperty = BindableProperty.Create(
            nameof(Current), typeof(string), typeof(FloatingTabBar), string.Empty, propertyChanged: (b, _, _) => ((FloatingTabBar)b).UpdateSelection());

        private static readonly (string Route, string Icon, string Title)[] Tabs =
        {
            (TabNavigation.Home, "Ic.Home", AppResources.Nav_Home),
            (Routes.TripList, "Ic.Luggage", AppResources.Nav_Trips),
            (Routes.Explore, "Ic.Compass", AppResources.Nav_Explore),
            (Routes.Settings, "Ic.UserPlain", AppResources.Nav_Profile),
        };

        private readonly List<(string Route, Label Icon, Label Title)> _items = new();
        private bool _busy;

        public FloatingTabBar()
        {
            var row = new Grid
            {
                ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star), new(new GridLength(72)), new(GridLength.Star), new(GridLength.Star) },
            };
            for (var i = 0; i < Tabs.Length; i++)
                row.Add(CreateTab(Tabs[i]), i < 2 ? i : i + 1);

            var bar = new Border
            {
                HeightRequest = 64,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 24 },
                Padding = new Thickness(4, 0),
                VerticalOptions = LayoutOptions.End,
                Content = row,
                Shadow = new Shadow { Brush = Colors.Black, Opacity = 0.10f, Radius = 18, Offset = new Point(0, 6) },
            };
            bar.SetDynamicResource(BackgroundColorProperty, "tp-surface");
            bar.SetDynamicResource(Border.StrokeProperty, "tp-border");

            var plus = new Border
            {
                WidthRequest = 56,
                HeightRequest = 56,
                StrokeThickness = 4,
                StrokeShape = new RoundRectangle { CornerRadius = 28 },
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Start,
                Padding = 0,
                Shadow = new Shadow { Brush = Color.FromArgb("#2563EB"), Opacity = 0.35f, Radius = 14, Offset = new Point(0, 6) },
                // The + is drawn with two bars instead of the Lucide glyph: the font's line height put the glyph
                // a few pixels off the middle on Android, two centred bars are exactly in the middle everywhere.
                Content = new Grid
                {
                    HorizontalOptions = LayoutOptions.Fill,
                    VerticalOptions = LayoutOptions.Fill,
                    Children = { PlusBar(20, 2.6), PlusBar(2.6, 20) },
                },
            };
            plus.SetDynamicResource(BackgroundColorProperty, "tp-accent");
            plus.SetDynamicResource(Border.StrokeProperty, "tp-surface");
            SemanticProperties.SetDescription(plus, AppResources.Dashboard_CreateTrip);
            plus.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Run(() => Shell.Current.GoToAsync(Routes.TripForm))) });

            // The + stands 20 above the bar. No InputTransparent here: on Android a transparent ContentView with
            // CascadeInputTransparent=false swallowed every tap on the bar (TP-106 fix). An empty layout without
            // a background doesn't take touches anyway, so the page around the bar stays tappable.
            Content = new Grid
            {
                HeightRequest = 92,
                Padding = new Thickness(16, 0, 16, 8),
                Children = { bar, plus },
            };
            VerticalOptions = LayoutOptions.End;
            UpdateSelection();
        }

        /// <summary>The Shell route of the tab this bar sits on ("DashboardPage", "TripList", "Explore", "Settings").</summary>
        public string Current { get => (string)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

        private View CreateTab((string Route, string Icon, string Title) tab)
        {
            var icon = new Label { FontFamily = "Lucide", FontSize = 22, Text = Glyph(tab.Icon), HorizontalOptions = LayoutOptions.Center };
            var title = new Label
            {
                FontSize = 11.5,
                Text = tab.Title,
                HorizontalOptions = LayoutOptions.Center,
                HorizontalTextAlignment = TextAlignment.Center,
                MaxLines = 1,
                LineBreakMode = LineBreakMode.TailTruncation,
            };
            _items.Add((tab.Route, icon, title));

            // The whole slot (full bar height) takes the tap, not only the icon and the label.
            var cell = new Grid
            {
                BackgroundColor = Colors.Transparent,
                Children = { new VerticalStackLayout { Spacing = 3, VerticalOptions = LayoutOptions.Center, Children = { icon, title } } },
            };
            SemanticProperties.SetDescription(cell, tab.Title);
            cell.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Select(tab.Route)) });
            return cell;
        }

        private void UpdateSelection()
        {
            foreach (var (route, icon, title) in _items)
            {
                var selected = route == Current;
                var color = selected ? "tp-nav-active" : "tp-text-3";
                icon.SetDynamicResource(Label.TextColorProperty, color);
                title.SetDynamicResource(Label.TextColorProperty, color);
                title.FontFamily = selected ? "InterBold" : "InterMedium";
            }
        }

        private void Select(string route)
        {
            if (route != Current)
                TabNavigation.Select(route);
        }

        private void Run(Func<Task> action)
        {
            if (_busy)
                return;
            _busy = true;
            action().ContinueWith(_ => _busy = false).FireAndForget();
        }

        private static BoxView PlusBar(double width, double height) => new()
        {
            WidthRequest = width,
            HeightRequest = height,
            CornerRadius = 1.3,
            Color = Colors.White,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };

        private static string Glyph(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is string glyph ? glyph : string.Empty;
    }
}
