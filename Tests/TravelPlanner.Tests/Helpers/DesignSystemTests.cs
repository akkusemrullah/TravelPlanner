using System.Text.RegularExpressions;
using TravelPlanner.Helpers;
using Xunit;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// TP-96: guards for the design system files - every icon key has a glyph in the subset Lucide font, every colour
    /// token exists in both themes (and the static light defaults), and every StaticResource / DynamicResource a page
    /// asks for is defined somewhere (a missing key only shows up at runtime on the device).
    /// </summary>
    public class DesignSystemTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TravelPlanner.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static HashSet<string> ColorKeys(string xaml) =>
            Regex.Matches(xaml, "<Color x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

        [Fact]
        public void EveryIconKey_HasAGlyphInTheFont()
        {
            var font = FontCoverage.FromFont(File.ReadAllBytes(Path.Combine(RepoRoot(), "Resources", "Fonts", "Lucide.ttf")));
            var icons = Regex.Matches(Read("Resources", "Styles", "Icons.xaml"), "x:Key=\"(Ic\\.[^\"]+)\">&#x([0-9a-fA-F]+);");

            Assert.NotEmpty(icons);
            var missing = icons.Where(m => !font.Has(Convert.ToInt32(m.Groups[2].Value, 16))).Select(m => m.Groups[1].Value).ToList();
            Assert.Empty(missing);
        }

        [Fact]
        public void ThemeTokens_AreTheSameInLightDarkAndDefaults()
        {
            var light = ColorKeys(Read("Resources", "Themes", "LightTheme.xaml"));
            var dark = ColorKeys(Read("Resources", "Themes", "DarkTheme.xaml"));
            var defaults = ColorKeys(Read("Resources", "Styles", "DesignTokens.xaml"));

            Assert.Empty(light.Except(dark));
            Assert.Empty(dark.Except(light));
            Assert.Empty(light.Except(defaults));
            Assert.Contains("tp-ticket-start", light);
            Assert.Contains("tp-ticket-end", light);
        }

        [Fact]
        public void EveryResourceAPageUses_IsDefined()
        {
            var root = RepoRoot();
            var xamlFiles = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                            && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .ToList();
            var all = xamlFiles.Select(File.ReadAllText).ToList();

            var defined = all.SelectMany(x => Regex.Matches(x, "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value)).ToHashSet();
            var used = all.SelectMany(x => Regex.Matches(x, "\\{(?:Static|Dynamic)Resource ([^}\\s]+)").Select(m => m.Groups[1].Value)).ToHashSet();

            Assert.Empty(used.Where(key => !defined.Contains(key)).OrderBy(k => k));
        }
    }
}
