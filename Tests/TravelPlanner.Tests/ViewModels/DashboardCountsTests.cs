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
    }
}
