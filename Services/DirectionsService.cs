using System.Globalization;

namespace TravelPlanner.Services
{
    /// <inheritdoc cref="IDirectionsService"/>
    public class DirectionsService : IDirectionsService
    {
        private readonly IMap _map;
        private readonly ILauncher _launcher;

        public DirectionsService(IMap map, ILauncher launcher)
        {
            _map = map;
            _launcher = launcher;
        }

        public async Task<bool> OpenAsync(double latitude, double longitude, string? name)
        {
            if (!PlaceService.IsValidCoordinate(latitude, longitude))
                return false;

            try
            {
                // Android: google.navigation: intent, iOS: Apple Maps with driving directions.
                var options = new MapLaunchOptions { Name = name, NavigationMode = NavigationMode.Driving };
                if (await _map.TryOpenAsync(latitude, longitude, options))
                    return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Directions] Map app failed: {ex.Message}");
            }

            // no map app (or it refused): Google Maps in the browser
            try
            {
                return await _launcher.TryOpenAsync(BuildWebUrl(latitude, longitude));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Directions] Browser fallback failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Google Maps directions URL (works in any browser, opens the app when it is installed).</summary>
        public static Uri BuildWebUrl(double latitude, double longitude)
        {
            var lat = latitude.ToString("0.######", CultureInfo.InvariantCulture);
            var lon = longitude.ToString("0.######", CultureInfo.InvariantCulture);
            return new Uri($"https://www.google.com/maps/dir/?api=1&destination={lat},{lon}");
        }
    }
}
