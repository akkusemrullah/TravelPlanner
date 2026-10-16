using TravelPlanner.Models;

namespace TravelPlanner.Helpers.Charts
{
    /// <summary>One slice of the category distribution (TP-404).</summary>
    public sealed record CategoryShare(ExpenseCategory Category, double Fraction);

    /// <summary>
    /// A single rounded horizontal bar split into category segments - a compact, label-free "distribution"
    /// view; the legend list under it carries names, amounts and percentages (and is what screen readers read).
    /// Drawn with Microsoft.Maui.Graphics, so no third-party chart library is needed.
    /// </summary>
    public sealed class CategoryBarDrawable : IDrawable
    {
        public IReadOnlyList<CategoryShare> Shares { get; set; } = Array.Empty<CategoryShare>();

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var height = Math.Min(dirtyRect.Height, 18f);
            var top = dirtyRect.Top + (dirtyRect.Height - height) / 2f;
            var bar = new RectF(dirtyRect.Left, top, dirtyRect.Width, height);
            var radius = height / 2f;

            canvas.SaveState();

            canvas.FillColor = ChartPalette.Track;
            canvas.FillRoundedRectangle(bar, radius);

            var total = Shares.Sum(s => s.Fraction);
            if (total > 0)
            {
                // Clip to the rounded bar so the first/last segments get the rounded ends.
                var clip = new PathF();
                clip.AppendRoundedRectangle(bar, radius);
                canvas.ClipPath(clip);

                var x = bar.Left;
                foreach (var share in Shares.Where(s => s.Fraction > 0))
                {
                    var width = (float)(bar.Width * share.Fraction / total);
                    canvas.FillColor = ChartPalette.ForCategory(share.Category);
                    canvas.FillRectangle(x, bar.Top, width, bar.Height);
                    x += width;
                }
            }

            canvas.RestoreState();
        }
    }
}
