using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// An on/off switch drawn by the app itself (a pill track with a round thumb) instead of the platform Switch.
    /// The Android Material switch was clipped in an "Auto" grid column and its white thumb on a near-white track
    /// was barely visible when off; this one has a fixed size and uses the theme tokens (tp-accent when on,
    /// tp-border-strong when off), so it looks the same everywhere, light and dark.
    /// Tapping it toggles <see cref="IsToggled"/>. Set InputTransparent="True" when the whole row toggles it instead.
    /// </summary>
    public class ToggleSwitch : ContentView
    {
        private const double TrackWidth = 50;
        private const double TrackHeight = 30;
        private const double ThumbSize = 24;
        private const double Inset = 3;

        public static readonly BindableProperty IsToggledProperty = BindableProperty.Create(
            nameof(IsToggled), typeof(bool), typeof(ToggleSwitch), false, BindingMode.TwoWay,
            propertyChanged: (bindable, _, _) => ((ToggleSwitch)bindable).UpdateVisual(animate: true));

        private readonly Border _track;
        private readonly Border _thumb;

        public ToggleSwitch()
        {
            _thumb = new Border
            {
                WidthRequest = ThumbSize,
                HeightRequest = ThumbSize,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = ThumbSize / 2 },
                BackgroundColor = Colors.White,
                HorizontalOptions = LayoutOptions.Start,
                VerticalOptions = LayoutOptions.Center,
                Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.25f, Radius = 3, Offset = new Point(0, 1) },
            };

            _track = new Border
            {
                WidthRequest = TrackWidth,
                HeightRequest = TrackHeight,
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = TrackHeight / 2 },
                Padding = new Thickness(Inset),
                Content = _thumb,
            };

            Content = _track;
            WidthRequest = TrackWidth;
            HeightRequest = TrackHeight;
            HorizontalOptions = LayoutOptions.End;
            VerticalOptions = LayoutOptions.Center;

            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) =>
            {
                if (IsEnabled)
                    IsToggled = !IsToggled;
            };
            GestureRecognizers.Add(tap);

            UpdateVisual(animate: false);
        }

        public bool IsToggled
        {
            get => (bool)GetValue(IsToggledProperty);
            set => SetValue(IsToggledProperty, value);
        }

        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsEnabled))
                Opacity = IsEnabled ? 1 : 0.5;
        }

        private void UpdateVisual(bool animate)
        {
            _track.SetDynamicResource(VisualElement.BackgroundColorProperty, IsToggled ? "tp-accent" : "tp-border-strong");

            var x = IsToggled ? TrackWidth - 2 * Inset - ThumbSize : 0;
            if (animate && Handler is not null)
            {
                _thumb.CancelAnimations();
                _ = _thumb.TranslateToAsync(x, 0, 140, Easing.CubicOut);
            }
            else
            {
                _thumb.TranslationX = x;
            }
        }
    }
}
