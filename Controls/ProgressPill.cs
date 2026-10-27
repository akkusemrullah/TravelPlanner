using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// TP-102: the prototype's Progress - a rounded track (tp-surface-sunken) with a rounded fill. <see cref="Progress"/> is
    /// 0..1 (clamped); <see cref="Tone"/> picks the fill: accent (default), success, warning, danger.
    /// The platform ProgressBar can't be rounded or recoloured per theme reliably, so this draws its own.
    /// </summary>
    public class ProgressPill : ContentView
    {
        public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
            nameof(Progress), typeof(double), typeof(ProgressPill), 0d, propertyChanged: (b, _, _) => ((ProgressPill)b).UpdateFill());

        public static readonly BindableProperty ToneProperty = BindableProperty.Create(
            nameof(Tone), typeof(string), typeof(ProgressPill), "accent", propertyChanged: (b, _, _) => ((ProgressPill)b).UpdateTone());

        public static readonly BindableProperty BarHeightProperty = BindableProperty.Create(
            nameof(BarHeight), typeof(double), typeof(ProgressPill), 8d, propertyChanged: (b, _, _) => ((ProgressPill)b).UpdateHeight());

        private readonly Border _track;
        private readonly Border _fill;

        public ProgressPill()
        {
            _fill = new Border
            {
                StrokeThickness = 0,
                HorizontalOptions = LayoutOptions.Start,
                WidthRequest = 0,
            };
            _track = new Border
            {
                StrokeThickness = 0,
                Padding = 0,
                Content = _fill,
            };
            _track.SetDynamicResource(BackgroundColorProperty, "tp-surface-sunken");
            _track.SizeChanged += (_, _) => UpdateFill();
            Content = _track;
            UpdateHeight();
            UpdateTone();
        }

        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
        public string Tone { get => (string)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
        public double BarHeight { get => (double)GetValue(BarHeightProperty); set => SetValue(BarHeightProperty, value); }

        private void UpdateHeight()
        {
            var h = BarHeight;
            _track.HeightRequest = h;
            _fill.HeightRequest = h;
            _track.StrokeShape = new RoundRectangle { CornerRadius = h / 2 };
            _fill.StrokeShape = new RoundRectangle { CornerRadius = h / 2 };
        }

        private void UpdateFill()
        {
            var width = _track.Width;
            if (width <= 0)
                return;
            var value = double.IsNaN(Progress) ? 0 : Math.Clamp(Progress, 0, 1);
            _fill.WidthRequest = width * value;
            _fill.IsVisible = value > 0;
        }

        private void UpdateTone()
        {
            var key = Tone?.ToLowerInvariant() switch
            {
                "success" => "tp-success",
                "warning" => "tp-warning",
                "danger" => "tp-danger",
                _ => "tp-accent",
            };
            _fill.SetDynamicResource(BackgroundColorProperty, key);
        }
    }
}
