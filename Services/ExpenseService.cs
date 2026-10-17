using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class ExpenseService : IExpenseService
    {
        private readonly IDatabaseService _databaseService;

        public ExpenseService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<Expense>> GetByTripIdAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            var expenses = await db.Table<Expense>()
                .Where(e => e.TripId == tripId)
                .OrderByDescending(e => e.Date)
                .ToListAsync();

            var currency = await GetTripCurrencyAsync(db, tripId);
            foreach (var expense in expenses)
                expense.TripCurrencyCode = currency;

            return expenses;
        }

        public async Task<Expense?> GetByIdAsync(int id)
        {
            var db = await _databaseService.Connection;
            var expense = await db.Table<Expense>().Where(e => e.Id == id).FirstOrDefaultAsync();
            if (expense is not null)
                expense.TripCurrencyCode = await GetTripCurrencyAsync(db, expense.TripId);
            return expense;
        }

        // Effective currency (stored code, or the user's default for trips created before TP-403).
        private static async Task<string> GetTripCurrencyAsync(SQLite.SQLiteAsyncConnection db, int tripId)
        {
            var code = await db.ExecuteScalarAsync<string>("SELECT CurrencyCode FROM Trip WHERE Id = ?", tripId);
            return MoneyFormatter.Normalize(code);
        }

        public async Task<int> SaveAsync(Expense expense)
        {
            Validate(expense);
            var db = await _databaseService.Connection;
            // TP-11: the amount is in the trip's currency (the form sets the one it converted into).
            expense.AmountCurrencyCode ??= await GetTripCurrencyAsync(db, expense.TripId);
            return await db.SaveTrackedAsync(expense);
        }

        public async Task<int> DeleteAsync(Expense expense)
        {
            var db = await _databaseService.Connection;
            var deleted = 0;
            var expenseId = expense.Id;
            await db.RunInTransactionAsync(conn =>
            {
                // The expense and its split shares go to the trash together (restored together).
                var batch = TrashBatch.For(expense);
                conn.DeleteAllTracked<ExpenseShare>(s => s.ExpenseId == expenseId, batch); // TP-504
                deleted = conn.DeleteTracked(expense, batch);
            });
            return deleted;
        }

        /// <summary>
        /// Last line of defence behind the expense form: amounts and rate within <see cref="InputLimits"/>
        /// (so SQL SUMs and conversions can't overflow), a sane date, text trimmed to its column limits.
        /// </summary>
        internal static void Validate(Expense expense)
        {
            ArgumentNullException.ThrowIfNull(expense);
            InputLimits.EnforceTextLimits(expense);
            expense.Description = TextCase.CapitalizeFirst(expense.Description);

            if (!InputLimits.IsValidMinor(expense.AmountMinor) || !InputLimits.IsValidMinor(expense.OriginalAmountMinor))
                throw new ArgumentOutOfRangeException(nameof(expense), "Expense amount is outside the supported range.");
            if (!InputLimits.IsValidRate(expense.ExchangeRate))
                throw new ArgumentOutOfRangeException(nameof(expense), "Exchange rate is outside the supported range.");
            if (!InputLimits.IsValidDate(expense.Date))
                throw new ArgumentOutOfRangeException(nameof(expense), "Expense date is outside the supported range.");
        }

        // ---- Budget calculations ----
        // Totals are summed by SQLite over the integer minor-unit column (exact, and no need to load
        // every row just to add them up).

        public async Task<decimal> GetTotalExpensesAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            var minor = await db.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(AmountMinor), 0) FROM Expense WHERE TripId = ?", tripId);
            return MoneyConverter.FromMinor(minor);
        }

        public async Task<decimal> GetTotalExpensesForAllTripsAsync()
        {
            var db = await _databaseService.Connection;
            var minor = await db.ExecuteScalarAsync<long>(
                "SELECT COALESCE(SUM(AmountMinor), 0) FROM Expense WHERE TripId IN (SELECT Id FROM Trip)");
            return MoneyConverter.FromMinor(minor);
        }

        public async Task<Dictionary<string, decimal>> GetTotalExpensesByCurrencyAsync()
        {
            var db = await _databaseService.Connection;
            var rows = await db.QueryAsync<CurrencyTotalRow>(
                "SELECT t.CurrencyCode AS CurrencyCode, COALESCE(SUM(e.AmountMinor), 0) AS TotalMinor " +
                "FROM Expense e INNER JOIN Trip t ON t.Id = e.TripId GROUP BY t.CurrencyCode");

            // NULL (pre-TP-403 trips) means "the default currency" - merge it with explicit rows of that code.
            var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var code = MoneyFormatter.Normalize(row.CurrencyCode);
                totals[code] = (totals.TryGetValue(code, out var existing) ? existing : 0m) + MoneyConverter.FromMinor(row.TotalMinor);
            }

            return totals;
        }

        public async Task<int> ConvertTripExpensesAsync(int tripId, string fromCurrency, string toCurrency, decimal rate)
        {
            if (!InputLimits.IsValidRate(rate))
                throw new ArgumentOutOfRangeException(nameof(rate));

            var db = await _databaseService.Connection;
            var changed = 0;

            await db.RunInTransactionAsync(conn =>
            {
                foreach (var expense in conn.Table<Expense>().Where(e => e.TripId == tripId).ToList())
                {
                    // Keep what the user originally typed; only the trip-currency amount and rate change.
                    if (string.IsNullOrWhiteSpace(expense.OriginalCurrencyCode))
                    {
                        expense.OriginalCurrencyCode = fromCurrency;
                        expense.OriginalAmountMinor = expense.AmountMinor;
                    }

                    if (string.Equals(expense.OriginalCurrencyCode, toCurrency, StringComparison.OrdinalIgnoreCase))
                    {
                        expense.AmountMinor = expense.OriginalAmountMinor;
                        expense.ExchangeRate = 1d;
                    }
                    else
                    {
                        var converted = Math.Round(expense.Amount * rate, 2, MidpointRounding.AwayFromZero);
                        // Throwing here rolls the whole conversion back - no trip ends up half converted.
                        if (!InputLimits.IsValidAmount(converted))
                            throw new ArgumentOutOfRangeException(nameof(rate), "A converted expense exceeds the supported amount.");

                        expense.Amount = converted;
                        var newRate = (expense.ExchangeRate <= 0 ? 1d : expense.ExchangeRate) * (double)rate;
                        expense.ExchangeRate = InputLimits.IsValidRate(newRate) ? newRate : (double)rate;
                    }

                    expense.AmountCurrencyCode = toCurrency; // TP-11

                    changed += conn.UpdateTracked(expense);
                }

                // Same transaction: the trip's currency and its expenses can never disagree.
                if (conn.Find<Trip>(tripId) is { } trip)
                {
                    trip.CurrencyCode = toCurrency;
                    conn.UpdateTracked(trip);
                }
            });

            return changed;
        }

        public async Task<List<DailyExpenseTotal>> GetDailyTotalsAsync(int tripId)
        {
            var expenses = await GetByTripIdAsync(tripId);
            return expenses
                .GroupBy(e => e.Date.Date)
                .Select(g => new DailyExpenseTotal { Date = g.Key, Total = g.Sum(e => e.Amount), Count = g.Count() })
                .OrderBy(d => d.Date)
                .ToList();
        }

        public async Task<decimal> GetRemainingBudgetAsync(int tripId, decimal plannedBudget)
        {
            var total = await GetTotalExpensesAsync(tripId);
            return plannedBudget - total;
        }

        public async Task<double> GetBudgetUsagePercentageAsync(int tripId, decimal plannedBudget)
        {
            if (plannedBudget <= 0)
                return 0;

            var total = await GetTotalExpensesAsync(tripId);
            return (double)(total / plannedBudget) * 100;
        }

        // ---- Grouping by category ----

        /// <summary>Simple category -> total map, handy for quick lookups.</summary>
        public async Task<Dictionary<ExpenseCategory, decimal>> GetTotalsByCategoryAsync(int tripId)
        {
            var expenses = await GetByTripIdAsync(tripId);
            return expenses
                .GroupBy(e => e.Category)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));
        }

        /// <summary>Richer per-category breakdown (total + count), ready for a summary list or chart.</summary>
        public async Task<List<ExpenseCategorySummary>> GetCategorySummariesAsync(int tripId)
        {
            var expenses = await GetByTripIdAsync(tripId);
            return expenses
                .GroupBy(e => e.Category)
                .Select(g => new ExpenseCategorySummary
                {
                    Category = g.Key,
                    Total = g.Sum(e => e.Amount),
                    Count = g.Count()
                })
                .OrderByDescending(s => s.Total)
                .ToList();
        }
    }

    /// <summary>Row shape for GetTotalExpensesByCurrencyAsync's GROUP BY query - not a DB entity.</summary>
    public class CurrencyTotalRow
    {
        public string? CurrencyCode { get; set; }
        public long TotalMinor { get; set; }
    }

    /// <summary>One day's spending, used by the expense analysis chart (TP-404) - not a DB entity.</summary>
    public class DailyExpenseTotal
    {
        public DateTime Date { get; set; }
        public decimal Total { get; set; }
        public int Count { get; set; }
    }

    /// <summary>Projection used by ExpenseService.GetCategorySummariesAsync - not a DB entity.</summary>
    public class ExpenseCategorySummary
    {
        public ExpenseCategory Category { get; set; }
        public decimal Total { get; set; }
        public int Count { get; set; }
    }
}
