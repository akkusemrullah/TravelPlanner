using System.Globalization;

namespace TravelPlanner.Helpers.Charts
{
    /// <summary>One bar of the daily spending chart.</summary>
    public sealed record DailyBar(DateTime Date, decimal Total);

    /// <summary>
    /// Vertical bar chart of spending per trip day (TP-404), with an optional dashed "daily budget" line
    /// (planned budget / trip length). Days above that line are drawn in the warning colour.
    /// </summary>
    public sealed class DailySpendingDrawable : IDrawable
    {
        public IReadOnlyList<DailyBar> Bars { get; set; } = Array.Empty<DailyBar>();

        /// <summary>Planned budget per day; 0 = no line.</summary>
        public decimal DailyBudget { get; set; }

        /// <summary>Formats an axis value (the page passes the trip-currency formatter).</summary>
        public Func<decimal, string> FormatValue { get; set; } = v => v.ToString("0", CultureInfo.CurrentCulture);

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            canvas.SaveState();

            // TP-104: the prototype's chart - no value axis (the bars' amounts are in the description), two light grid
            // lines and a firmer base line, day numbers ("1.g") under the bars.
            const float leftAxis = 8f;
            const float bottomAxis = 22f;
            const float topPadding = 10f;

            var plot = new RectF(dirtyRect.Left + leftAxis, dirtyRect.Top + topPadding,
                Math.Max(dirtyRect.Width - leftAxis - 4f, 1f), Math.Max(dirtyRect.Height - bottomAxis - topPadding, 1f));

            var max = Math.Max(Bars.Count == 0 ? 0m : Bars.Max(b => b.Total), DailyBudget);
            if (max <= 0)
                max = 1m;
            max *= 1.1m; // headroom

            canvas.FontSize = 10;
            canvas.FontColor = ChartPalette.Text;

            canvas.StrokeSize = 1;
            canvas.StrokeColor = ChartPalette.Grid;
            foreach (var fraction in new[] { 0.5f, 1f })
            {
                var y = plot.Bottom - fraction / 1.1f * plot.Height;
                canvas.DrawLine(plot.Left, y, plot.Right, y);
            }
            canvas.StrokeColor = ChartPalette.Axis;
            canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom);

            if (Bars.Count > 0)
            {
                var slot = plot.Width / Bars.Count;
                var barWidth = Math.Max(Math.Min(slot * 0.65f, 28f), 2f);
                var labelEvery = Math.Max(1, (int)Math.Ceiling(Bars.Count / 10.0));
                if (slot * labelEvery < 24f) // never let neighbouring labels overlap on narrow screens
                    labelEvery = Math.Max(1, (int)Math.Ceiling(24f / Math.Max(slot, 0.1f)));

                for (var i = 0; i < Bars.Count; i++)
                {
                    var bar = Bars[i];
                    var height = (float)(bar.Total / max) * plot.Height;
                    var x = plot.Left + i * slot + (slot - barWidth) / 2f;

                    if (height > 0)
                    {
                        canvas.FillColor = DailyBudget > 0 && bar.Total > DailyBudget ? ChartPalette.BarOverBudget : ChartPalette.Bar;
                        canvas.FillRoundedRectangle(x, plot.Bottom - height, barWidth, height, Math.Min(4f, barWidth / 2f));
                    }

                    if (i % labelEvery == 0)
                    {
                        // On long trips a slot is only a few pixels wide - centre a fixed-width label on the bar
                        // instead of squeezing (and clipping) the day number into the slot.
                        const float labelWidth = 28f;
                        canvas.DrawString(DayLabel(i, Bars.Count),
                            plot.Left + i * slot + slot / 2f - labelWidth / 2f, plot.Bottom + 4, labelWidth, 14,
                            Microsoft.Maui.Graphics.HorizontalAlignment.Center, Microsoft.Maui.Graphics.VerticalAlignment.Top);
                    }
                }
            }

            if (DailyBudget > 0)
            {
                var y = plot.Bottom - (float)(DailyBudget / max) * plot.Height;
                canvas.StrokeColor = ChartPalette.BudgetLine;
                canvas.StrokeSize = 1.5f;
                canvas.StrokeDashPattern = new float[] { 5, 4 };
                canvas.DrawLine(plot.Left, y, plot.Right, y);
            }

            canvas.RestoreState();
        }

        /// <summary>"3.g" (the trip's day number) on short trips, just "3" on long ones.</summary>
        internal static string DayLabel(int index, int count) =>
            count > 10 ? (index + 1).ToString(CultureInfo.CurrentCulture) : string.Format(CultureInfo.CurrentCulture, Resources.Strings.AppResources.Analysis_DayShortFormat, index + 1);
    }
}
