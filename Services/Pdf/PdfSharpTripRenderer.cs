using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services.Pdf
{
    /// <summary>
    /// Trip summary PDF (TP-502) drawn with PDFsharp (MIT licence, fully free, no network). Text is laid out
    /// top-to-bottom with simple word wrapping and automatic page breaks; Inter (shipped with the app) is
    /// embedded so Turkish characters render on every device.
    /// </summary>
    public class PdfSharpTripRenderer : ITripPdfRenderer
    {
        private const string FontFamily = "Inter";

        // Unicode (Identity-H) encoding so every glyph of the embedded font is usable - with the default
        // single-byte encoding symbols outside Windows-1252 (the Turkish lira sign, among others) came out as boxes.
        private static readonly XPdfFontOptions PdfUnicode = new(PdfFontEncoding.Unicode);

        private static XFont PdfFont(double size, XFontStyleEx style) => new(FontFamily, size, style, PdfUnicode);
        private static readonly SemaphoreSlim FontLock = new(1, 1);
        private static bool _fontsReady;

        private readonly string _outputDirectory;

        /// <param name="outputDirectory">The app passes FileSystem.CacheDirectory.</param>
        public PdfSharpTripRenderer(string outputDirectory)
        {
            _outputDirectory = outputDirectory;
        }

        public async Task<string> RenderAsync(TripReport report, TripReportService texts)
        {
            await EnsureFontsAsync();

            var path = Path.Combine(_outputDirectory, SafeFileName(report.Trip.Name) + ".pdf");

            await Task.Run(() =>
            {
                using var document = new PdfDocument();
                document.Info.Title = report.Trip.Name;
                document.Info.Creator = "TravelPlanner";

                var writer = new PageWriter(document);
                Write(writer, report);
                writer.Finish();

                document.Save(path);
            });

            return path;
        }

        private static void Write(PageWriter w, TripReport report)
        {
            var trip = report.Trip;
            var currency = trip.EffectiveCurrencyCode;
            var culture = CultureInfo.CurrentCulture;

            // Royal-blue header band (app hero colours): trip name, place, dates.
            w.Header(trip.Name, new[]
            {
                TripReportService.Place(trip),
                $"{trip.CompactDateRangeDisplay} · {string.Format(AppResources.Common_DaysFormat, trip.DurationInDays)}"
            });
            if (!string.IsNullOrWhiteSpace(trip.Description))
                w.Paragraph(trip.Description.Trim());

            // Budget
            w.Heading(AppResources.Report_Budget);
            w.KeyValue(AppResources.Budget_Planned, trip.FormattedBudget);
            w.KeyValue(AppResources.Budget_Spent, MoneyFormatter.FormatIn(report.TotalExpenses, currency));
            if (trip.PlannedBudget > 0)
                w.KeyValue(AppResources.Budget_Remaining, MoneyFormatter.FormatIn(trip.PlannedBudget - report.TotalExpenses, currency));
            foreach (var category in report.Categories.Where(c => c.Total > 0))
                w.KeyValue("   " + ExpenseCategoryDisplayConverter.ToDisplayText(category.Category.ToString()), MoneyFormatter.FormatIn(category.Total, currency));

            // Day plan
            if (report.Plan.Count > 0)
            {
                w.Heading(AppResources.Section_Itinerary);
                foreach (var (day, activities) in report.Plan)
                {
                    var number = (day - trip.StartDate.Date).Days + 1;
                    w.SubHeading($"{string.Format(AppResources.Itinerary_DayFormat, number)} · {day.ToString("dddd d MMMM", culture)}");
                    foreach (var activity in activities)
                        w.Bullet(TripReportService.ActivityLine(activity));
                }
            }

            // Places
            if (report.Places.Count > 0)
            {
                w.Heading(AppResources.Section_Places);
                foreach (var place in report.Places)
                    w.Bullet(place.IsVisited ? $"{place.Name} ({AppResources.Place_Visited})" : place.Name);
            }

            // Split
            if (report.Split is { Settlements.Count: > 0 } split)
            {
                w.Heading(AppResources.Split_WhoOwesWhom);
                foreach (var settlement in split.Settlements)
                    w.Bullet(TripReportService.SettlementLine(settlement, currency));
            }

            // To-dos / packing list
            if (report.Todos.Count > 0)
            {
                w.Heading(AppResources.Report_Tasks);
                foreach (var todo in report.Todos)
                    w.Bullet((todo.IsCompleted ? "[x] " : "[  ] ") + todo.Title); // ASCII: the embedded Inter subset has no ballot-box glyphs
            }

            w.Spacer(10);
            w.Line(string.Format(AppResources.Report_GeneratedFormat, report.GeneratedAt.ToString("g", culture)) + " · " + AppResources.Report_Footer, muted: true, size: 8);
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var clean = new string((name ?? "trip").Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 ? "trip" : clean.Length > 60 ? clean[..60] : clean;
        }

        // ---- Fonts: PDFsharp needs the TTF bytes; MAUI fonts aren't files, so they're also packaged as assets ----

        private static async Task EnsureFontsAsync()
        {
            if (_fontsReady)
                return;

            await FontLock.WaitAsync();
            try
            {
                if (_fontsReady)
                    return;

                var regular = await ReadAssetAsync("fonts/Inter-Regular.ttf");
                var semibold = await ReadAssetAsync("fonts/Inter-Bold.ttf");

                // FontResolver can only be set once per process.
                if (GlobalFontSettings.FontResolver is not EmbeddedFontResolver)
                    GlobalFontSettings.FontResolver = new EmbeddedFontResolver(regular, semibold);

                _fontsReady = true;
            }
            finally
            {
                FontLock.Release();
            }
        }

        private static async Task<byte[]> ReadAssetAsync(string path)
        {
            await using var stream = await FileSystem.OpenAppPackageFileAsync(path);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return memory.ToArray();
        }

        private sealed class EmbeddedFontResolver : IFontResolver
        {
            private const string RegularFace = "Inter#Regular";
            private const string BoldFace = "Inter#Bold";
            private readonly byte[] _regular;
            private readonly byte[] _bold;

            public EmbeddedFontResolver(byte[] regular, byte[] bold)
            {
                _regular = regular;
                _bold = bold;
            }

            public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
                new(bold ? BoldFace : RegularFace);

            public byte[]? GetFont(string faceName) => faceName == BoldFace ? _bold : _regular;
        }

        // ---- Minimal flowing-text layout ----

        private sealed class PageWriter
        {
            private const double Margin = 48;

            // The app's colour tokens (Resources/Themes/LightTheme.xaml) so the PDF looks like the app.
            private static readonly XColor Royal = XColor.FromArgb(30, 58, 138);      // tp-primary / tp-hero  #1E3A8A
            private static readonly XColor Bright = XColor.FromArgb(37, 99, 235);     // tp-accent             #2563EB
            private static readonly XColor AccentText = XColor.FromArgb(29, 78, 216); // tp-accent-text        #1D4ED8
            private static readonly XColor HeroMuted = XColor.FromArgb(195, 206, 235);// tp-hero-muted         #C3CEEB
            private static readonly XColor Soft = XColor.FromArgb(230, 237, 252);     // tp-accent-soft        #E6EDFC
            private static readonly XColor Text = XColor.FromArgb(20, 22, 27);        // tp-text               #14161B
            private static readonly XColor Muted = XColor.FromArgb(107, 114, 128);    // tp-text-3             #6B7280
            private static readonly XColor Rule = XColor.FromArgb(226, 229, 234);     // tp-border             #E2E5EA

            private readonly PdfDocument _document;
            private PdfPage _page = null!;
            private XGraphics _gfx = null!;
            private double _y;
            private int _pageNumber;

            public PageWriter(PdfDocument document)
            {
                _document = document;
                NewPage();
            }

            private double Width => _page.Width.Point - 2 * Margin;
            private double Bottom => _page.Height.Point - Margin;

            private void NewPage()
            {
                _gfx?.Dispose();
                _page = _document.AddPage();
                _page.Size = PdfSharp.PageSize.A4;
                _gfx = XGraphics.FromPdfPage(_page);
                _y = Margin;
                _pageNumber++;

                // Footer: thin accent rule, brand on the left, page number on the right.
                var footer = PdfFont(8, XFontStyleEx.Regular);
                var footerY = _page.Height.Point - Margin + 16;
                _gfx.DrawLine(new XPen(Soft, 1), Margin, footerY - 6, Margin + Width, footerY - 6);
                _gfx.DrawString("TravelPlanner · EAKSOFT", PdfFont(8, XFontStyleEx.Bold), new XSolidBrush(Royal),
                    new XRect(Margin, footerY, Width, 12), XStringFormats.TopLeft);
                _gfx.DrawString(string.Format(AppResources.Report_PageFormat, _pageNumber), footer, new XSolidBrush(Muted),
                    new XRect(Margin, footerY, Width, 12), XStringFormats.TopRight);
            }

            private void Ensure(double height)
            {
                if (_y + height > Bottom)
                    NewPage();
            }

            /// <summary>
            /// Full-width royal-blue band at the top of the first page (like the app's trip card): the title in
            /// white, the subtitle lines in the hero-muted colour, a bright-blue stripe under the band.
            /// </summary>
            public void Header(string title, IReadOnlyList<string> subtitles)
            {
                var titleFont = PdfFont(22, XFontStyleEx.Bold);
                var subFont = PdfFont(10.5, XFontStyleEx.Regular);
                var brandFont = PdfFont(9, XFontStyleEx.Bold);
                var pageWidth = _page.Width.Point;

                var titleLines = Wrap(title, titleFont, Width - 100).ToList();
                var titleLineHeight = titleFont.GetHeight() + 2;
                var subLineHeight = subFont.GetHeight() + 3;
                var bandHeight = 34 + titleLines.Count * titleLineHeight + 6 + subtitles.Count * subLineHeight + 24;

                _gfx.DrawRectangle(new XSolidBrush(Royal), 0, 0, pageWidth, bandHeight);
                _gfx.DrawRectangle(new XSolidBrush(Bright), 0, bandHeight, pageWidth, 4);

                // Brand, top right.
                _gfx.DrawString("TravelPlanner", brandFont, new XSolidBrush(HeroMuted),
                    new XRect(Margin, 22, Width, 12), XStringFormats.TopRight);

                var y = 34.0;
                foreach (var line in titleLines)
                {
                    _gfx.DrawString(line, titleFont, XBrushes.White, new XRect(Margin, y, Width, titleLineHeight), XStringFormats.TopLeft);
                    y += titleLineHeight;
                }

                y += 6;
                foreach (var subtitle in subtitles.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    _gfx.DrawString(subtitle, subFont, new XSolidBrush(HeroMuted), new XRect(Margin, y, Width, subLineHeight), XStringFormats.TopLeft);
                    y += subLineHeight;
                }

                _y = bandHeight + 4 + 22;
            }

            public void Title(string text)
            {
                var font = PdfFont(22, XFontStyleEx.Bold);
                Draw(text, font, Royal, 0);
                _y += 4;
            }

            /// <summary>Section heading: royal-blue text after a short bright-blue bar, light rule underneath.</summary>
            public void Heading(string text)
            {
                _y += 14;
                Ensure(40);
                var font = PdfFont(14, XFontStyleEx.Bold);
                var lineHeight = font.GetHeight();
                _gfx.DrawRectangle(new XSolidBrush(Bright), Margin, _y + 2, 3.5, lineHeight - 4);
                Draw(text, font, Royal, 10);
                _gfx.DrawLine(new XPen(Rule, 0.8), Margin, _y + 2, Margin + Width, _y + 2);
                _y += 8;
            }

            public void SubHeading(string text)
            {
                _y += 4;
                Draw(text, PdfFont(11, XFontStyleEx.Bold), AccentText, 0);
            }

            public void Line(string text, bool muted = false, double size = 10.5) =>
                Draw(text, PdfFont(size, XFontStyleEx.Regular), muted ? Muted : Text, 0);

            public void Paragraph(string text)
            {
                _y += 6;
                Line(text);
            }

            /// <summary>Bright-blue dot, then the (wrapped) text.</summary>
            public void Bullet(string text)
            {
                var font = PdfFont(10.5, XFontStyleEx.Regular);
                var lineHeight = font.GetHeight() + 3;
                Ensure(lineHeight);
                _gfx.DrawEllipse(new XSolidBrush(Bright), Margin + 4, _y + lineHeight / 2 - 3, 4, 4);
                Draw(text, font, Text, 16);
            }

            public void KeyValue(string key, string value)
            {
                var font = PdfFont(10.5, XFontStyleEx.Regular);
                var height = font.GetHeight() + 3;
                Ensure(height);
                _gfx.DrawString(key, font, new XSolidBrush(Text), new XRect(Margin, _y, Width * 0.6, height), XStringFormats.TopLeft);
                _gfx.DrawString(value, PdfFont(10.5, XFontStyleEx.Bold), new XSolidBrush(Royal),
                    new XRect(Margin + Width * 0.6, _y, Width * 0.4, height), XStringFormats.TopRight);
                _y += height;
            }

            public void Spacer(double height) => _y += height;

            /// <summary>Draws word-wrapped text, breaking to a new page when needed.</summary>
            private void Draw(string text, XFont font, XColor color, double indent)
            {
                var brush = new XSolidBrush(color);
                var lineHeight = font.GetHeight() + 3;
                var maxWidth = Width - indent;

                foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
                {
                    foreach (var line in Wrap(paragraph, font, maxWidth))
                    {
                        Ensure(lineHeight);
                        _gfx.DrawString(line, font, brush, new XRect(Margin + indent, _y, maxWidth, lineHeight), XStringFormats.TopLeft);
                        _y += lineHeight;
                    }
                }
            }

            private IEnumerable<string> Wrap(string text, XFont font, double maxWidth)
            {
                var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    yield return string.Empty;
                    yield break;
                }

                var current = string.Empty;
                foreach (var word in words)
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && _gfx.MeasureString(candidate, font).Width > maxWidth)
                    {
                        yield return current;
                        current = word;
                    }
                    else
                    {
                        current = candidate;
                    }
                }

                yield return current;
            }

            public void Finish() => _gfx?.Dispose();
        }
    }
}
