using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    /// <summary>TP-74: the PDF leaves out what its font can't draw (emoji, Japanese …) instead of printing boxes.</summary>
    public class FontCoverageTests
    {
        private static FontCoverage Inter()
        {
            var dir = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(dir, "TravelPlanner.sln")))
                dir = Path.GetDirectoryName(dir)!;
            var fonts = Path.Combine(dir, "Resources", "Fonts");
            return FontCoverage.Common(
                FontCoverage.FromFont(File.ReadAllBytes(Path.Combine(fonts, "Inter-Regular.ttf"))),
                FontCoverage.FromFont(File.ReadAllBytes(Path.Combine(fonts, "Inter-Bold.ttf"))));
        }

        [Fact]
        public void Inter_DrawsTurkishAndTheLiraSign()
        {
            var inter = Inter();
            Assert.True(inter.Count > 300, $"only {inter.Count}");
            foreach (var c in "ğüşıöçĞÜŞİÖÇ₺€$ab09·“”")
                Assert.True(inter.Has(c), $"missing {c}");
        }

        [Theory]
        [InlineData("Ankara 🏖️ plajı", "Ankara plajı")]
        [InlineData("東京 Tokyo", "Tokyo")]
        [InlineData("Müze bileti · ₺120", "Müze bileti · ₺120")]
        [InlineData("Satır 1\nSatır 2 😀", "Satır 1\nSatır 2")]
        public void Clean_DropsWhatTheFontCannotDraw(string text, string expected) =>
            Assert.Equal(expected, Inter().Clean(text));

        [Fact]
        public void UnreadableFont_LeavesTextAlone()
        {
            var broken = FontCoverage.FromFont(new byte[] { 1, 2, 3 });
            Assert.Equal("😀 x", broken.Clean("😀 x"));
        }
    }
}
