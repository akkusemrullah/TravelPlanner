namespace TravelPlanner.Controls
{
    /// <summary>
    /// The launch splash's map scene (Views/SplashPage), drawn in one GraphicsView on a fixed
    /// <see cref="StageWidth"/>×<see cref="StageHeight"/> stage: a faint globe, a dashed flight route that draws
    /// itself while a paper plane flies along it, and the route ending where the logo pin's own plane sits - so the
    /// pin "catches" the plane. SplashPage drives <see cref="MapReveal"/> and <see cref="Flight"/>; nothing here runs
    /// on its own. Only for the splash: the app's loading animation stays <see cref="LoadingLogo"/>.
    /// </summary>
    public class SplashRouteView : GraphicsView
    {
        public const double StageWidth = 320;
        public const double StageHeight = 260;

        /// <summary>Logo pin: size and top-left on the stage (SplashPage places its LogoMark here).</summary>
        public const double PinSize = 96;
        public const double PinLeft = (StageWidth - PinSize) / 2;
        public const double PinTop = 62;

        private readonly SceneDrawable _scene = new();

        public SplashRouteView()
        {
            Drawable = _scene;
            WidthRequest = StageWidth;
            HeightRequest = StageHeight;
            BackgroundColor = Colors.Transparent;
            InputTransparent = true;
            AutomationProperties.SetIsInAccessibleTree(this, false);
        }

        /// <summary>0 → 1: the globe and the departure dot fade in.</summary>
        public double MapReveal
        {
            get => _scene.MapReveal;
            set { _scene.MapReveal = (float)value; Invalidate(); }
        }

        /// <summary>0 → 1: how far the plane has flown; the dashed trail follows it.</summary>
        public double Flight
        {
            get => _scene.Flight;
            set { _scene.Flight = (float)value; Invalidate(); }
        }

        /// <summary>0 → 1: a ring spreads out on the ground from the pin's tip as it lands.</summary>
        public double Landing
        {
            get => _scene.Landing;
            set { _scene.Landing = (float)value; Invalidate(); }
        }

        private sealed class SceneDrawable : IDrawable
        {
            public float MapReveal;
            public float Flight;
            public float Landing;

            // Route: from the lower left of the globe up and over into the logo's plane (logo-mark.svg ≈ 59,48 of 120).
            private static readonly PointF P0 = new(26, 214);
            private static readonly PointF P1 = new(56, 96);
            private static readonly PointF P2 = new(118, 40);
            private static readonly PointF P3 = new((float)(PinLeft + PinSize * 59 / 120), (float)(PinTop + PinSize * 48 / 120));

            // A few "cities" on the globe, blinking in one after another.
            private static readonly PointF[] Cities =
            {
                new(78, 168), new(236, 104), new(258, 176), new(104, 214), new(206, 222),
            };

            public void Draw(ICanvas canvas, RectF dirtyRect)
            {
                canvas.Antialias = true;
                var reveal = Math.Clamp(MapReveal, 0, 1);
                if (reveal <= 0)
                    return;

                DrawGlobe(canvas, reveal);
                DrawCities(canvas, reveal);
                DrawLandingRing(canvas, Math.Clamp(Landing, 0, 1));
                DrawRoute(canvas, reveal, Math.Clamp(Flight, 0, 1));
            }

            private static void DrawGlobe(ICanvas canvas, float reveal)
            {
                const float cx = (float)StageWidth / 2, cy = 150, r = 112;
                canvas.StrokeColor = Colors.White.WithAlpha(0.10f * reveal);
                canvas.StrokeSize = 1;
                canvas.StrokeDashPattern = null;

                canvas.DrawEllipse(cx - r, cy - r, 2 * r, 2 * r);
                // Meridians
                foreach (var k in new[] { 0.36f, 0.72f })
                    canvas.DrawEllipse(cx - r * k, cy - r, 2 * r * k, 2 * r);
                canvas.DrawLine(cx, cy - r, cx, cy + r);
                // Parallels: flattened ellipses read as lines going round the globe.
                foreach (var y in new[] { -0.5f, 0f, 0.5f })
                {
                    var half = r * MathF.Sqrt(1 - y * y);
                    var py = cy + y * r;
                    canvas.DrawEllipse(cx - half, py - half * 0.12f, 2 * half, half * 0.24f);
                }
            }

            private static void DrawLandingRing(ICanvas canvas, float landing)
            {
                if (landing <= 0 || landing >= 1)
                    return;
                var cx = (float)(PinLeft + PinSize / 2);
                var cy = (float)(PinTop + PinSize * LogoMark.TipRatio);
                var w = 30 + 90 * landing;
                canvas.StrokeColor = Colors.White.WithAlpha(0.6f * (1 - landing));
                canvas.StrokeSize = 1.5f;
                canvas.StrokeDashPattern = null;
                canvas.DrawEllipse(cx - w / 2, cy - w * 0.14f, w, w * 0.28f);
            }

            private static void DrawCities(ICanvas canvas, float reveal)
            {
                for (var i = 0; i < Cities.Length; i++)
                {
                    // Staggered: each city lights up a little after the previous one.
                    var a = Math.Clamp(reveal * 1.6f - i * 0.12f, 0, 1);
                    if (a <= 0)
                        continue;
                    canvas.FillColor = Colors.White.WithAlpha(0.35f * a);
                    canvas.FillCircle(Cities[i], 2.2f);
                }
            }

            private static void DrawRoute(ICanvas canvas, float reveal, float flight)
            {
                // Departure point: a dot with a soft halo.
                canvas.FillColor = Colors.White.WithAlpha(0.18f * reveal);
                canvas.FillCircle(P0, 9);
                canvas.FillColor = Colors.White.WithAlpha(0.95f * reveal);
                canvas.FillCircle(P0, 4);

                if (flight <= 0)
                    return;

                // Dashed trail behind the plane.
                const int steps = 48;
                var path = new PathF(P0);
                var n = Math.Max(1, (int)(steps * flight));
                for (var i = 1; i <= n; i++)
                    path.LineTo(Point(flight * i / n));
                canvas.StrokeColor = Colors.White.WithAlpha(0.75f);
                canvas.StrokeSize = 2;
                canvas.StrokeLineCap = LineCap.Round;
                canvas.StrokeDashPattern = new float[] { 2, 6 };
                canvas.DrawPath(path);
                canvas.StrokeDashPattern = null;

                // The plane, nose along the route; it shrinks into the pin over the last stretch.
                if (flight >= 1)
                    return;
                var at = Point(flight);
                var tangent = Tangent(flight);
                var angle = MathF.Atan2(tangent.Y, tangent.X) * 180f / MathF.PI;
                var scale = 1.6f * (flight > 0.85f ? 1 - (flight - 0.85f) / 0.15f * 0.6f : 1);

                canvas.SaveState();
                canvas.Translate(at.X, at.Y);
                canvas.Rotate(angle);
                canvas.Scale(scale, scale);
                var plane = new PathF(8, 0);
                plane.LineTo(-7, -6);
                plane.LineTo(-3.5f, 0);
                plane.LineTo(-7, 6);
                plane.Close();
                canvas.FillColor = Colors.White;
                canvas.FillPath(plane);
                canvas.RestoreState();
            }

            private static PointF Point(float t)
            {
                var u = 1 - t;
                return new PointF(
                    u * u * u * P0.X + 3 * u * u * t * P1.X + 3 * u * t * t * P2.X + t * t * t * P3.X,
                    u * u * u * P0.Y + 3 * u * u * t * P1.Y + 3 * u * t * t * P2.Y + t * t * t * P3.Y);
            }

            private static PointF Tangent(float t)
            {
                var u = 1 - t;
                return new PointF(
                    3 * u * u * (P1.X - P0.X) + 6 * u * t * (P2.X - P1.X) + 3 * t * t * (P3.X - P2.X),
                    3 * u * u * (P1.Y - P0.Y) + 6 * u * t * (P2.Y - P1.Y) + 3 * t * t * (P3.Y - P2.Y));
            }
        }
    }
}
