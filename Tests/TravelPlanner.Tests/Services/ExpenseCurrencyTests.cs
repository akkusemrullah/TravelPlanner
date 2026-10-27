using Moq;
using Xunit;
using SQLite;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    [Collection("Currency")] // relies on the default currency being TRY
    public class ExpenseCurrencyTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private ExpenseService _expenses = null!;
        private TripService _trips = null!;

        public Task InitializeAsync()
        {
            MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;
            _expenses = new ExpenseService(_db.Service);
            _trips = new TripService(_db.Service, _expenses, new Mock<INotificationService>().Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private async Task<Trip> NewTripAsync(string? currency, decimal budget = 0m)
        {
            var trip = new Trip { Name = "T", City = "C", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22), PlannedBudget = budget, CurrencyCode = currency };
            await _trips.SaveAsync(trip);
            return trip;
        }

        [Fact]
        public async Task LoadedExpenses_KnowTheirTripsCurrency()
        {
            var trip = await NewTripAsync("EUR");
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 12.5m });

            var expense = Assert.Single(await _expenses.GetByTripIdAsync(trip.Id));

            Assert.Equal("EUR", expense.TripCurrencyCode);
            Assert.StartsWith("€", expense.FormattedAmount.TrimStart());
            Assert.Equal("EUR", (await _expenses.GetByIdAsync(expense.Id))!.TripCurrencyCode);
        }

        [Fact]
        public async Task LegacyTripsWithoutCurrency_UseTheDefault()
        {
            var trip = await NewTripAsync(null);
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 1m });

            Assert.Equal("TRY", (await _expenses.GetByTripIdAsync(trip.Id)).Single().TripCurrencyCode);
            Assert.Equal("TRY", trip.EffectiveCurrencyCode);
        }

        [Fact]
        public async Task TotalsByCurrency_GroupPerTripCurrency_AndMergeNullIntoTheDefault()
        {
            var lira = await NewTripAsync("TRY");
            var legacy = await NewTripAsync(null);
            var euro = await NewTripAsync("EUR");
            await _expenses.SaveAsync(new Expense { TripId = lira.Id, Amount = 100m });
            await _expenses.SaveAsync(new Expense { TripId = legacy.Id, Amount = 50m });
            await _expenses.SaveAsync(new Expense { TripId = euro.Id, Amount = 20m });

            var totals = await _expenses.GetTotalExpensesByCurrencyAsync();

            Assert.Equal(2, totals.Count);
            Assert.Equal(150m, totals["TRY"]);
            Assert.Equal(20m, totals["EUR"]);
        }

        [Fact]
        public async Task TP69_MyTotals_SameAsAllForASoloTrip()
        {
            var lira = await NewTripAsync("TRY");
            await _expenses.SaveAsync(new Expense { TripId = lira.Id, Amount = 100m });

            Assert.Equal(100m, (await _expenses.GetMyTotalsByCurrencyAsync(null))["TRY"]);
        }

        [Fact]
        public async Task TP104_MyTotalForTrip_CountsOnlyThatTrip()
        {
            var lira = await NewTripAsync("TRY");
            var other = await NewTripAsync("TRY");
            await _expenses.SaveAsync(new Expense { TripId = lira.Id, Amount = 100m });
            await _expenses.SaveAsync(new Expense { TripId = lira.Id, Amount = 25.5m });
            await _expenses.SaveAsync(new Expense { TripId = other.Id, Amount = 40m });

            Assert.Equal(125.5m, await _expenses.GetMyTotalForTripAsync(lira.Id, null));
            Assert.Equal(0m, await _expenses.GetMyTotalForTripAsync(999, null));
        }

        [Theory]
        [InlineData(new[] { 1, 2, 3 }, 1, 334)]   // my share of 10,00 split three ways (the first gets the extra kuruş)
        [InlineData(new[] { 1, 2, 3 }, 2, 333)]
        [InlineData(new[] { 2, 3 }, 1, 0)]          // split without me
        public void TP69_MyPart_OfASplitExpense_IsMyShare(int[] members, int me, long expected)
        {
            var expense = new Expense { AmountMinor = 1000, CreatedBy = "other" };
            Assert.Equal(expected, ExpenseService.MyPartMinor(expense, members, me, "me"));
        }

        [Theory]
        [InlineData("me", 1000)]
        [InlineData(null, 1000)]     // entered before accounts / as a guest
        [InlineData("other", 0)]     // another member's unsplit expense
        public void TP69_MyPart_OfAnUnsplitExpense_DependsOnWhoEnteredIt(string? createdBy, long expected)
        {
            var expense = new Expense { AmountMinor = 1000, CreatedBy = createdBy };
            Assert.Equal(expected, ExpenseService.MyPartMinor(expense, null, 1, "me"));
            Assert.Equal(1000, ExpenseService.MyPartMinor(expense, null, null, null)); // guest: everything is theirs
        }

        [Fact]
        public async Task ConvertTripExpenses_KeepsOriginals_AndRestoresExactAmountsTypedInTheNewCurrency()
        {
            var trip = await NewTripAsync("TRY");
            var typedInLira = new Expense { TripId = trip.Id, Amount = 370m };                 // pre-TP-403 style (no original)
            var typedInEuro = new Expense { TripId = trip.Id, Amount = 740m, OriginalAmount = 20m, OriginalCurrencyCode = "EUR", ExchangeRate = 37 };
            await _expenses.SaveAsync(typedInLira);
            await _expenses.SaveAsync(typedInEuro);

            await _expenses.ConvertTripExpensesAsync(trip.Id, "TRY", "EUR", 0.027m);

            var lira = (await _expenses.GetByIdAsync(typedInLira.Id))!;
            var euro = (await _expenses.GetByIdAsync(typedInEuro.Id))!;

            Assert.Equal(9.99m, lira.Amount);             // 370 * 0.027
            Assert.Equal(370m, lira.OriginalAmount);
            Assert.Equal("TRY", lira.OriginalCurrencyCode);

            Assert.Equal(20m, euro.Amount);               // exact original, not 740 * 0.027 = 19.98
            Assert.Equal(1d, euro.ExchangeRate);

            // The trip's currency switched in the same transaction.
            Assert.Equal("EUR", (await _trips.GetByIdAsync(trip.Id))!.CurrencyCode);
            Assert.Equal("EUR", lira.TripCurrencyCode);
        }

        [Fact]
        public async Task DailyTotals_AreGroupedByDay()
        {
            var trip = await NewTripAsync("TRY");
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 10m, Date = new DateTime(2026, 8, 21, 9, 0, 0) });
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 5m, Date = new DateTime(2026, 8, 21, 20, 0, 0) });
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 7m, Date = new DateTime(2026, 8, 20) });

            var daily = await _expenses.GetDailyTotalsAsync(trip.Id);

            Assert.Equal(2, daily.Count);
            Assert.Equal(new DateTime(2026, 8, 20), daily[0].Date);
            Assert.Equal(15m, daily[1].Total);
            Assert.Equal(2, daily[1].Count);
        }

        [Fact]
        public void ConvertedExpense_ShowsItsOriginalAmount()
        {
            var expense = new Expense { Amount = 1850m, OriginalAmount = 50m, OriginalCurrencyCode = "EUR", TripCurrencyCode = "TRY" };
            var plain = new Expense { Amount = 10m, OriginalAmount = 10m, OriginalCurrencyCode = "TRY", TripCurrencyCode = "TRY" };

            Assert.True(expense.IsConverted);
            Assert.StartsWith("€", expense.FormattedOriginalAmount.TrimStart());
            Assert.False(plain.IsConverted);
        }
    }

    public class Migration2Tests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();

        [Fact]
        public async Task LegacyTrips_ArePinnedToTheConfiguredDefaultCurrency()
        {
            using (var legacy = new SQLiteConnection(_db.Path))
            {
                legacy.Execute("CREATE TABLE Trip (Id integer primary key autoincrement not null, Name varchar, Country varchar, City varchar, StartDate bigint, EndDate bigint, PlannedBudgetMinor bigint, Description varchar)");
                legacy.Execute("INSERT INTO Trip (Name, City, StartDate, EndDate, PlannedBudgetMinor) VALUES ('Paris', 'Paris', 0, 0, 100)");
                legacy.Execute("PRAGMA user_version = 1");
            }

            var service = new DatabaseService(_db.Path, "eur");
            var connection = await service.Connection;

            Assert.Equal("EUR", (await connection.Table<Trip>().FirstAsync()).CurrencyCode);
            await connection.CloseAsync();
        }

        public Task InitializeAsync() => _db.InitializeAsync();

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task ExistingExpenses_GetTheirOriginalAmountAndRateFilledIn()
        {
            // A version-1 database (money already in minor units, no multi-currency columns yet).
            using (var legacy = new SQLiteConnection(_db.Path))
            {
                legacy.Execute("CREATE TABLE Trip (Id integer primary key autoincrement not null, Name varchar, Country varchar, City varchar, StartDate bigint, EndDate bigint, PlannedBudgetMinor bigint, Description varchar)");
                legacy.Execute("CREATE TABLE Expense (Id integer primary key autoincrement not null, TripId integer, Category integer, AmountMinor bigint, Date bigint, Description varchar)");
                legacy.Execute("INSERT INTO Trip (Name, City, StartDate, EndDate, PlannedBudgetMinor) VALUES ('Roma', 'Roma', 0, 0, 100000)");
                legacy.Execute("INSERT INTO Expense (TripId, Category, AmountMinor, Date) VALUES (1, 2, 1234, 0)");
                legacy.Execute("PRAGMA user_version = 1");
            }

            var connection = await _db.Connection;
            var expense = await connection.Table<Expense>().FirstAsync();
            var trip = await connection.Table<Trip>().FirstAsync();

            Assert.Equal("TRY", trip.CurrencyCode); // pinned to the default currency it was planned in
            Assert.Equal(12.34m, expense.OriginalAmount);
            Assert.Equal(1d, expense.ExchangeRate);
            Assert.Null(expense.OriginalCurrencyCode); // = "the trip's currency"
            Assert.Equal(DatabaseService.LatestSchemaVersion, await connection.ExecuteScalarAsync<int>("PRAGMA user_version"));
            Assert.True(DatabaseService.LatestSchemaVersion >= 2); // later migrations (3 = cloud sync, G1) run after it
        }
    }
}
