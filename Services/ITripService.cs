using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Trip CRUD plus the per-trip budget figures the UI shows. Implemented by TripService.</summary>
    public interface ITripService
    {
        Task<List<Trip>> GetAllAsync();
        Task<Trip?> GetByIdAsync(int id);

        /// <summary>Inserts or updates the trip and (re)schedules its local notifications.</summary>
        Task<int> SaveAsync(Trip trip);

        /// <summary>How many to-dos, expenses, places and plan activities belong to the trip (shown before deleting it).</summary>
        Task<(int Todos, int Expenses, int Places, int Activities)> GetRelatedCountsAsync(int tripId);

        /// <summary>Deletes the trip and everything that belongs to it in one transaction.</summary>
        Task<int> DeleteAsync(Trip trip);

        /// <summary>
        /// Copies a trip into a new one named <paramref name="newName"/> (one transaction): destination, dates,
        /// budget, currency and description, plus its to-dos (unticked, unassigned), places (not visited), day plan
        /// (place links kept) and participants (without account links). Expenses and attachments are not copied.
        /// The copy is the user's own, even when the original is a trip shared with them. Null when the trip is gone.
        /// </summary>
        Task<Trip?> DuplicateAsync(int tripId, string newName);

        Task<decimal> GetTotalExpensesAsync(int tripId);
        Task<decimal> GetRemainingBudgetAsync(int tripId);
        Task<double> GetBudgetUsagePercentageAsync(int tripId);
    }
}
