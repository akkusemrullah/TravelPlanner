namespace TravelPlanner.Models
{
    /// <summary>
    /// One geocoded city result shown in the "Şehir" autocomplete dropdown on TripFormPage.
    /// Not persisted - built on the fly from GeocodingService.SearchCitiesAsync results.
    /// </summary>
    public class CitySuggestion
    {
        public string Name { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string? CountryCode { get; set; }
        public string? Admin1 { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }

        /// <summary>Secondary line shown under the city name, e.g. "Lazio, İtalya" or just "İtalya".</summary>
        public string DisplayRegion => string.IsNullOrWhiteSpace(Admin1) ? Country : $"{Admin1}, {Country}";
    }
}
