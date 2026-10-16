using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Day-by-day plan (itinerary) CRUD and ordering for a trip (TP-401). Implemented by ActivityService.</summary>
    public interface IActivityService
    {
        /// <summary>All of a trip's activities, ordered by day and then by position within the day.</summary>
        Task<List<TripActivity>> GetByTripIdAsync(int tripId);

        /// <summary>One day's activities in display order.</summary>
        Task<List<TripActivity>> GetByDateAsync(int tripId, DateTime date);

        Task<TripActivity?> GetByIdAsync(int id);

        /// <summary>
        /// Inserts or updates. A new activity - or one moved to another day/time - is slotted into its day
        /// chronologically (untimed ones go to the end); otherwise the user's manual order is kept.
        /// </summary>
        Task SaveAsync(TripActivity activity);

        Task<int> DeleteAsync(TripActivity activity);

        /// <summary>Persists a day's new order (after drag &amp; drop): SortOrder = position in the list.</summary>
        Task ReorderAsync(IReadOnlyList<TripActivity> orderedDay);

        /// <summary>
        /// Writes a <see cref="TripDateRealignment"/> in one transaction after the trip's dates changed: shifts,
        /// moves outside activities to the nearest trip day (appended to that day) or deletes them, then
        /// renumbers the affected days so their order stays contiguous.
        /// </summary>
        Task ApplyRealignmentAsync(TripDateRealignment realignment);
    }
}
