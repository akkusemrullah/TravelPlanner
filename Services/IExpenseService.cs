using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Expense CRUD and budget arithmetic. Implemented by ExpenseService.</summary>
    public interface IExpenseService
    {
        Task<List<Expense>> GetByTripIdAsync(int tripId);
        Task<Expense?> GetByIdAsync(int id);
        Task<int> SaveAsync(Expense expense);
        Task<int> DeleteAsync(Expense expense);

        Task<decimal> GetTotalExpensesAsync(int tripId);

        /// <summary>Sum of every expense across all existing trips - one SQL query (used by the Dashboard).</summary>
        Task<decimal> GetTotalExpensesForAllTripsAsync();

        /// <summary>
        /// Total spent per trip currency across all trips (TP-403) - summing amounts of different currencies
        /// would be meaningless, so the Dashboard converts these into the default currency itself.
        /// </summary>
        Task<Dictionary<string, decimal>> GetTotalExpensesByCurrencyAsync();

        /// <summary>
        /// Switches a trip to <paramref name="toCurrency"/>: re-expresses every expense in it and updates the trip's
        /// CurrencyCode, all in one transaction. Amounts originally typed in <paramref name="toCurrency"/> get their
        /// exact value back.
        /// </summary>
        Task<int> ConvertTripExpensesAsync(int tripId, string fromCurrency, string toCurrency, decimal rate);

        /// <summary>Spending per calendar day, oldest first (TP-404 daily chart).</summary>
        Task<List<DailyExpenseTotal>> GetDailyTotalsAsync(int tripId);

        Task<decimal> GetRemainingBudgetAsync(int tripId, decimal plannedBudget);
        Task<double> GetBudgetUsagePercentageAsync(int tripId, decimal plannedBudget);
        Task<Dictionary<ExpenseCategory, decimal>> GetTotalsByCategoryAsync(int tripId);
        Task<List<ExpenseCategorySummary>> GetCategorySummariesAsync(int tripId);
    }
}
