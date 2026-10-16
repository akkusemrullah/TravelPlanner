using System.Globalization;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class TripListFilterTests
    {
        private static readonly CultureInfo Turkish = new("tr-TR");

        private static List<Trip> Trips() => new()
        {
            new Trip { Id = 1, Name = "Yaz tatili", City = "İstanbul", Country = "Türkiye", StartDate = DateTime.Today.AddDays(30), EndDate = DateTime.Today.AddDays(35), PlannedBudget = 5000m },
            new Trip { Id = 2, Name = "Oktoberfest", City = "Münih", Country = "Almanya", StartDate = DateTime.Today.AddDays(-2), EndDate = DateTime.Today.AddDays(2), PlannedBudget = 900m },
            new Trip { Id = 3, Name = "Antik kentler", City = "Roma", Country = "İtalya", StartDate = DateTime.Today.AddDays(-60), EndDate = DateTime.Today.AddDays(-55), PlannedBudget = 20000m },
        };

        [Theory]
        [InlineData("istanbul", 1)]   // Turkish dotted İ, case-insensitive
        [InlineData("munih", 2)]      // accent-insensitive
        [InlineData("ALMANYA", 2)]    // matches the country too
        [InlineData("antik", 3)]      // and the trip name
        public void Search_MatchesNameCityOrCountry(string query, int expectedId)
        {
            var result = TripListFilter.Apply(Trips(), query, null, TripSortOption.DateAscending, culture: Turkish);

            Assert.Equal(expectedId, Assert.Single(result).Id);
        }

        [Theory]
        [InlineData(TripStatus.Upcoming, 1)]
        [InlineData(TripStatus.Ongoing, 2)]
        [InlineData(TripStatus.Completed, 3)]
        public void StatusFilter(TripStatus status, int expectedId)
        {
            Assert.Equal(expectedId, Assert.Single(TripListFilter.Apply(Trips(), null, status, TripSortOption.DateAscending, culture: Turkish)).Id);
        }

        [Fact]
        public void Sorting()
        {
            int[] Ids(TripSortOption sort) => TripListFilter.Apply(Trips(), "", null, sort, culture: Turkish).Select(t => t.Id).ToArray();

            Assert.Equal(new[] { 3, 2, 1 }, Ids(TripSortOption.DateAscending));
            Assert.Equal(new[] { 1, 2, 3 }, Ids(TripSortOption.DateDescending));
            Assert.Equal(new[] { 3, 1, 2 }, Ids(TripSortOption.BudgetDescending));
            Assert.Equal(new[] { 2, 1, 3 }, Ids(TripSortOption.BudgetAscending));
            Assert.Equal(new[] { 3, 2, 1 }, Ids(TripSortOption.NameAscending)); // Antik, Oktoberfest, Yaz
        }

        [Fact]
        public void BudgetSort_CanCompareAcrossCurrencies()
        {
            var trips = new List<Trip>
            {
                new() { Id = 1, Name = "A", City = "A", PlannedBudget = 1000m, CurrencyCode = "EUR" },  // ≈ 37 500 TRY
                new() { Id = 2, Name = "B", City = "B", PlannedBudget = 20000m, CurrencyCode = "TRY" },
            };

            var raw = TripListFilter.Apply(trips, null, null, TripSortOption.BudgetDescending, culture: Turkish);
            var converted = TripListFilter.Apply(trips, null, null, TripSortOption.BudgetDescending,
                t => t.CurrencyCode == "EUR" ? t.PlannedBudget * 37.5m : t.PlannedBudget, Turkish);

            Assert.Equal(2, raw[0].Id);
            Assert.Equal(1, converted[0].Id);
        }

        [Fact]
        public async Task ViewModel_SearchAndChips_UpdateTheVisibleList_AndEmptyStates()
        {
            var service = new Mock<ITripService>();
            service.Setup(s => s.GetAllAsync()).ReturnsAsync(Trips());
            var vm = new TripListViewModel(service.Object, new Mock<IExchangeRateService>().Object, Fakes.Dialogs().Object);
            await vm.LoadTripsCommand.ExecuteAsync(null);
            Assert.Equal(3, vm.Trips.Count);
            Assert.True(vm.ShowFilterBar);

            vm.SelectStatusFilterCommand.Execute(vm.StatusFilters.Single(f => f.Status == TripStatus.Completed));
            Assert.Equal(3, Assert.Single(vm.Trips).Id);

            vm.SearchText = "paris";
            Assert.Empty(vm.Trips);
            Assert.True(vm.HasNoResults);
            Assert.False(vm.HasNoTrips); // trips exist - this is "no results", not the first-run empty state

            vm.ClearFiltersCommand.Execute(null);
            Assert.Equal(3, vm.Trips.Count);
            Assert.True(vm.StatusFilters[0].IsSelected);
        }
    }
}
