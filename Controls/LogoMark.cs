using System.Globalization;
using Microsoft.Maui.Controls.Shapes;
// 'Path' alone is ambiguous with System.IO.Path (implicit usings).
using Path = Microsoft.Maui.Controls.Shapes.Path;

namespace TravelPlanner.Controls
{
    /// <summary>
    /// The app's logo mark drawn in code: the map pin with the paper plane cut into it (docs/brand/logo-mark.svg,
    /// 120×120 view box). Sharp at any <see cref="Size"/>; the loading animations move the whole mark and its
    /// <see cref="Plane"/> separately.
    /// </summary>
    public class LogoMark : Grid
    {
        /// <summary>Where the pin's tip sits, as a share of the mark's height (112 of 120).</summary>
        public const double TipRatio = 112.0 / 120.0;

        public static readonly BindableProperty SizeProperty = BindableProperty.Create(
            nameof(Size), typeof(double), typeof(LogoMark), 56.0,
            propertyChanged: (b, _, _) => ((LogoMark)b).Build());

        public static readonly BindableProperty PinColorProperty = BindableProperty.Create(
            nameof(PinColor), typeof(Color), typeof(LogoMark), Color.FromArgb("#2563EB"),
            propertyChanged: (b, _, _) => ((LogoMark)b).ApplyColors());

        public static readonly BindableProperty PlaneColorProperty = BindableProperty.Create(
            nameof(PlaneColor), typeof(Color), typeof(LogoMark), Colors.White,
            propertyChanged: (b, _, _) => ((LogoMark)b).ApplyColors());

        private readonly Path _pin = new();
        private readonly Path _planeBody = new();
        private readonly Path _planeFold = new() { StrokeLineCap = PenLineCap.Round };
        private readonly Grid _plane = new()
        {
            // rotates around the middle of the plane, not of the pin
            AnchorX = 0.49,
            AnchorY = 0.40,
        };

        public LogoMark()
        {
            HorizontalOptions = LayoutOptions.Center;
            VerticalOptions = LayoutOptions.Start;
            InputTransparent = true;

            _plane.Add(_planeBody);
            _plane.Add(_planeFold);
            Add(_pin);
            Add(_plane);

            Build();
            ApplyColors();
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

        /// <summary>The paper plane (animated on its own: it tilts as if taking off).</summary>
        public View Plane => _plane;

        private void Build()
        {
            var k = Size / 120.0;
            string F(double v) => (v * k).ToString("0.###", CultureInfo.InvariantCulture);

            WidthRequest = HeightRequest = Size;
            _plane.WidthRequest = _plane.HeightRequest = Size;

            _pin.Data = Parse($"M {F(60)},{F(112)} C {F(60)},{F(112)} {F(22)},{F(72)} {F(22)},{F(46)} " +
                              $"A {F(38)},{F(38)} 0 0 1 {F(98)},{F(46)} C {F(98)},{F(72)} {F(60)},{F(112)} {F(60)},{F(112)} Z");
            _planeBody.Data = Parse($"M {F(37)},{F(47)} L {F(81)},{F(28)} L {F(65)},{F(69)} L {F(57)},{F(55)} Z");
            _planeFold.Data = Parse($"M {F(57)},{F(55)} L {F(81)},{F(28)}");
            _planeFold.StrokeThickness = Math.Max(1, 3.5 * k);
        }

        private void ApplyColors()
        {
            _pin.Fill = new SolidColorBrush(PinColor);
            _planeBody.Fill = new SolidColorBrush(PlaneColor);
            _planeFold.Stroke = new SolidColorBrush(PinColor); // the fold is "cut" back into the pin
        }

        private static Geometry Parse(string data) =>
            (Geometry)new PathGeometryConverter().ConvertFromInvariantString(data)!;
    }
}
