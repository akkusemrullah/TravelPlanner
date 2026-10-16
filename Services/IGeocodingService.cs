using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Resolves a free-text place (city, country) to map coordinates - no API key required.</summary>
    public interface IGeocodingService
    {
        /// <summary>Returns (Latitude, Longitude) for the given place, or null if it couldn't be resolved.</summary>
        Task<(double Latitude, double Longitude)?> GeocodeAsync(string city, string? country = null);

        /// <summary>
        /// Returns up to a handful of matching cities for the given free-text query, used to power the
        /// "Şehir" autocomplete on TripFormPage. Optionally scoped to a country name/code to disambiguate
        /// cities that share a name (e.g. "Paris" in France vs. the US). Empty on no matches or no network.
        /// </summary>
        Task<List<CitySuggestion>> SearchCitiesAsync(string query, string? country = null, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns up to a handful of matching places for the given free-text query, used to power the
        /// "Yer Adı" autocomplete on PlaceFormPage. Unlike SearchCitiesAsync above, this also matches
        /// points of interest - monuments, museums, historic sites, landmarks (e.g. "Anıtkabir") - not
        /// just cities/districts. Empty on no matches or no network.
        /// </summary>
        Task<List<PlaceSuggestion>> SearchPlacesAsync(string query, CancellationToken cancellationToken = default);

        /// <summary>The nearest named place to a point (Photon reverse geocoding, TP-505). Null when unknown/offline.</summary>
        Task<PlaceSuggestion?> ReverseGeocodeAsync(double latitude, double longitude, CancellationToken cancellationToken = default);
    }
}
