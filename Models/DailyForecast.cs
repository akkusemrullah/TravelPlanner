using System.Globalization;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Models
{
    /// <summary>Where a trip day's weather figures come from (TP-402).</summary>
    public enum ForecastKind
    {
        /// <summary>Open-Meteo daily forecast (today .. today + 15).</summary>
        Forecast = 0,

        /// <summary>Recorded weather for a day that has already passed.</summary>
        Observed = 1,

        /// <summary>Average of the same calendar day over the previous years (beyond the forecast range).</summary>
        ClimateAverage = 2
    }

    /// <summary>Weather for one day of a trip. Not persisted - built by WeatherService.GetTripForecastAsync.</summary>
    public class DailyForecast
    {
        public DateTime Date { get; set; }
        public double? TemperatureMax { get; set; }
        public double? TemperatureMin { get; set; }

        /// <summary>Total precipitation in mm (an average for <see cref="ForecastKind.ClimateAverage"/>).</summary>
        public double? PrecipitationSum { get; set; }

        /// <summary>
        /// Chance of precipitation in percent. For climate averages: the share of past years in which that day
        /// had at least 1 mm. Null when unknown (e.g. recorded days).
        /// </summary>
        public int? PrecipitationProbability { get; set; }

        public int WeatherCode { get; set; }
        public string Condition { get; set; } = string.Empty;
        public string IconEmoji { get; set; } = "🌡️";
        public ForecastKind Kind { get; set; }

        // ---- Display helpers ----

        public bool IsClimateAverage => Kind == ForecastKind.ClimateAverage;

        /// <summary>"Pzt 12 Eki" in the current culture.</summary>
        public string DateDisplay => Date.ToString("ddd d MMM", CultureInfo.CurrentCulture);

        /// <summary>"24° / 15°".</summary>
        public string TemperatureRange =>
            $"{FormatTemperature(TemperatureMax)} / {FormatTemperature(TemperatureMin)}";

        /// <summary>"%40" (probability) or "2,4 mm" (amount); empty when dry/unknown.</summary>
        public string PrecipitationDisplay
        {
            get
            {
                if (PrecipitationProbability is int probability && probability > 0)
                    return string.Format(CultureInfo.CurrentCulture, AppResources.Forecast_PrecipitationChanceFormat, probability);

                if (PrecipitationSum is double sum && sum >= 0.1)
                    return string.Format(CultureInfo.CurrentCulture, AppResources.Forecast_PrecipitationAmountFormat, sum);

                return string.Empty;
            }
        }

        public bool HasPrecipitation => !string.IsNullOrEmpty(PrecipitationDisplay);

        /// <summary>Small tag under the date: "Tahmin", "Gerçekleşen" or "Ortalama".</summary>
        public string KindLabel => Kind switch
        {
            ForecastKind.Observed => AppResources.Forecast_KindObserved,
            ForecastKind.ClimateAverage => AppResources.Forecast_KindAverage,
            _ => AppResources.Forecast_KindForecast,
        };

        private static string FormatTemperature(double? value) =>
            value is double v ? $"{Math.Round(v):0}°" : "–";
    }
}
