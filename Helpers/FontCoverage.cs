using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-74: which characters a TrueType font can draw (read from its "cmap" table), so the PDF report can leave out
    /// what the embedded font has no glyph for - emoji, Japanese … - instead of printing empty boxes.
    /// </summary>
    public sealed class FontCoverage
    {
        private readonly HashSet<int> _codePoints;

        private FontCoverage(HashSet<int> codePoints) => _codePoints = codePoints;

        public int Count => _codePoints.Count;

        public bool Has(int codePoint) => _codePoints.Contains(codePoint);

        /// <summary>Characters every one of the fonts can draw (the report mixes regular and bold).</summary>
        public static FontCoverage Common(params FontCoverage[] fonts) =>
            new(fonts.Skip(1).Aggregate(new HashSet<int>(fonts[0]._codePoints), (set, f) => { set.IntersectWith(f._codePoints); return set; }));

        /// <summary>Reads a .ttf/.otf's Unicode cmap (formats 4 and 12). An unreadable font covers nothing.</summary>
        public static FontCoverage FromFont(byte[] font)
        {
            var points = new HashSet<int>();
            try
            {
                ReadCmap(font, points);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                points.Clear();
            }
            return new FontCoverage(points);
        }

        /// <summary>
        /// The text without the characters the font can't draw. Line breaks and tabs stay; spaces left doubled by a
        /// removed emoji are closed up. With an empty coverage (font unreadable) the text is returned unchanged.
        /// </summary>
        public string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text) || _codePoints.Count == 0)
                return text ?? string.Empty;

            var builder = new StringBuilder(text.Length);
            var removed = false;
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value is '\n' or '\r' or '\t' || Has(rune.Value))
                    builder.Append(rune.ToString());
                else
                    removed = true;
            }
            if (!removed)
                return text;
            return Regex.Replace(builder.ToString(), " {2,}", " ").Trim(' ');
        }

        private static void ReadCmap(byte[] font, HashSet<int> points)
        {
            var span = font.AsSpan();
            var numTables = U16(span, 4);
            var cmap = -1;
            for (var i = 0; i < numTables; i++)
            {
                var record = 12 + i * 16;
                if (span.Slice(record, 4).SequenceEqual("cmap"u8))
                {
                    cmap = (int)U32(span, record + 8);
                    break;
                }
            }
            if (cmap < 0)
                return;

            int best = -1, bestFormat = 0;
            var subtables = U16(span, cmap + 2);
            for (var i = 0; i < subtables; i++)
            {
                var record = cmap + 4 + i * 8;
                var platform = U16(span, record);
                var encoding = U16(span, record + 2);
                var offset = cmap + (int)U32(span, record + 4);
                var format = U16(span, offset);
                var unicode = platform == 0 || (platform == 3 && encoding is 1 or 10);
                if (unicode && (format == 12 || (format == 4 && bestFormat != 12)))
                {
                    best = offset;
                    bestFormat = format;
                }
            }

            if (bestFormat == 12)
                ReadFormat12(span, best, points);
            else if (bestFormat == 4)
                ReadFormat4(span, best, points);
        }

        private static void ReadFormat4(ReadOnlySpan<byte> span, int table, HashSet<int> points)
        {
            var segments = U16(span, table + 6) / 2;
            var ends = table + 14;
            var starts = ends + segments * 2 + 2;
            var deltas = starts + segments * 2;
            var rangeOffsets = deltas + segments * 2;
            for (var s = 0; s < segments; s++)
            {
                int end = U16(span, ends + s * 2), start = U16(span, starts + s * 2);
                var delta = (short)U16(span, deltas + s * 2);
                var rangeOffsetAt = rangeOffsets + s * 2;
                var rangeOffset = U16(span, rangeOffsetAt);
                for (var c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph;
                    if (rangeOffset == 0)
                        glyph = (c + delta) & 0xFFFF;
                    else
                    {
                        glyph = U16(span, rangeOffsetAt + rangeOffset + (c - start) * 2);
                        if (glyph != 0)
                            glyph = (glyph + delta) & 0xFFFF;
                    }
                    if (glyph != 0)
                        points.Add(c);
                }
            }
        }

        private static void ReadFormat12(ReadOnlySpan<byte> span, int table, HashSet<int> points)
        {
            var groups = U32(span, table + 12);
            for (var g = 0; g < groups; g++)
            {
                var group = table + 16 + g * 12;
                long start = U32(span, group), end = U32(span, group + 4), glyph = U32(span, group + 8);
                for (var c = start; c <= end && c <= 0x10FFFF; c++, glyph++)
                    if (glyph != 0)
                        points.Add((int)c);
            }
        }

        private static ushort U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16BigEndian(span.Slice(at, 2));

        private static uint U32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32BigEndian(span.Slice(at, 4));
    }
}
