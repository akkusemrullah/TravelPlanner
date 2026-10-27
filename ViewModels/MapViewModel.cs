using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Loads a trip's saved Places as pins for MapPage. Rendering is done via a free
    /// WebView + MapLibre GL JS / OpenFreeMap map (no API key or billing needed).
    /// When the trip has no saved Places yet, falls back to geocoding the trip's
    /// city/country (also free, via Open-Meteo's geocoding API) so the map isn't blank.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    [QueryProperty(nameof(Preview), "Preview")]
    public partial class MapViewModel : ViewModelBase
    {
        private readonly IPlaceService _placeService;
        private readonly ITripService _tripService;
        private readonly IGeocodingService _geocodingService;
        private readonly IConnectivity _connectivity;
        private readonly IActivityService _activityService;
        private readonly IDirectionsService _directions;

        // Day -> that day's linked places in plan order (TP-505).
        private Dictionary<DateTime, List<Place>> _routesByDay = new();

        [ObservableProperty]
        private int tripId;

        /// <summary>
        /// TP-33: shows a Keşfet sample's places (<see cref="MapPreview"/>) instead of a saved trip's - read-only, no
        /// routes, no "add a place here".
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsTripMap))]
        [NotifyPropertyChangedFor(nameof(Subtitle))]
        private bool preview;

        /// <summary>A saved trip's map (visited legend and tags), not a preview.</summary>
        public bool IsTripMap => !Preview;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Subtitle))]
        private Trip? trip;

        /// <summary>TP-103: the app bar's second line - the trip's name, or the Keşfet plan's title in a preview.</summary>
        public string Subtitle => Preview ? MapPreview.Title : Trip?.Name ?? string.Empty;

        [ObservableProperty]
        private double centerLatitude;

        [ObservableProperty]
        private double centerLongitude;

        [ObservableProperty]
        private bool isLoadingCity;

        public ObservableCollection<Place> Pins { get; } = new RangeObservableCollection<Place>();

        /// <summary>TP-50: the place tapped on the map or in the list - the "Yol tarifi" bar is shown for it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGetDirections))]
        private Place? selectedPlace;

        /// <summary>A place is selected and has a usable coordinate.</summary>
        public bool CanGetDirections =>
            SelectedPlace is { } p && PlaceService.IsValidCoordinate(p.Latitude, p.Longitude);

        // ---- TP-505: day route ----

        /// <summary>"Rota yok" plus every day whose plan visits at least two saved places.</summary>
        public ObservableCollection<SettingsOption<DateTime?>> RouteDays { get; } = new();

        [ObservableProperty]
        private SettingsOption<DateTime?>? selectedRouteDay;

        [ObservableProperty]
        private bool hasRoutes;

        /// <summary>Places of the selected day in visiting order - drawn as a numbered line on the map.</summary>
        /// <summary>TP-103: a route line is on the map, so the legend shows its pill.</summary>
        public bool HasRoutePlaces => RoutePlaces.Count > 1;

        public IReadOnlyList<Place> RoutePlaces =>
            SelectedRouteDay?.Value is DateTime day && _routesByDay.TryGetValue(day, out var places) ? places : Array.Empty<Place>();

        /// <summary>Raised when the route changes, so the page can redraw the map.</summary>
        public event EventHandler? RouteChanged;

        /// <summary>True when the device has no internet - the map tiles can't load, so the page shows a notice (TP-108).</summary>
        [ObservableProperty]
        private bool isOffline;

        public MapViewModel(IPlaceService placeService, ITripService tripService, IGeocodingService geocodingService, IConnectivity connectivity,
            IActivityService activityService, IDirectionsService directions, IDialogService dialogs)
            : base(dialogs)
        {
            _directions = directions;
            _activityService = activityService;
            _placeService = placeService;
            _tripService = tripService;
            _geocodingService = geocodingService;
            _connectivity = connectivity;
            ListPositions.Track(Pins);
        }

        // NOTE (TP-104): loading is triggered only from MapPage.OnAppearing.

        [RelayCommand]
        private Task LoadPlacesAsync() => RunLoadAsync(async () =>
        {
            IsOffline = _connectivity.NetworkAccess != NetworkAccess.Internet;

            if (Preview)
            {
                var pins = MapPreview.Places.Where(p => PlaceService.IsValidCoordinate(p.Latitude, p.Longitude)).ToList();
                Pins.ReplaceAll(pins);
                KeepSelection();
                RouteDays.Clear();
                HasRoutes = false;
                if (pins.Count > 0)
                {
                    CenterLatitude = pins[0].Latitude;
                    CenterLongitude = pins[0].Longitude;
                }
                return;
            }

            if (TripId == 0) return;

            Trip = await _tripService.GetByIdAsync(TripId);

            // A place with unusable coordinates (NaN/out of range, e.g. from an old or hand-edited backup) would
            // make the marker JSON throw and MapLibre reject the whole layer - leave it off the map instead.
            var places = (await _placeService.GetByTripIdAsync(TripId)).Where(p => PlaceService.IsValidCoordinate(p.Latitude, p.Longitude)).ToList();
            Pins.ReplaceAll(places);
            KeepSelection();

            await LoadRoutesAsync(places);

            if (places.Count > 0)
            {
                // Center on the first saved place.
                CenterLatitude = places[0].Latitude;
                CenterLongitude = places[0].Longitude;
                return;
            }

            // No saved places yet - geocode the trip's city so the map still shows
            // something useful instead of an empty/default view.
            if (Trip is not null)
            {
                IsLoadingCity = true;
                try
                {
                    var coords = await _geocodingService.GeocodeAsync(Trip.City, Trip.Country);
                    if (coords is not null)
                    {
                        CenterLatitude = coords.Value.Latitude;
                        CenterLongitude = coords.Value.Longitude;
                    }
                }
                finally
                {
                    IsLoadingCity = false;
                }
            }
        }, AppResources.Map_LoadError);

        private async Task LoadRoutesAsync(IReadOnlyList<Place> places)
        {
            var activities = await _activityService.GetByTripIdAsync(TripId);
            _routesByDay = BuildRoutes(activities, places);

            var previous = SelectedRouteDay?.Value;
            RouteDays.Clear();
            RouteDays.Add(new SettingsOption<DateTime?>(null, AppResources.Map_NoRoute));
            foreach (var day in _routesByDay.Keys.OrderBy(d => d))
            {
                var number = Trip is null ? 0 : (day - Trip.StartDate.Date).Days + 1;
                var label = number > 0
                    ? $"{string.Format(AppResources.Itinerary_DayFormat, number)} · {day.ToString("ddd d MMM", CultureInfo.CurrentCulture)}"
                    : day.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                RouteDays.Add(new SettingsOption<DateTime?>(day, label));
            }

            HasRoutes = RouteDays.Count > 1;

            // Keep the user's choice; otherwise show today's route mid-trip.
            SelectedRouteDay = RouteDays.FirstOrDefault(d => d.Value == previous && previous is not null)
                ?? RouteDays.FirstOrDefault(d => d.Value == DateTime.Today)
                ?? RouteDays[0];
        }

        /// <summary>
        /// For each day: the places its activities link to, in plan order (consecutive repeats collapsed).
        /// Only days with two or more stops make a route.
        /// </summary>
        internal static Dictionary<DateTime, List<Place>> BuildRoutes(IEnumerable<TripActivity> activities, IEnumerable<Place> places)
        {
            var byId = places.ToDictionary(p => p.Id);
            var routes = new Dictionary<DateTime, List<Place>>();

            foreach (var day in activities.Where(a => a.PlaceId is not null).GroupBy(a => a.Date.Date))
            {
                var stops = new List<Place>();
                foreach (var activity in day.OrderBy(a => a.SortOrder))
                {
                    if (!byId.TryGetValue(activity.PlaceId!.Value, out var place))
                        continue;
                    if (stops.Count > 0 && stops[^1].Id == place.Id)
                        continue;
                    stops.Add(place);
                }

                if (stops.Count >= 2)
                    routes[day.Key] = stops;
            }

            return routes;
        }

        partial void OnSelectedRouteDayChanged(SettingsOption<DateTime?>? value)
        {
            OnPropertyChanged(nameof(RoutePlaces));
            OnPropertyChanged(nameof(HasRoutePlaces));
            RouteChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Custom URL the map's JavaScript navigates to on a long press (TP-505) - intercepted, never loaded.</summary>
        public const string AddPlaceUrlPrefix = "travelplanner://addplace";

        /// <summary>Parses "travelplanner://addplace?lat=41.0&amp;lon=28.9" (invariant culture, range-checked).</summary>
        public static bool TryParseAddPlaceUrl(string? url, out double latitude, out double longitude)
        {
            latitude = longitude = 0;
            if (string.IsNullOrEmpty(url) || !url.StartsWith(AddPlaceUrlPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : string.Empty;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2)
                    values[kv[0]] = Uri.UnescapeDataString(kv[1]);
            }

            return values.TryGetValue("lat", out var lat) && values.TryGetValue("lon", out var lon) &&
                   double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out latitude) &&
                   double.TryParse(lon, NumberStyles.Float, CultureInfo.InvariantCulture, out longitude) &&
                   latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
        }

        /// <summary>TP-505: long-press on the map → new place form with those coordinates.</summary>
        public Task AddPlaceAtAsync(double latitude, double longitude)
        {
            if (Preview || TripId == 0)
                return Task.CompletedTask;
            var lat = latitude.ToString("0.######", CultureInfo.InvariantCulture);
            var lon = longitude.ToString("0.######", CultureInfo.InvariantCulture);
            return Shell.Current.GoToAsync($"{Routes.PlaceForm}?TripId={TripId}&Lat={lat}&Lon={lon}");
        }

        /// <summary>After a reload: keep the selected place if it is still on the map (fresh copy), else clear it.</summary>
        private void KeepSelection() =>
            SelectedPlace = SelectedPlace is { } current ? Pins.FirstOrDefault(p => p.Id == current.Id) : null;

        /// <summary>Custom URL the map's JavaScript navigates to when a pin is tapped (TP-50) - intercepted, never loaded.</summary>
        public const string SelectPlaceUrlPrefix = "travelplanner://selectplace";

        /// <summary>Parses "travelplanner://selectplace?id=12".</summary>
        public static bool TryParseSelectPlaceUrl(string? url, out int placeId)
        {
            placeId = 0;
            if (string.IsNullOrEmpty(url) || !url.StartsWith(SelectPlaceUrlPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            var query = url.Contains('?') ? url[(url.IndexOf('?') + 1)..] : string.Empty;
            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("id", StringComparison.OrdinalIgnoreCase))
                    return int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out placeId);
            }
            return false;
        }

        /// <summary>TP-50: a pin was tapped on the map.</summary>
        public void SelectPlace(int placeId) => SelectedPlace = Pins.FirstOrDefault(p => p.Id == placeId);

        [RelayCommand]
        private void ClearSelection() => SelectedPlace = null;

        /// <summary>TP-50: the phone's map app (or Google Maps in the browser) with directions to the selected place.</summary>
        [RelayCommand]
        private async Task OpenDirectionsAsync()
        {
            if (SelectedPlace is not { } place || !CanGetDirections)
                return;
            if (!await _directions.OpenAsync(place.Latitude, place.Longitude, place.Name))
                await Dialogs.ShowErrorAsync(AppResources.Map_DirectionsError);
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
