using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Free geocoding via Open-Meteo's geocoding API - no API key or billing account needed,
    /// unlike Microsoft.Maui.Controls.Maps' Google Maps requirement. Used to center the Map
    /// page (and to resolve a city to coordinates for WeatherService) when there are no saved
    /// Places yet. Uses the same provider as the weather forecast API so the app only depends
    /// on one external, keyless service instead of two.
    /// </summary>
    public class GeocodingService : IGeocodingService
    {
        private static readonly TimeSpan GeocodeCacheDuration = TimeSpan.FromHours(24);
        private static readonly TimeSpan SuggestionCacheDuration = TimeSpan.FromMinutes(10);

        private readonly HttpClient _httpClient;
        private readonly IConnectivity _connectivity;
        private readonly IResponseCache _cache;

        public GeocodingService(HttpClient httpClient, IConnectivity connectivity, IResponseCache cache)
        {
            _httpClient = httpClient;
            _connectivity = connectivity;
            _cache = cache;
        }

        // The UI language is part of the key: the APIs return localized names, so a Turkish answer
        // must not be served after switching to English (and vice versa).
        private static string CacheKey(string kind, params string?[] parts) =>
            $"geo:{kind}:{LocalizationHelper.ApiLanguage}:" + string.Join("|", parts.Select(p => p?.Trim().ToLowerInvariant() ?? string.Empty));

        public async Task<(double Latitude, double Longitude)?> GeocodeAsync(string city, string? country = null)
        {
            if (string.IsNullOrWhiteSpace(city))
                return null;

            var cacheKey = CacheKey("point", city, country);
            if (_cache.TryGet<(double Latitude, double Longitude)>(cacheKey, out var cached))
                return cached;

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                Debug.WriteLine("[GeocodingService] Skipped: no internet access reported by Connectivity API.");
                return null;
            }

            try
            {
                // Ask for several candidates and pick the one whose country matches (when we know
                // it), since Open-Meteo's geocoding matches by name only and cities can repeat
                // across countries (e.g. more than one "Paris").
                var url = $"{AppConstants.OpenMeteoGeocodingBaseUrl}?name={Uri.EscapeDataString(InputLimits.Truncate(city.Trim(), InputLimits.MaxSearchQueryLength)!)}&count=10&language={LocalizationHelper.ApiLanguage}&format=json";

                var response = await _httpClient.GetFromJsonAsync<GeocodingResponse>(url);
                var results = response?.Results;
                if (results is null || results.Count == 0)
                {
                    Debug.WriteLine($"[GeocodingService] No results for '{city}, {country}'.");
                    return null;
                }

                var match = string.IsNullOrWhiteSpace(country)
                    ? results[0]
                    : results.FirstOrDefault(r =>
                          string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(r.CountryCode, country, StringComparison.OrdinalIgnoreCase))
                      ?? results[0];

                var point = (match.Latitude, match.Longitude);
                _cache.Set(cacheKey, point, GeocodeCacheDuration);
                return point;
            }
            catch (HttpRequestException ex)
            {
                // Non-success status code / DNS / TLS failure - API unreachable. If this keeps
                // happening, check the Debug Output for the exact message: a corporate firewall
                // or proxy silently blocking the domain looks like this too (not just "no internet").
                Debug.WriteLine($"[GeocodingService] HttpRequestException for '{city}, {country}': {ex}");
                return null;
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"[GeocodingService] Request timed out for '{city}, {country}': {ex}");
                return null;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[GeocodingService] Unexpected response shape for '{city}, {country}': {ex}");
                return null;
            }
            catch (Exception ex)
            {
                // Catch-all so an unexpected platform-specific error (e.g. a wrapped Java
                // exception on Android) doesn't crash the app - but still gets logged so it's
                // possible to diagnose from the Debug Output window.
                Debug.WriteLine($"[GeocodingService] Unexpected error for '{city}, {country}': {ex}");
                return null;
            }
        }

        /// <summary>
        /// Returns up to a handful of geocoded city matches for <paramref name="query"/>, used to power
        /// the "Şehir" autocomplete on TripFormPage. Shares the same Open-Meteo endpoint and response
        /// shape as GeocodeAsync above, just surfacing the full result set instead of one coordinate pair.
        /// </summary>
        public async Task<List<CitySuggestion>> SearchCitiesAsync(string query, string? country = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                return new List<CitySuggestion>();

            var cacheKey = CacheKey("cities", query, country);
            if (_cache.TryGet<List<CitySuggestion>>(cacheKey, out var cachedCities) && cachedCities is not null)
                return cachedCities.ToList();

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                Debug.WriteLine("[GeocodingService] SearchCitiesAsync skipped: no internet access reported by Connectivity API.");
                return new List<CitySuggestion>();
            }

            try
            {
                var url = $"{AppConstants.OpenMeteoGeocodingBaseUrl}?name={Uri.EscapeDataString(InputLimits.Truncate(query.Trim(), InputLimits.MaxSearchQueryLength)!)}&count=10&language={LocalizationHelper.ApiLanguage}&format=json";
                var response = await _httpClient.GetFromJsonAsync<GeocodingResponse>(url, cancellationToken);
                var results = response?.Results;
                if (results is null || results.Count == 0)
                    return new List<CitySuggestion>();

                // Prefer matches in the selected country (if any), same disambiguation logic as
                // GeocodeAsync - but fall back to the unfiltered set rather than an empty list, since
                // showing "the wrong country" suggestions while the user is still typing beats showing none.
                var filtered = string.IsNullOrWhiteSpace(country)
                    ? results
                    : results.Where(r =>
                          string.Equals(r.Country, country, StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(r.CountryCode, country, StringComparison.OrdinalIgnoreCase))
                      .ToList();

                var source = filtered.Count > 0 ? filtered : results;

                var suggestions = source
                    .Select(r => new CitySuggestion
                    {
                        Name = r.Name ?? query,
                        Country = r.Country ?? string.Empty,
                        CountryCode = r.CountryCode,
                        Admin1 = r.Admin1,
                        Latitude = r.Latitude,
                        Longitude = r.Longitude
                    })
                    .Take(8)
                    .ToList();

                _cache.Set(cacheKey, suggestions, SuggestionCacheDuration);
                return suggestions.ToList();
            }
            catch (OperationCanceledException)
            {
                // Expected: a newer keystroke superseded this search before it finished (see the
                // debounce in TripFormViewModel) - let the caller's cancellation handling take over.
                throw;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchCitiesAsync HttpRequestException for '{query}, {country}': {ex}");
                return new List<CitySuggestion>();
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchCitiesAsync unexpected response shape for '{query}, {country}': {ex}");
                return new List<CitySuggestion>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchCitiesAsync unexpected error for '{query}, {country}': {ex}");
                return new List<CitySuggestion>();
            }
        }

        /// <summary>
        /// Returns up to a handful of Photon (OpenStreetMap) matches for <paramref name="query"/>, used
        /// to power the "Yer Adı" autocomplete on PlaceFormPage. Unlike SearchCitiesAsync above, Photon's
        /// index includes points of interest - monuments, museums, historic sites, landmarks - not just
        /// populated places, so a query like "Anıtkabir" actually turns up a result.
        /// </summary>
        public async Task<List<PlaceSuggestion>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
                return new List<PlaceSuggestion>();

            var cacheKey = CacheKey("places", query);
            if (_cache.TryGet<List<PlaceSuggestion>>(cacheKey, out var cachedPlaces) && cachedPlaces is not null)
                return cachedPlaces.ToList();

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                Debug.WriteLine("[GeocodingService] SearchPlacesAsync skipped: no internet access reported by Connectivity API.");
                return new List<PlaceSuggestion>();
            }

            try
            {
                var url = $"{AppConstants.PhotonSearchBaseUrl}?q={Uri.EscapeDataString(InputLimits.Truncate(query.Trim(), InputLimits.MaxSearchQueryLength)!)}&limit=8&lang=en"; // Photon has no Turkish option
                var response = await _httpClient.GetFromJsonAsync<PhotonResponse>(url, cancellationToken);
                var features = response?.Features;
                if (features is null || features.Count == 0)
                    return new List<PlaceSuggestion>();

                var places = features
                    // Photon can return features with no usable name (e.g. a bare postcode area) -
                    // skip those rather than showing a blank row in the suggestion list.
                    .Where(f => f.Geometry?.Coordinates is { Length: 2 } && !string.IsNullOrWhiteSpace(f.Properties?.Name))
                    .Select(f => new PlaceSuggestion
                    {
                        Name = f.Properties!.Name!,
                        CategoryLabel = DescribeCategory(f.Properties.OsmKey, f.Properties.OsmValue),
                        City = f.Properties.City,
                        State = f.Properties.State,
                        Country = f.Properties.Country,
                        // GeoJSON coordinate order is [longitude, latitude], not [latitude, longitude].
                        Longitude = f.Geometry!.Coordinates![0],
                        Latitude = f.Geometry.Coordinates[1]
                    })
                    .Take(8)
                    .ToList();

                _cache.Set(cacheKey, places, SuggestionCacheDuration);
                return places.ToList();
            }
            catch (OperationCanceledException)
            {
                // Expected: a newer keystroke superseded this search before it finished (see the
                // debounce in PlaceFormViewModel) - let the caller's cancellation handling take over.
                throw;
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchPlacesAsync HttpRequestException for '{query}': {ex}");
                return new List<PlaceSuggestion>();
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchPlacesAsync unexpected response shape for '{query}': {ex}");
                return new List<PlaceSuggestion>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeocodingService] SearchPlacesAsync unexpected error for '{query}': {ex}");
                return new List<PlaceSuggestion>();
            }
        }

        public async Task<PlaceSuggestion?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var cacheKey = CacheKey("reverse", latitude.ToString("0.####", inv), longitude.ToString("0.####", inv));
            if (_cache.TryGet<PlaceSuggestion>(cacheKey, out var cached) && cached is not null)
                return cached;

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                return null;

            try
            {
                var url = $"{AppConstants.PhotonReverseBaseUrl}?lat={latitude.ToString(inv)}&lon={longitude.ToString(inv)}&limit=1&lang=en";
                var response = await _httpClient.GetFromJsonAsync<PhotonResponse>(url, cancellationToken);
                var feature = response?.Features?.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Properties?.Name));
                if (feature?.Properties is null)
                    return null;

                var suggestion = new PlaceSuggestion
                {
                    Name = feature.Properties.Name!,
                    CategoryLabel = DescribeCategory(feature.Properties.OsmKey, feature.Properties.OsmValue),
                    City = feature.Properties.City,
                    State = feature.Properties.State,
                    Country = feature.Properties.Country,
                    // Keep the exact point the user pressed - the feature may be a whole building or park.
                    Latitude = latitude,
                    Longitude = longitude
                };

                _cache.Set(cacheKey, suggestion, GeocodeCacheDuration);
                return suggestion;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GeocodingService] ReverseGeocodeAsync failed for {latitude},{longitude}: {ex}");
                return null;
            }
        }

        // Localized labels (resource keys) for the most common OSM tags Photon returns for tourist/historic
        // points of interest - checked by osm_value first (more specific), then osm_key (broader).
        private static readonly Dictionary<string, string> CategoryKeysByOsmValue = new(StringComparer.OrdinalIgnoreCase)
        {
            ["monument"] = nameof(AppResources.Geo_Monument),
            ["memorial"] = nameof(AppResources.Geo_Monument),
            ["museum"] = nameof(AppResources.Geo_Museum),
            ["castle"] = nameof(AppResources.Geo_Castle),
            ["fort"] = nameof(AppResources.Geo_Castle),
            ["ruins"] = nameof(AppResources.Geo_Ruins),
            ["archaeological_site"] = nameof(AppResources.Geo_Ruins),
            ["attraction"] = nameof(AppResources.Geo_Attraction),
            ["viewpoint"] = nameof(AppResources.Geo_Viewpoint),
            ["place_of_worship"] = nameof(AppResources.Geo_PlaceOfWorship),
            ["mosque"] = nameof(AppResources.Geo_Mosque),
            ["church"] = nameof(AppResources.Geo_Church),
            ["synagogue"] = nameof(AppResources.Geo_Synagogue),
            ["palace"] = nameof(AppResources.Geo_Palace),
            ["city"] = nameof(AppResources.Geo_City),
            ["town"] = nameof(AppResources.Geo_Town),
            ["village"] = nameof(AppResources.Geo_Village),
            ["hamlet"] = nameof(AppResources.Geo_Village),
            ["suburb"] = nameof(AppResources.Geo_Neighbourhood),
            ["neighbourhood"] = nameof(AppResources.Geo_Neighbourhood),
        };

        private static readonly Dictionary<string, string> CategoryKeysByOsmKey = new(StringComparer.OrdinalIgnoreCase)
        {
            ["historic"] = nameof(AppResources.Geo_Historic),
            ["tourism"] = nameof(AppResources.Geo_Attraction),
            ["amenity"] = nameof(AppResources.Geo_Amenity),
            ["leisure"] = nameof(AppResources.Geo_Leisure),
            ["natural"] = nameof(AppResources.Geo_Natural),
            ["place"] = nameof(AppResources.Geo_Settlement),
        };

        private static string? DescribeCategory(string? osmKey, string? osmValue)
        {
            string? resourceKey = null;
            if (!string.IsNullOrWhiteSpace(osmValue))
                CategoryKeysByOsmValue.TryGetValue(osmValue, out resourceKey);
            if (resourceKey is null && !string.IsNullOrWhiteSpace(osmKey))
                CategoryKeysByOsmKey.TryGetValue(osmKey, out resourceKey);

            return resourceKey is null ? null : AppResources.ResourceManager.GetString(resourceKey, AppResources.Culture);
        }

        private class GeocodingResponse
        {
            [JsonPropertyName("results")]
            public List<GeocodingResult>? Results { get; set; }
        }

        private class GeocodingResult
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("latitude")]
            public double Latitude { get; set; }

            [JsonPropertyName("longitude")]
            public double Longitude { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }

            [JsonPropertyName("country_code")]
            public string? CountryCode { get; set; }

            [JsonPropertyName("admin1")]
            public string? Admin1 { get; set; }
        }

        // ---- Photon (OpenStreetMap) response shape - a GeoJSON FeatureCollection ----

        private class PhotonResponse
        {
            [JsonPropertyName("features")]
            public List<PhotonFeature>? Features { get; set; }
        }

        private class PhotonFeature
        {
            [JsonPropertyName("geometry")]
            public PhotonGeometry? Geometry { get; set; }

            [JsonPropertyName("properties")]
            public PhotonProperties? Properties { get; set; }
        }

        private class PhotonGeometry
        {
            /// <summary>GeoJSON Point coordinates: [longitude, latitude].</summary>
            [JsonPropertyName("coordinates")]
            public double[]? Coordinates { get; set; }
        }

        private class PhotonProperties
        {
            [JsonPropertyName("name")]
            public string? Name { get; set; }

            [JsonPropertyName("city")]
            public string? City { get; set; }

            [JsonPropertyName("state")]
            public string? State { get; set; }

            [JsonPropertyName("country")]
            public string? Country { get; set; }

            [JsonPropertyName("osm_key")]
            public string? OsmKey { get; set; }

            [JsonPropertyName("osm_value")]
            public string? OsmValue { get; set; }
        }
    }
}
