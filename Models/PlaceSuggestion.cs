namespace TravelPlanner.Models
{
    /// <summary>
    /// One geocoded match shown in the "Yer Adı" autocomplete dropdown on PlaceFormPage. Backed by
    /// Photon (komoot's free OpenStreetMap-based search) - unlike Open-Meteo's geocoder used for
    /// TripFormPage's "Gidilecek Yer" field, Photon indexes points of interest (monuments, museums,
    /// historic sites, landmarks) in addition to cities and districts.
    /// </summary>
    public class PlaceSuggestion
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>Turkish category label derived from OSM tags, e.g. "Tarihi Yer", "Müze" - null if unrecognized.</summary>
        public string? CategoryLabel { get; set; }

        public string? City { get; set; }
        public string? State { get; set; }
        public string? Country { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        /// <summary>Secondary line shown under the name, e.g. "Tarihi Yer · Ankara, Türkiye".</summary>
        public string DisplayRegion
        {
            get
            {
                var location = string.Join(", ", new[] { City, State, Country }
                    .Where(part => !string.IsNullOrWhiteSpace(part)));

                if (string.IsNullOrWhiteSpace(CategoryLabel))
                    return location;

                return string.IsNullOrWhiteSpace(location) ? CategoryLabel : $"{CategoryLabel} · {location}";
            }
        }
    }
}
