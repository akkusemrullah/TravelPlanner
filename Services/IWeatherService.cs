using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Wraps calls to Open-Meteo's free weather forecast API (no API key/billing account
    /// needed) and maps the response to WeatherInfo. Implemented by WeatherService.
    /// </summary>
    public interface IWeatherService
    {
        /// <summary>Look up weather by explicit city/country.</summary>
        Task<WeatherInfo?> GetWeatherAsync(string city, string country);

        /// <summary>Convenience overload that reads City/Country straight off a Trip.</summary>
        Task<WeatherInfo?> GetWeatherAsync(Trip trip);

        /// <summary>
        /// Weather for every day of the trip (TP-402): Open-Meteo's daily forecast for days within the
        /// forecast range (recorded values for recent past days), and the average of the same calendar days
        /// over the past years for days further ahead. Null when offline or the city can't be resolved.
        /// </summary>
        Task<List<DailyForecast>?> GetTripForecastAsync(Trip trip);
    }
}
