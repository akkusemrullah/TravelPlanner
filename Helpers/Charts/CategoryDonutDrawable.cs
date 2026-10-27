using TravelPlanner.Models;

namespace TravelPlanner.Helpers.Charts
{
    /// <summary>One slice of the category distribution (TP-404).</summary>
    public sealed record CategoryShare(ExpenseCategory Category, double Fraction);

    /// <summary>
    /// TP-104: the prototype's category donut - a 22-thick ring on the sunken track, one arc per category (with a
    /// small gap between arcs), "Toplam" and the total in the middle. The legend under it carries the numbers for
    /// screen readers.
    /// </summary>
    public sealed class CategoryDonutDrawable : IDrawable
    {
        public IReadOnlyList<CategoryShare> Shares { get; set; } = Array.Empty<CategoryShare>();

        public string CenterTitle { get; set; } = string.Empty;

        public string CenterValue { get; set; } = string.Empty;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            const float thickness = 22f;
            var size = Math.Min(dirtyRect.Width, dirtyRect.Height);
            var radius = size / 2f - thickness / 2f - 4f;
            if (radius <= 0)
                return;
            var cx = dirtyRect.Center.X;
            var cy = dirtyRect.Center.Y;
            var ring = new RectF(cx - radius, cy - radius, radius * 2, radius * 2);

            canvas.SaveState();
            canvas.StrokeSize = thickness;
            canvas.StrokeLineCap = LineCap.Butt;

            canvas.StrokeColor = ChartPalette.Track;
            canvas.DrawEllipse(ring);

            var total = Shares.Sum(s => s.Fraction);
            if (total > 0)
            {
                // Clockwise from 12 o'clock. MAUI angles run counter-clockwise from 3 o'clock.
                var gap = Shares.Count(s => s.Fraction > 0) > 1 ? 1.2f : 0f;
                var start = 90f;
                foreach (var share in Shares.Where(s => s.Fraction > 0))
                {
                    var sweep = (float)(360.0 * share.Fraction / total);
                    var drawn = Math.Max(sweep - gap, 0.5f);
                    canvas.StrokeColor = ChartPalette.ForCategory(share.Category);
                    if (drawn >= 359.9f)
                        canvas.DrawEllipse(ring);
                    else
                        canvas.DrawArc(ring, start - drawn, start, false, false);
                    start -= sweep;
                }
            }

            canvas.FontColor = ChartPalette.Text;
            canvas.FontSize = 11;
            canvas.DrawString(CenterTitle, cx - radius, cy - 22, radius * 2, 16,
                Microsoft.Maui.Graphics.HorizontalAlignment.Center, Microsoft.Maui.Graphics.VerticalAlignment.Center);
            canvas.Font = new Microsoft.Maui.Graphics.Font("InterBold");
            canvas.FontColor = ChartPalette.Strong;
            canvas.FontSize = 16;
            canvas.DrawString(CenterValue, cx - radius + thickness / 2f, cy - 4, radius * 2 - thickness, 22,
                Microsoft.Maui.Graphics.HorizontalAlignment.Center, Microsoft.Maui.Graphics.VerticalAlignment.Center);

            canvas.RestoreState();
        }
    }
}
