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
                Content = new Label
                {
                    FontFamily = "Lucide",
                    FontSize = 26,
                    Text = Glyph("Ic.Plus"),
                    TextColor = Colors.White,
                    HorizontalTextAlignment = TextAlignment.Center,
                    VerticalTextAlignment = TextAlignment.Center,
                },
            };
            plus.SetDynamicResource(BackgroundColorProperty, "tp-accent");
            plus.SetDynamicResource(Border.StrokeProperty, "tp-surface");
            SemanticProperties.SetDescription(plus, AppResources.Dashboard_CreateTrip);
            plus.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(() => Run(() => Shell.Current.GoToAsync(Routes.TripForm))) });

            // The + stands 20 above the bar. Around the bar and the + the page stays visible and tappable: the
            // containers pass touches through, their children still take them.
            Content = new Grid
            {
                InputTransparent = true,
                CascadeInputTransparent = false,
                HeightRequest = 92,
                Padding = new Thickness(16, 0, 16, 8),
                Children = { bar, plus },
            };
            VerticalOptions = LayoutOptions.End;
            InputTransparent = true;
            CascadeInputTransparent = false;
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

            var cell = new VerticalStackLayout
            {
                Spacing = 3,
                VerticalOptions = LayoutOptions.Center,
                BackgroundColor = Colors.Transparent,
                Children = { icon, title },
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

        private static string Glyph(string key) =>
            Application.Current?.Resources.TryGetValue(key, out var value) == true && value is string glyph ? glyph : string.Empty;
    }
}
