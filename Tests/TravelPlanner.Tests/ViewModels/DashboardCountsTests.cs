using Xunit;
using TravelPlanner.Models;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class DashboardCountsTests
    {
        private static readonly DateTime Today = new(2026, 10, 19);

        private static Trip Trip(string city, string country, DateTime end) =>
            new() { Name = city, City = city, Country = country, StartDate = end.AddDays(-2), EndDate = end };

        [Fact]
        public void VisitedCities_CountsFinishedTrips_OncePerCityAndCountry()
        {
            var trips = new[]
            {
                Trip("Roma", "İtalya", Today.AddDays(-30)),
                Trip(" roma ", "İtalya", Today.AddDays(-10)),       // same city again
                Trip("Paris", "Fransa", Today.AddDays(-5)),
                Trip("Paris", "ABD", Today.AddDays(-3)),            // another Paris
                Trip("Bari", "İtalya", Today.AddDays(5)),           // not over yet
                Trip("Bari", "İtalya", Today),                      // ends today: not over yet
                Trip(" ", "İtalya", Today.AddDays(-1)),             // no city
            };

            Assert.Equal(3, DashboardViewModel.CountVisitedCities(trips, Today));
        }

        [Fact]
        public void VisitedCountries_OneCountryWrittenDifferently_CountsOnce()
        {
            var trips = new[]
            {
                Trip("İstanbul", "Türkiye Cumhuriyeti", Today.AddDays(-20)),
                Trip("Nevşehir", "Türkiye", Today.AddDays(-10)),
                Trip("Ankara", "turkey", Today.AddDays(-5)),
                Trip("Girne", "Kuzey Kıbrıs Türk Cumhuriyeti", Today.AddDays(-4)), // not Türkiye
                Trip("Roma", "İtalya", Today.AddDays(-3)),
                Trip("Milano", "italya ", Today.AddDays(-2)),
                Trip("Paris", "Fransa", Today.AddDays(3)),                         // not over yet
            };

            Assert.Equal(3, DashboardViewModel.CountVisitedCountries(trips, Today));
        }

        [Theory]
        [InlineData("Türkiye Cumhuriyeti", "Türkiye")]
        [InlineData("Turkey (Türkiye)", "Türkiye")]
        [InlineData(" İtalya ", "İtalya")]
        [InlineData("Kuzey Kıbrıs Türk Cumhuriyeti", "Kuzey Kıbrıs Türk Cumhuriyeti")]
        public void CountryNames_Tidy_ShortensTurkiye(string input, string expected)
        {
            Assert.Equal(expected, Helpers.CountryNames.Tidy(input));
        }
    }
}
