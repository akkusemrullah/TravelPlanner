using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class ExpenseServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private ExpenseService _service = null!;
        private Trip _trip = null!;

        public async Task InitializeAsync()
        {
            _service = new ExpenseService(_db.Service);
            _trip = new Trip { Name = "Roma", City = "Roma", StartDate = DateTime.Today, EndDate = DateTime.Today.AddDays(3), PlannedBudget = 1000m };
            await (await _db.Connection).InsertAsync(_trip);
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private Task AddExpense(decimal amount, ExpenseCategory category = ExpenseCategory.Food, int? tripId = null) =>
            _service.SaveAsync(new Expense { TripId = tripId ?? _trip.Id, Amount = amount, Category = category });

        [Fact]
        public async Task Totals_AreExact()
        {
            await AddExpense(0.1m);
            await AddExpense(0.2m);

            Assert.Equal(0.3m, await _service.GetTotalExpensesAsync(_trip.Id));
        }

        [Fact]
        public async Task Total_IsZero_WhenTripHasNoExpenses()
        {
            Assert.Equal(0m, await _service.GetTotalExpensesAsync(_trip.Id));
        }

        [Fact]
        public async Task RemainingBudget_AndUsagePercentage()
        {
            await AddExpense(250m);
            await AddExpense(150m);

            Assert.Equal(600m, await _service.GetRemainingBudgetAsync(_trip.Id, 1000m));
            Assert.Equal(40d, await _service.GetBudgetUsagePercentageAsync(_trip.Id, 1000m), precision: 6);
        }

        [Fact]
        public async Task UsagePercentage_IsZero_WhenNoBudget()
        {
            await AddExpense(50m);

            Assert.Equal(0d, await _service.GetBudgetUsagePercentageAsync(_trip.Id, 0m));
        }

        [Fact]
        public async Task TotalForAllTrips_IgnoresExpensesOfDeletedTrips()
        {
            await AddExpense(100m);
            await AddExpense(999m, tripId: 12345); // orphan - its trip doesn't exist

            Assert.Equal(100m, await _service.GetTotalExpensesForAllTripsAsync());
        }

        [Fact]
        public async Task CategorySummaries_AreGroupedAndSortedByTotal()
        {
            await AddExpense(20m, ExpenseCategory.Food);
            await AddExpense(30m, ExpenseCategory.Food);
            await AddExpense(200m, ExpenseCategory.Accommodation);

            var summaries = await _service.GetCategorySummariesAsync(_trip.Id);

            Assert.Collection(summaries,
                s => { Assert.Equal(ExpenseCategory.Accommodation, s.Category); Assert.Equal(200m, s.Total); Assert.Equal(1, s.Count); },
                s => { Assert.Equal(ExpenseCategory.Food, s.Category); Assert.Equal(50m, s.Total); Assert.Equal(2, s.Count); });

            var byCategory = await _service.GetTotalsByCategoryAsync(_trip.Id);
            Assert.Equal(50m, byCategory[ExpenseCategory.Food]);
        }

        [Fact]
        public async Task Save_UpdatesExistingExpense_AndDeleteRemovesIt()
        {
            var expense = new Expense { TripId = _trip.Id, Amount = 10m };
            await _service.SaveAsync(expense);

            expense.Amount = 15m;
            await _service.SaveAsync(expense);

            var reloaded = await _service.GetByIdAsync(expense.Id);
            Assert.Equal(15m, reloaded!.Amount);
            Assert.Single(await _service.GetByTripIdAsync(_trip.Id));

            await _service.DeleteAsync(expense);
            Assert.Empty(await _service.GetByTripIdAsync(_trip.Id));
        }
    }
}
