namespace TravelPlanner.Services
{
    /// <summary>TP-50: hands a destination to the phone's map app for turn-by-turn directions.</summary>
    public interface IDirectionsService
    {
        /// <summary>
        /// Opens Google Maps (Android) / Apple Maps (iOS) in directions mode to the given point; without a map app,
        /// Google Maps in the browser. False when nothing could be opened or the coordinate is unusable.
        /// </summary>
        Task<bool> OpenAsync(double latitude, double longitude, string? name);
    }
}
