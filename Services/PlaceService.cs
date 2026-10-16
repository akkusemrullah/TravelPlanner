using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class PlaceService : IPlaceService
    {
        private readonly IDatabaseService _databaseService;

        public PlaceService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<Place>> GetByTripIdAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            return await db.Table<Place>()
                .Where(p => p.TripId == tripId)
                .ToListAsync();
        }

        public async Task<Place?> GetByIdAsync(int id)
        {
            var db = await _databaseService.Connection;
            return await db.Table<Place>().Where(p => p.Id == id).FirstOrDefaultAsync();
        }

        /// <summary>True for a real point on Earth: finite, latitude in [-90, 90], longitude in [-180, 180].</summary>
        public static bool IsValidCoordinate(double latitude, double longitude) =>
            double.IsFinite(latitude) && double.IsFinite(longitude) &&
            latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

        public async Task<int> SaveAsync(Place place)
        {
            if (!IsValidCoordinate(place.Latitude, place.Longitude))
                throw new ArgumentOutOfRangeException(nameof(place), $"Invalid coordinates {place.Latitude}, {place.Longitude}.");

            Helpers.InputLimits.EnforceTextLimits(place);
            place.Name = Helpers.TextCase.CapitalizeFirst(place.Name) ?? string.Empty;
            place.Description = Helpers.TextCase.CapitalizeFirst(place.Description);
            var db = await _databaseService.Connection;
            return await db.SaveTrackedAsync(place);
        }

        public async Task<int> DeleteAsync(Place place)
        {
            var db = await _databaseService.Connection;
            var deleted = 0;

            // Plan activities that pointed at this place keep their entry, just without the place (TP-401).
            var placeId = place.Id;
            await db.RunInTransactionAsync(conn =>
            {
                foreach (var activity in conn.Table<TripActivity>().Where(a => a.PlaceId == placeId).ToList())
                {
                    activity.PlaceId = null;
                    conn.UpdateTracked(activity);
                }

                deleted = conn.DeleteTracked(place);
            });

            return deleted;
        }
    }
}
