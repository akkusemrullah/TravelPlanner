using Microsoft.Maui.Controls.Shapes;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Views
{
    /// <summary>
    /// TP-93: the first-start tour, a full-screen modal over the shell. One card at a time (<see cref="OnboardingTour.Steps"/>):
    /// a picture drawn from the theme's colours and the app's icons (so it follows light/dark and never goes stale like a
    /// screenshot), a short title and one sentence. Swipe or "İleri" for the next card, "Başla" on the last one; the X and
    /// the back button on the first card close it. However it closes, the tour counts as seen.
    /// </summary>
    public sealed class OnboardingPage : ContentPage
    {
        private const double SlideDistance = 48;

        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IReadOnlyList<OnboardingStep> _steps = OnboardingTour.Steps;
        private readonly OnboardingProgress _progress;
        private readonly IPreferences _preferences;
        private readonly ContentView _card = new();
        private readonly HorizontalStackLayout _dots = new() { Spacing = 6, HorizontalOptions = LayoutOptions.Center };
        private readonly Button _action;
        private bool _moving;
        private bool _closing;

        private OnboardingPage(IPreferences preferences)
        {
            _preferences = preferences;
            _progress = new OnboardingProgress(_steps.Count);
            Shell.SetPresentationMode(this, PresentationMode.ModalAnimated);
            Shell.SetNavBarIsVisible(this, false);
            NavigationPage.SetHasNavigationBar(this, false);
            this.SetDynamicResource(BackgroundColorProperty, "tp-surface");

            var closeIcon = new Label { Text = Res<string>("Ic.Close"), Style = Res<Style>("Icon.Lg") };
            closeIcon.SetDynamicResource(Label.TextColorProperty, "tp-text-2");
            var close = new Border
            {
                Style = Res<Style>("IconButton"),
                WidthRequest = 44,
                HeightRequest = 44,
                HorizontalOptions = LayoutOptions.End,
                Margin = new Thickness(0, 8, 8, 0),
                Content = closeIcon,
            };
            SemanticProperties.SetDescription(close, AppResources.Onboarding_Close);
            close.GestureRecognizers.Add(new TapGestureRecognizer { Command = new Command(Close) });

            // the whole middle takes the swipes, not just the picture
            var swipeArea = new Grid { BackgroundColor = Colors.Transparent, Children = { _card } };
            var left = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
            left.Swiped += (_, _) => Move(forward: true);
            var right = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
            right.Swiped += (_, _) => Move(forward: false);
            swipeArea.GestureRecognizers.Add(left);
            swipeArea.GestureRecognizers.Add(right);

            _action = new Button { Style = Res<Style>("Btn.Primary") };
            _action.Clicked += (_, _) =>
            {
                if (_progress.IsLast)
                    Close();
                else
                    Move(forward: true);
            };

            var bottom = new VerticalStackLayout
            {
                Spacing = 20,
                Padding = new Thickness(24, 0, 24, 28),
                Children = { _dots, _action },
            };

            var layout = new Grid
            {
                RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
                MaximumWidthRequest = 520,
            };
            layout.Add(close);
            layout.Add(swipeArea, 0, 1);
            layout.Add(bottom, 0, 2);
            Content = layout;

            ShowStep();
        }

        /// <summary>Opens the tour; finishes when it's closed (and saved as seen).</summary>
        public static async Task ShowAsync(INavigation navigation, IPreferences preferences)
        {
            var page = new OnboardingPage(preferences);
            await navigation.PushModalAsync(page, animated: true);
            await page._closed.Task;
        }

        // ------------------------------------------------------------------------------------------- cards

        private void ShowStep()
        {
            var step = _steps[_progress.Index];
            _card.Content = BuildCard(step);
            _action.Text = _progress.ActionText;
            SemanticProperties.SetDescription(_card, $"{step.Title}. {step.Text} {_progress.PositionText}");

            _dots.Clear();
            for (var i = 0; i < _progress.Count; i++)
            {
                var active = i == _progress.Index;
                var dot = new BoxView { HeightRequest = 8, WidthRequest = active ? 22 : 8, CornerRadius = 4 };
                dot.SetDynamicResource(BoxView.ColorProperty, active ? "tp-accent" : "tp-border-strong");
                _dots.Add(dot);
            }
        }

        private async void Move(bool forward)
        {
            if (_moving || _closing)
                return;
            if (!(forward ? _progress.Next() : _progress.Back()))
                return;

            _moving = true;
            try
            {
                var direction = forward ? -1 : 1;
                await Task.WhenAll(_card.FadeToAsync(0, 120, Easing.CubicIn), _card.TranslateToAsync(direction * SlideDistance, 0, 120, Easing.CubicIn));
                ShowStep();
                _card.TranslationX = -direction * SlideDistance;
                await Task.WhenAll(_card.FadeToAsync(1, 180, Easing.CubicOut), _card.TranslateToAsync(0, 0, 180, Easing.CubicOut));
            }
            finally
            {
                _card.Opacity = 1;
                _card.TranslationX = 0;
                _moving = false;
            }
        }

        private static View BuildCard(OnboardingStep step)
        {
            var title = new Label
            {
                Text = step.Title,
                Style = Res<Style>("Text.H1"),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(0, 32, 0, 0),
            };
            SemanticProperties.SetHeadingLevel(title, SemanticHeadingLevel.Level1);
            var text = new Label
            {
                Text = step.Text,
                Style = Res<Style>("Text.Body"),
                HorizontalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.WordWrap,
                LineHeight = 1.3,
                Margin = new Thickness(0, 10, 0, 0),
            };
            text.SetDynamicResource(Label.TextColorProperty, "tp-text-2");

            return new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                Padding = new Thickness(32, 0),
                Children = { Picture(step), title, text },
            };
        }

        // The picture: a soft disc with a dashed orbit, the step's icon on a raised tile in the middle and two small
        // bubbles around it. Only theme colours, so it reads the same in light and dark.
        private static View Picture(OnboardingStep step)
        {
            const double width = 240, height = 210;
            var stage = new AbsoluteLayout { WidthRequest = width, HeightRequest = height, HorizontalOptions = LayoutOptions.Center, InputTransparent = true };

            var orbit = new Ellipse { StrokeThickness = 1.5, StrokeDashArray = [4, 6], Fill = Brush.Transparent };
            orbit.SetDynamicResource(Shape.StrokeProperty, "tp-border-strong");
            Place(stage, orbit, Centered(width, height, 204));

            var disc = new Ellipse();
            disc.SetDynamicResource(Shape.FillProperty, "tp-accent-soft");
            Place(stage, disc, Centered(width, height, 168));

            var icon = new Label { Text = Res<string>(step.Icon), Style = Res<Style>("Icon"), FontSize = 46 };
            icon.SetDynamicResource(Label.TextColorProperty, "tp-accent");
            var tile = new Border
            {
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 28 },
                Content = icon,
                Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.12f, Radius = 18, Offset = new Point(0, 6) },
            };
            tile.SetDynamicResource(BackgroundColorProperty, "tp-surface");
            Place(stage, tile, Centered(width, height, 100));

            Place(stage, Bubble(step.SideIcon, "tp-success-soft", "tp-success"), new Rect(170, 22, 50, 50));
            Place(stage, Bubble(step.CornerIcon, "tp-warning-soft", "tp-warning"), new Rect(22, 140, 50, 50));
            Place(stage, Dot(10), new Rect(40, 46, 10, 10));
            Place(stage, Dot(7), new Rect(196, 160, 7, 7));
            return stage;
        }

        private static void Place(AbsoluteLayout stage, View view, Rect bounds)
        {
            AbsoluteLayout.SetLayoutBounds(view, bounds);
            stage.Add(view);
        }

        private static Rect Centered(double width, double height, double size) =>
            new((width - size) / 2, (height - size) / 2, size, size);

        private static View Bubble(string glyph, string fill, string color)
        {
            var icon = new Label { Text = Res<string>(glyph), Style = Res<Style>("Icon"), FontSize = 22 };
            icon.SetDynamicResource(Label.TextColorProperty, color);
            var bubble = new Border { StrokeThickness = 3, StrokeShape = new Ellipse(), Content = icon };
            bubble.SetDynamicResource(BackgroundColorProperty, fill);
            bubble.SetDynamicResource(Border.StrokeProperty, "tp-surface");
            return bubble;
        }

        private static View Dot(double size)
        {
            var dot = new Ellipse { WidthRequest = size, HeightRequest = size, Opacity = 0.45 };
            dot.SetDynamicResource(Shape.FillProperty, "tp-accent");
            return dot;
        }

        // ------------------------------------------------------------------------------------------- close

        protected override bool OnBackButtonPressed()
        {
            // back walks the cards backwards; on the first one it closes the tour
            if (_progress.Index > 0)
                Move(forward: false);
            else
                Close();
            return true;
        }

        private async void Close()
        {
            if (_closing)
                return;
            _closing = true;
            OnboardingTour.MarkSeen(_preferences);
            try
            {
                await Navigation.PopModalAsync(animated: true);
            }
            catch (Exception ex)
            {
                // the page is already gone (navigation changed underneath)
                System.Diagnostics.Debug.WriteLine($"[Onboarding] Close failed: {ex.Message}");
            }
            finally
            {
                _closed.TrySetResult();
            }
        }

        private static T Res<T>(string key) =>
            Application.Current is { } app && app.Resources.TryGetValue(key, out var value) && value is T typed
                ? typed
                : throw new InvalidOperationException($"Resource '{key}' is missing.");
    }
}
