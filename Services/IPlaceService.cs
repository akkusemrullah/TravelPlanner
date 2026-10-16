using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Place CRUD for a trip. Implemented by PlaceService.</summary>
    public interface IPlaceService
    {
        Task<List<Place>> GetByTripIdAsync(int tripId);
        Task<Place?> GetByIdAsync(int id);
        Task<int> SaveAsync(Place place);
        Task<int> DeleteAsync(Place place);
    }
}
