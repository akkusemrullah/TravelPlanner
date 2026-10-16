using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// The app's loading animation: three dots that rise and fade in a wave. Replaces the platform spinner on every
    /// waiting screen, so all of them look the same (and like the app) in light and dark.
    /// It animates only while it is visible and on screen - bind <c>IsVisible</c> to the "loading" flag and it
    /// starts and stops by itself. Cheap: one animation moving three small views (scale/opacity/translation only).
    /// </summary>
    public class LoadingDots : ContentView
    {
        private const string AnimationName = "tp-loading-dots";
        private const uint CycleMs = 1100;

        public static readonly BindableProperty DotColorProperty = BindableProperty.Create(
            nameof(DotColor), typeof(Color), typeof(LoadingDots), Colors.Gray,
            propertyChanged: (b, _, n) => ((LoadingDots)b).ApplyColor((Color)n));

        public static readonly BindableProperty DotSizeProperty = BindableProperty.Create(
            nameof(DotSize), typeof(double), typeof(LoadingDots), 10.0,
            propertyChanged: (b, _, _) => ((LoadingDots)b).ApplySize());

        private readonly Border[] _dots;
        private readonly HorizontalStackLayout _row;
        private bool _loaded;

        public LoadingDots()
        {
            _dots = Enumerable.Range(0, 3).Select(_ => new Border
            {
                StrokeThickness = 0,
                StrokeShape = new Ellipse(),
                VerticalOptions = LayoutOptions.Center,
            }).ToArray();

            _row = new HorizontalStackLayout { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
            foreach (var dot in _dots)
                _row.Add(dot);

            Content = _row;
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Center;
            InputTransparent = true;
            SemanticProperties.SetDescription(this, global::TravelPlanner.Resources.Strings.AppResources.A11y_Loading);

            ApplyColor(DotColor);
            ApplySize();

            Loaded += (_, _) => { _loaded = true; UpdateRunning(); };
            Unloaded += (_, _) => { _loaded = false; UpdateRunning(); };
        }

        public Color DotColor
        {
            get => (Color)GetValue(DotColorProperty);
            set => SetValue(DotColorProperty, value);
        }

        public double DotSize
        {
            get => (double)GetValue(DotSizeProperty);
            set => SetValue(DotSizeProperty, value);
        }

        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsVisible))
                UpdateRunning();
        }

        private void ApplyColor(Color color)
        {
            foreach (var dot in _dots)
                dot.BackgroundColor = color;
        }

        private void ApplySize()
        {
            var size = DotSize;
            _row.Spacing = size * 0.6;
            foreach (var dot in _dots)
            {
                dot.WidthRequest = size;
                dot.HeightRequest = size;
            }
            // room for the dots to rise
            HeightRequest = size * 2.2;
        }

        private void UpdateRunning()
        {
            var run = _loaded && IsVisible;
            if (!run)
            {
                this.AbortAnimation(AnimationName);
                return;
            }
            if (this.AnimationIsRunning(AnimationName))
                return;

            var lift = DotSize * 0.45;
            new Animation(t =>
            {
                for (var i = 0; i < _dots.Length; i++)
                {
                    // each dot a third of a cycle behind the previous one; a smooth 0→1→0 bump
                    var phase = (t - i * 0.18 + 1) % 1;
                    var bump = phase < 0.6 ? Math.Sin(phase / 0.6 * Math.PI) : 0;
                    _dots[i].TranslationY = -lift * bump;
                    _dots[i].Opacity = 0.35 + 0.65 * bump;
                    _dots[i].Scale = 0.8 + 0.2 * bump;
                }
            }).Commit(this, AnimationName, length: CycleMs, easing: Easing.Linear, repeat: () => _loaded && IsVisible);
        }
    }
}
