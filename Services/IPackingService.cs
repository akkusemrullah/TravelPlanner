using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Smart packing list suggestions and reusable packing templates (TP-405). Implemented by PackingService.</summary>
    public interface IPackingService
    {
        /// <summary>
        /// Suggestions based on the trip's length, destination, plan (activities, places, trip details - see
        /// <see cref="GetPlanAsync"/>) and, if available, its daily forecast.
        /// Items whose title already appears in <paramref name="existingTitles"/> are left out.
        /// </summary>
        IReadOnlyList<PackingSuggestion> GetSuggestions(Trip trip, IReadOnlyList<DailyForecast>? forecast, IEnumerable<string> existingTitles,
                                                        IEnumerable<PackingPlanEntry>? plan = null);

        /// <summary>The trip's plan as the packing rules read it: the trip itself, its itinerary activities and its places.</summary>
        Task<IReadOnlyList<PackingPlanEntry>> GetPlanAsync(Trip trip);

        Task<List<PackingTemplate>> GetTemplatesAsync();

        /// <summary>Saves a template; an existing template with the same name (case-insensitive) is replaced.</summary>
        Task<PackingTemplate> SaveTemplateAsync(string name, IEnumerable<string> items);

        Task<int> DeleteTemplateAsync(PackingTemplate template);
    }
}
