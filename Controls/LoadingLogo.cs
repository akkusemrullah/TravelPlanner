using Microsoft.Maui.Controls.Shapes;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// The app's one loading animation, made from the logo - the same everywhere (splash, AI planner, every page
    /// that loads): the map pin hops like a marker dropping onto a map, squashes a little when it lands and sends a
    /// ring out along the ground from its tip; its shadow shrinks while it is up, and the paper plane inside tilts as
    /// if taking off. Colours come from the implicit style in Styles.xaml (the splash sets its own).
    /// For tiny inline waits next to a text (buttons, rows) <see cref="LoadingDots"/> stays.
    /// Animates only while visible and on screen - bind <c>IsVisible</c> to the "loading" flag and it starts and
    /// stops by itself. Cheap: one animation, transforms only.
    /// </summary>
    public class LoadingLogo : ContentView
    {
        private const string AnimationName = "tp-loading-logo";
        private const uint CycleMs = 1100;
        private const double RingMaxScale = 3.2;

        public static readonly BindableProperty SizeProperty = BindableProperty.Create(
            nameof(Size), typeof(double), typeof(LoadingLogo), 56.0,
            propertyChanged: (b, _, _) => ((LoadingLogo)b).ApplyLayout());

        public static readonly BindableProperty PinColorProperty = BindableProperty.Create(
            nameof(PinColor), typeof(Color), typeof(LoadingLogo), Color.FromArgb("#2563EB"),
            propertyChanged: (b, _, n) =>
            {
                var view = (LoadingLogo)b;
                view._mark.PinColor = (Color)n;
                view._ring.Stroke = new SolidColorBrush((Color)n);
            });

        public static readonly BindableProperty PlaneColorProperty = BindableProperty.Create(
            nameof(PlaneColor), typeof(Color), typeof(LoadingLogo), Colors.White,
            propertyChanged: (b, _, n) => ((LoadingLogo)b)._mark.PlaneColor = (Color)n);

        public static readonly BindableProperty ShadowColorProperty = BindableProperty.Create(
            nameof(ShadowColor), typeof(Color), typeof(LoadingLogo), Colors.Black,
            propertyChanged: (b, _, n) => ((LoadingLogo)b)._shadow.Fill = new SolidColorBrush((Color)n));

        private readonly LogoMark _mark = new();
        private readonly Ellipse _shadow = new() { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Start };
        private readonly Ellipse _ring = new() { HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Start, Opacity = 0 };
        private readonly Grid _stage = new() { HorizontalOptions = LayoutOptions.Center };
        private double _lift;
        private bool _loaded;

        public LoadingLogo()
        {
            _shadow.Fill = new SolidColorBrush(ShadowColor);
            _ring.Stroke = new SolidColorBrush(PinColor);
            _stage.Add(_ring);
            _stage.Add(_shadow);
            _stage.Add(_mark);

            Content = _stage;
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Center;
            InputTransparent = true;
            SemanticProperties.SetDescription(this, global::TravelPlanner.Resources.Strings.AppResources.A11y_Loading);

            ApplyLayout();
            Rest();

            Loaded += (_, _) => { _loaded = true; UpdateRunning(); };
            Unloaded += (_, _) => { _loaded = false; UpdateRunning(); };
        }

        public double Size
        {
            get => (double)GetValue(SizeProperty);
            set => SetValue(SizeProperty, value);
        }

        public Color PinColor
        {
            get => (Color)GetValue(PinColorProperty);
            set => SetValue(PinColorProperty, value);
        }

        public Color PlaneColor
        {
            get => (Color)GetValue(PlaneColorProperty);
            set => SetValue(PlaneColorProperty, value);
        }

        public Color ShadowColor
        {
            get => (Color)GetValue(ShadowColorProperty);
            set => SetValue(ShadowColorProperty, value);
        }

        protected override void OnPropertyChanged(string? propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(IsVisible))
                UpdateRunning();
        }

        // Pin at the top with room above it to hop into; the shadow sits under its tip.
        private void ApplyLayout()
        {
            var size = Size;
            _lift = size * 0.22;
            var tipY = _lift + size * LogoMark.TipRatio;
            var shadowW = size * 0.42;
            var shadowH = Math.Max(3, size * 0.09);

            _mark.Size = size;
            _mark.Margin = new Thickness(0, _lift, 0, 0);
            _mark.AnchorX = 0.5;
            _mark.AnchorY = LogoMark.TipRatio; // squash towards the tip, where it "lands"

            _shadow.WidthRequest = _ring.WidthRequest = shadowW;
            _shadow.HeightRequest = _ring.HeightRequest = shadowH;
            _shadow.Margin = _ring.Margin = new Thickness(0, tipY - shadowH / 2, 0, 0);

            // Wide enough for the landing ring to spread out (RingMaxScale × the shadow).
            _stage.WidthRequest = Math.Max(size, shadowW * RingMaxScale);
            _stage.HeightRequest = tipY + shadowH * RingMaxScale / 2 + 2;
        }

        private void Rest()
        {
            _mark.TranslationY = 0;
            _mark.ScaleX = _mark.ScaleY = 1;
            _mark.Plane.Rotation = 0;
            _mark.Plane.TranslationX = _mark.Plane.TranslationY = 0;
            _shadow.Scale = 1;
            _shadow.Opacity = 0.22;
            _ring.Opacity = 0;
        }

        private void UpdateRunning()
        {
            if (!(_loaded && IsVisible))
            {
                this.AbortAnimation(AnimationName);
                Rest();
                return;
            }
            if (this.AnimationIsRunning(AnimationName))
                return;

            new Animation(t =>
            {
                // A jump: up and back down in one cycle (parabola, so it hangs a moment at the top).
                var h = 4 * t * (1 - t);
                _mark.TranslationY = -_lift * h;

                // Touching the ground (start/end of the cycle): a short squash.
                var ground = Math.Max(0, 0.18 - Math.Min(t, 1 - t)) / 0.18;
                _mark.ScaleY = 1 - 0.10 * ground;
                _mark.ScaleX = 1 + 0.07 * ground;

                // The plane tilts up while the pin is in the air.
                _mark.Plane.Rotation = -14 * h;
                _mark.Plane.TranslationX = Size * 0.03 * h;
                _mark.Plane.TranslationY = -Size * 0.03 * h;

                // Shadow: small and faint while up, full under the pin on the ground.
                _shadow.Scale = 1 - 0.5 * h;
                _shadow.Opacity = 0.22 - 0.14 * h;

                // The landing sends a ring out along the ground (it starts as the pin touches down).
                var ringScale = 1 + (RingMaxScale - 1) * t;
                _ring.Scale = ringScale;
                _ring.StrokeThickness = Math.Max(1, Size / 40) / ringScale; // stays a thin line while it grows
                _ring.Opacity = 0.55 * (1 - t);
            }).Commit(this, AnimationName, length: CycleMs, easing: Easing.Linear, repeat: () => _loaded && IsVisible);
        }
    }
}
