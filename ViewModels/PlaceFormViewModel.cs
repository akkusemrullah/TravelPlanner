using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    [QueryProperty(nameof(PlaceId), "PlaceId")]
    [QueryProperty(nameof(LatitudeParameter), "Lat")]
    [QueryProperty(nameof(LongitudeParameter), "Lon")]
    public partial class PlaceFormViewModel : ViewModelBase
    {
        private readonly IPlaceService _placeService;
        private readonly IGeocodingService _geocodingService;
        private readonly IConnectivity _connectivity;

        /// <summary>True while typing without internet - shows a hint that suggestions can't load (TP-306).</summary>
        [ObservableProperty]
        private bool isOffline;
        private Place? _existingPlace;

        // Guards against the name autocomplete re-opening its own dropdown (or clearing the
        // "location picked" flag below) right after the user - or LoadPlaceAsync - sets Name
        // programmatically, since that also fires OnNameChanged.
        private bool _isApplyingNameSelection;

        // True once Latitude/Longitude reflect an actual picked location (a loaded existing place, or
        // a suggestion the user tapped) rather than the unset 0,0 default - SaveAsync refuses to save
        // without this, since there's no more manual Enlem/Boylam entry to fall back on.
        private bool _hasSelectedLocation;

        // TP-505: the coordinates came from a long press on the map, so typing a name must not discard them.
        private bool _locationFromMap;

        // Debounce guard for the name search: each keystroke bumps this, and a pending search only
        // applies its results if it's still the latest one when it wakes up (same pattern as
        // TripFormViewModel's city search - see its comment for why this is nicer than cancelling
        // Task.Delay with a CancellationTokenSource).
        private int _nameSearchGeneration;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private int placeId;

        /// <summary>Latitude passed from the map (invariant culture string), TP-505.</summary>
        [ObservableProperty]
        private string? latitudeParameter;

        [ObservableProperty]
        private string? longitudeParameter;

        /// <summary>Shown when the location was picked on the map.</summary>
        [ObservableProperty]
        private bool isLocationFromMap;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string? description;

        [ObservableProperty]
        private double latitude;

        [ObservableProperty]
        private double longitude;

        [ObservableProperty]
        private bool isNameSuggestionsVisible;

        /// <summary>
        /// Matches from Photon (OpenStreetMap search), shown under the "Yer Adı" entry. Unlike
        /// TripFormPage's "Gidilecek Yer" field (which searches Open-Meteo's city/district gazetteer),
        /// this also matches points of interest - monuments, museums, historic sites, landmarks like
        /// "Anıtkabir" - since a visited place is usually more specific than a whole city.
        /// </summary>
        public ObservableCollection<PlaceSuggestion> NameSuggestions { get; } = new();

        /// <summary>Validation message shown above the Save button (TP-102). Empty = no error.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private string validationMessage = string.Empty;

        public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

        public PlaceFormViewModel(IPlaceService placeService, IGeocodingService geocodingService, IConnectivity connectivity, IDialogService dialogs)
            : base(dialogs)
        {
            _placeService = placeService;
            _geocodingService = geocodingService;
            _connectivity = connectivity;
        }

        partial void OnLatitudeParameterChanged(string? value) => TryApplyMapLocation();

        partial void OnLongitudeParameterChanged(string? value) => TryApplyMapLocation();

        private void TryApplyMapLocation()
        {
            if (PlaceId > 0 || _locationFromMap ||
                !double.TryParse(LatitudeParameter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                !double.TryParse(LongitudeParameter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) ||
                !PlaceService.IsValidCoordinate(lat, lon)) // "NaN"/"Infinity"/out-of-range parse fine as doubles
                return;

            Latitude = lat;
            Longitude = lon;
            _hasSelectedLocation = true;
            _locationFromMap = true;
            IsLocationFromMap = true;

            // Suggest a name for the pressed point (free Photon reverse geocoding); the user can change it.
            RunSafeAsync(async () =>
            {
                var place = await _geocodingService.ReverseGeocodeAsync(lat, lon);
                if (place is null || !string.IsNullOrWhiteSpace(Name))
                    return;

                _isApplyingNameSelection = true;
                Name = place.Name;
                _isApplyingNameSelection = false;
            }).FireAndForget();
        }

        partial void OnPlaceIdChanged(int value)
        {
            if (value > 0)
                RunSafeAsync(() => LoadPlaceAsync(value), AppResources.Place_LoadError).FireAndForget();
        }

        private async Task LoadPlaceAsync(int id)
        {
            _existingPlace = await _placeService.GetByIdAsync(id);
            if (_existingPlace is null) return;

            _isApplyingNameSelection = true;

            Name = _existingPlace.Name;
            Description = _existingPlace.Description;
            Latitude = _existingPlace.Latitude;
            Longitude = _existingPlace.Longitude;

            _isApplyingNameSelection = false;
            _hasSelectedLocation = true; // the loaded place already has valid coordinates
        }

        // ---- "Yer Adı" autocomplete ----
        // Backed by GeocodingService's free Photon (OpenStreetMap) search, which - unlike TripFormPage's
        // "Gidilecek Yer" field - also covers points of interest (monuments, museums, historic sites),
        // so Latitude/Longitude get filled in automatically from the picked suggestion instead of the
        // user ever typing coordinates by hand.

        partial void OnNameChanged(string value)
        {
            if (_isApplyingNameSelection)
                return;

            // Typing invalidates whatever location was previously picked (if any) - a fresh pick is
            // required before saving, so stale coordinates from an earlier name never sneak through.
            // Exceptions - the user is just (re)naming a point that already has a location:
            // - a point picked on the map (TP-505);
            // - an existing place being edited: fixing a typo in its name must not force picking its location
            //   again (which would also be impossible offline). Picking a suggestion still moves it.
            if (!_locationFromMap && _existingPlace is null)
                _hasSelectedLocation = false;

            var generation = ++_nameSearchGeneration;
            IsOffline = _connectivity.NetworkAccess != NetworkAccess.Internet;

            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
            {
                NameSuggestions.Clear();
                IsNameSuggestionsVisible = false;
                return;
            }

            SearchPlacesDebouncedAsync(value, generation).FireAndForget();
        }

        private async Task SearchPlacesDebouncedAsync(string query, int generation)
        {
            await Task.Delay(350); // debounce - wait for a pause in typing

            if (generation != _nameSearchGeneration)
                return; // a newer keystroke arrived while we were waiting - this search is stale

            var results = await _geocodingService.SearchPlacesAsync(query);

            if (generation != _nameSearchGeneration)
                return; // the user kept typing during the network round-trip

            NameSuggestions.ReplaceAll(results);

            IsNameSuggestionsVisible = NameSuggestions.Count > 0;
        }

        [RelayCommand]
        private void SelectPlace(PlaceSuggestion selected)
        {
            if (selected is null)
                return;

            _isApplyingNameSelection = true;
            Name = selected.Name;
            _isApplyingNameSelection = false;

            Latitude = selected.Latitude;
            Longitude = selected.Longitude;
            _hasSelectedLocation = true;
            _locationFromMap = false;
            IsLocationFromMap = false;
            ValidationMessage = string.Empty;

            IsNameSuggestionsVisible = false;
            NameSuggestions.Clear();
        }

        [RelayCommand]
        private Task SaveAsync() => RunSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Name))
            {
                ValidationMessage = AppResources.Place_NameRequired;
                return;
            }

            if (!_hasSelectedLocation)
            {
                // Coordinates only come from a picked suggestion - there's no manual lat/lon entry.
                ValidationMessage = AppResources.Place_PickSuggestion;
                return;
            }

            ValidationMessage = string.Empty;

            var place = _existingPlace ?? new Place { TripId = TripId };
            place.Name = Name.Trim();
            place.Description = Description;
            place.Latitude = Latitude;
            place.Longitude = Longitude;

            await _placeService.SaveAsync(place);
            await Shell.Current.GoToAsync("..");
        }, AppResources.Place_SaveError);

        [RelayCommand]
        private async Task CancelAsync() => await Shell.Current.GoToAsync("..");
    }
}
