using Xunit;
using TravelPlanner.Models;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-98: the new trip list (chip counts, row texts) and the trip page's row summaries.</summary>
    public class TripListAndDetailDesignTests
    {
        private static Trip TripOn(int startInDays, int endInDays, string country = "İtalya", decimal budget = 0) =>
            new() { Name = "Gezi", City = "Roma", Country = country, StartDate = DateTime.Today.AddDays(startInDays), EndDate = DateTime.Today.AddDays(endInDays), PlannedBudget = budget };

        [Fact]
        public void CountFor_CountsEachStatusAndAll()
        {
            var trips = new[] { TripOn(10, 12), TripOn(20, 22), TripOn(-1, 1), TripOn(-30, -28) };

            Assert.Equal(4, TripListFilter.CountFor(trips, null));
            Assert.Equal(2, TripListFilter.CountFor(trips, TripStatus.Upcoming));
            Assert.Equal(1, TripListFilter.CountFor(trips, TripStatus.Ongoing));
            Assert.Equal(1, TripListFilter.CountFor(trips, TripStatus.Completed));
        }

        [Fact]
        public void Row_ShowsCityAndCountry_AndADashWithoutBudget()
        {
            Assert.Equal("Roma, İtalya", TripOn(1, 2).PlaceDisplay);
            Assert.Equal("Roma", TripOn(1, 2, country: " ").PlaceDisplay);
            Assert.Equal("—", TripOn(1, 2).BudgetOrDash);
            Assert.NotEqual("—", TripOn(1, 2, budget: 500).BudgetOrDash);
        }

        [Fact]
        public void Summaries_AreEmptyWhenThereIsNothing()
        {
            Assert.Equal(string.Empty, TripDetailSummary.Activities(0));
            Assert.Equal(string.Empty, TripDetailSummary.Tasks(Array.Empty<TodoItem>()));
            Assert.Equal(string.Empty, TripDetailSummary.Places(Array.Empty<Place>()));
            Assert.Equal(string.Empty, TripDetailSummary.Expenses(0, "0 ₺"));
            Assert.Equal(string.Empty, TripDetailSummary.Files(0));
        }

        [Fact]
        public void Summaries_CountWhatIsThere()
        {
            Assert.Equal("12 etkinlik", TripDetailSummary.Activities(12));
            Assert.Equal("2 bekleyen", TripDetailSummary.Tasks(new[] { new TodoItem(), new TodoItem(), new TodoItem { IsCompleted = true } }));
            Assert.Equal("Hepsi tamam", TripDetailSummary.Tasks(new[] { new TodoItem { IsCompleted = true } }));
            Assert.Equal("1/3 ziyaret edildi", TripDetailSummary.Places(new[] { new Place { IsVisited = true }, new Place(), new Place() }));
            Assert.Equal("8 harcama · 4.250 ₺", TripDetailSummary.Expenses(8, "4.250 ₺"));
            Assert.Equal("3 dosya", TripDetailSummary.Files(3));
        }

        [Theory]
        [InlineData(50, false, false)]
        [InlineData(80, false, true)]
        [InlineData(99, false, true)]
        [InlineData(120, true, false)] // over the budget: red, not amber
        public void BudgetBar_TurnsAmberFrom80Percent(double usage, bool over, bool warning) =>
            Assert.Equal(warning, TripDetailSummary.IsBudgetWarning(usage, over));
    }
}
