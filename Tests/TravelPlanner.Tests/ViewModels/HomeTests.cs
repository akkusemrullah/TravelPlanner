using System.Globalization;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-106: the new Home - hero title, trip calendar rows, Keşfet cards and the Turkish upper-casing.</summary>
    public class HomeTests
    {
        private static readonly CultureInfo Tr = new("tr-TR");
        private static readonly CultureInfo En = new("en-US");
        private static readonly DateTime Today = new(2026, 10, 28);

        private static Trip Trip(string city, DateTime start, int days, string country = "İtalya", string? name = null) =>
            new() { Name = name ?? city, City = city, Country = country, StartDate = start, EndDate = start.AddDays(days - 1) };

        [Theory]
        [InlineData("Roma", "Roma'ya")]
        [InlineData("Paris", "Paris'e")]
        [InlineData("İstanbul", "İstanbul'a")]
        [InlineData("Kapadokya", "Kapadokya'ya")]
        [InlineData("Berlin", "Berlin'e")]
        [InlineData("Tokyo", "Tokyo'ya")]
        [InlineData("Köln", "Köln'e")]
        [InlineData("NYC", "NYC")]
        public void Dative_FollowsVowelHarmony(string place, string expected) =>
            Assert.Equal(expected, PlaceSuffix.Dative(place, Tr));

        [Theory]
        [InlineData("Roma", "Roma'da")]
        [InlineData("Paris", "Paris'te")]
        [InlineData("Berlin", "Berlin'de")]
        [InlineData("Bangkok", "Bangkok'ta")]
        [InlineData("Ürgüp", "Ürgüp'te")]
        public void Locative_FollowsVowelHarmonyAndHardConsonants(string place, string expected) =>
            Assert.Equal(expected, PlaceSuffix.Locative(place, Tr));

        [Fact]
        public void Suffix_NotAddedInEnglish() => Assert.Equal("Rome", PlaceSuffix.Dative("Rome", En));

        [Theory]
        [InlineData("Gidiş", "GİDİŞ")]
        [InlineData("Dönüş", "DÖNÜŞ")]
        [InlineData("Seyahatlerim", "SEYAHATLERİM")]
        [InlineData("Ilık", "ILIK")]
        public void Upper_UsesTurkishRules(string text, string expected) => Assert.Equal(expected, TextCase.Upper(text, Tr));

        [Fact]
        public void Upper_FollowsTheAppLanguage_NotTheInvariantCulture()
        {
            // tests run in tr-TR (TestCulture.cs): the neutral (Turkish) strings get Turkish casing
            Assert.Equal("tr", TextCase.UiCulture.TwoLetterISOLanguageName);
            Assert.Equal("GİDİŞ", TextCase.Upper("Gidiş"));
            Assert.Equal("GİDİŞ", new UpperExtension { Key = "Ticket_Departure" }.ProvideValue(null!));
        }

        [Fact]
        public void NoXaml_UsesTextTransformUppercase()
        {
            // MAUI upper-cases TextTransform with the invariant culture ("GIDIŞ"); use {helpers:Upper} / UpperText instead
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TravelPlanner.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            var root = dir!.FullName;
            var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => File.ReadAllText(f).Contains("TextTransform", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void HeroTitle_CountsDownToTheTrip()
        {
            var (before, highlight, after) = DashboardViewModel.HeroTitle(Trip("Roma", Today.AddDays(12), 7), Today);
            Assert.Equal("Roma'ya ", before);
            Assert.Equal("12 gün", highlight);
            Assert.Equal(" kaldı", after);
        }

        [Fact]
        public void HeroTitle_DuringTheTrip_ShowsTheDay()
        {
            var (before, highlight, after) = DashboardViewModel.HeroTitle(Trip("Paris", Today.AddDays(-2), 5), Today);
            Assert.Equal("Paris'te 3. gün", before + highlight + after);
            Assert.Equal("3. gün", highlight);
        }

        [Fact]
        public void HeroTitle_WithoutTrip_AsksWhereToGo()
        {
            var (before, highlight, after) = DashboardViewModel.HeroTitle(null, Today);
            Assert.Equal("Bir sonraki durağın neresi?", before);
            Assert.Equal(string.Empty, highlight + after);
        }

        [Fact]
        public void CalendarRow_ComingTrip()
        {
            var row = new HomeTripRow(Trip("Roma", new DateTime(2026, 11, 9), 7), isNext: true, Today, Tr);
            Assert.Equal("09", row.Day);
            Assert.Equal("KAS", row.Month);
            Assert.Equal("Roma", row.Title);
            Assert.Equal("İtalya · 7 gün", row.Sub);
            Assert.Equal("12 gün sonra", row.BadgeText);
            Assert.Equal("accent", row.BadgeTone);
            Assert.True(row.IsNext);
            Assert.False(row.IsLater);
        }

        [Fact]
        public void CalendarRow_FarAndPastTrips()
        {
            var later = new HomeTripRow(Trip("Kapadokya", new DateTime(2026, 12, 18), 3, "Türkiye"), isNext: false, Today, Tr);
            Assert.Equal("ARA", later.Month);
            Assert.Equal("Yaklaşan", later.BadgeText);
            Assert.True(later.IsLater);

            var past = new HomeTripRow(Trip("Ankara", new DateTime(2026, 10, 25), 2, "Türkiye", "Ankara gezisi"), isNext: false, Today.AddDays(5), Tr);
            Assert.Equal("EKİ", past.Month); // Turkish İ, not "EKI"
            Assert.Equal("Ankara gezisi", past.Title);
            Assert.Equal("Ankara, Türkiye · 2 gün", past.Sub);
            Assert.Equal("Tamamlandı", past.BadgeText);
            Assert.Equal("neutral", past.BadgeTone);
            Assert.True(past.IsPast);
        }

        [Fact]
        public void CalendarRow_Tomorrow()
        {
            var row = new HomeTripRow(Trip("Roma", Today.AddDays(1), 2), isNext: true, Today, Tr);
            Assert.Equal("Yarın", row.BadgeText);
        }

        [Fact]
        public void CalendarOrder_ComingFirstThenNewestPast()
        {
            var t = DateTime.Today;
            var past = Trip("Eski", t.AddDays(-40), 2);
            var pastNewer = Trip("Yeni geçmiş", t.AddDays(-10), 2);
            var far = Trip("Uzak", t.AddDays(60), 2);
            var near = Trip("Yakın", t.AddDays(5), 2);
            var order = DashboardViewModel.CalendarOrder(new[] { past, far, pastNewer, near }).ToList();
            Assert.Equal(new[] { near, far, pastNewer, past }, order);
            Assert.Same(near, DashboardViewModel.PickFeatured(order));
            Assert.Null(DashboardViewModel.PickFeatured(new[] { past }));
        }

        [Fact]
        public void PlanCard_ShowsNoBudget()
        {
            var card = new HomePlanCard(new ExploreTripCard
            {
                Title = "Kapadokya'da 3 gün", City = "Nevşehir", Country = "Türkiye", DurationDays = 3, Tags = new() { "nature", "culture" },
            });
            Assert.Equal("3 gün", card.Duration);
            Assert.StartsWith("Nevşehir, Türkiye", card.Sub);
            Assert.DoesNotContain("₺", card.Sub + card.ShortSub);
            Assert.StartsWith("3 gün", card.ShortSub);
        }

        [Fact]
        public void TicketShortDates()
        {
            var ticket = new TripTicket(Trip("Roma", new DateTime(2026, 11, 9), 7, name: "İtalya turu"), Today, Tr);
            Assert.Equal("Kas, Pzt", ticket.StartShort);
            Assert.Equal("Kas, Paz", ticket.EndShort);
            Assert.Equal("İtalya turu", ticket.Caption);
        }
    }
}
