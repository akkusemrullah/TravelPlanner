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
    /// Free weather lookup via Open-Meteo (https://open-meteo.com) - no API key or billing
    /// account required, unlike OpenWeatherMap which needs a registered key even on its free
    /// tier. Open-Meteo's forecast endpoint takes coordinates, so a city/country is first
    /// resolved to lat/lon via the same free geocoding service the Map page uses.
    /// </summary>
    public class WeatherService : IWeatherService
    {
        private static readonly TimeSpan WeatherCacheDuration = TimeSpan.FromMinutes(15);

        private readonly HttpClient _httpClient;
        private readonly IGeocodingService _geocodingService;
        private readonly IConnectivity _connectivity;
        private readonly IResponseCache _cache;

        public WeatherService(HttpClient httpClient, IGeocodingService geocodingService, IConnectivity connectivity, IResponseCache cache)
        {
            _httpClient = httpClient;
            _geocodingService = geocodingService;
            _connectivity = connectivity;
            _cache = cache;
        }

        /// <summary>Convenience overload that maps a Trip's City/Country straight into the API query.</summary>
        public Task<WeatherInfo?> GetWeatherAsync(Trip trip) =>
            GetWeatherAsync(trip.City, trip.Country);

        public async Task<WeatherInfo?> GetWeatherAsync(string city, string country)
        {
            // Fail fast and let the caller show an "unavailable offline" message
            // instead of waiting on a request that can't succeed.
            // Current conditions change slowly - reuse a recent answer instead of calling the API again.
            var cacheKey = $"weather:{city?.Trim().ToLowerInvariant()}|{country?.Trim().ToLowerInvariant()}";
            if (_cache.TryGet<WeatherInfo>(cacheKey, out var cached) && cached is not null)
                return cached;

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                Debug.WriteLine("[WeatherService] Skipped: no internet access reported by Connectivity API.");
                return null;
            }

            try
            {
                var coords = await _geocodingService.GeocodeAsync(city, country);
                if (coords is null)
                {
                    // GeocodingService already logs the specific reason (no results, timeout,
                    // network error, etc.) to Debug Output - check there for the root cause.
                    Debug.WriteLine($"[WeatherService] Could not resolve coordinates for '{city}, {country}'.");
                    return null;
                }

                var lat = coords.Value.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var lon = coords.Value.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture);

                var url = $"{AppConstants.OpenMeteoForecastBaseUrl}?latitude={lat}&longitude={lon}" +
                          "&current=temperature_2m,relative_humidity_2m,wind_speed_10m,weather_code" +
                          "&wind_speed_unit=ms&timezone=auto";

                var response = await _httpClient.GetFromJsonAsync<OpenMeteoResponse>(url);
                if (response?.Current is null)
                {
                    Debug.WriteLine($"[WeatherService] Forecast response missing 'current' data for {lat},{lon}.");
                    return null;
                }

                var current = response.Current;
                var (description, emoji) = DescribeWeatherCode(current.WeatherCode);

                var weather = new WeatherInfo
                {
                    Temperature = current.Temperature2m,
                    Condition = description,
                    Humidity = current.RelativeHumidity2m,
                    WindSpeed = current.WindSpeed10m,
                    IconEmoji = emoji
                };

                _cache.Set(cacheKey, weather, WeatherCacheDuration);
                return weather;
            }
            catch (HttpRequestException ex)
            {
                // Non-success status code / DNS / TLS failure - API unreachable. If this keeps
                // happening, check the Debug Output for the exact message: a corporate firewall
                // or proxy silently blocking the domain looks like this too (not just "no internet").
                Debug.WriteLine($"[WeatherService] HttpRequestException for '{city}, {country}': {ex}");
                return null;
            }
            catch (TaskCanceledException ex)
            {
                Debug.WriteLine($"[WeatherService] Request timed out for '{city}, {country}': {ex}");
                return null;
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"[WeatherService] Unexpected response shape for '{city}, {country}': {ex}");
                return null;
            }
            catch (Exception ex)
            {
                // Catch-all so an unexpected platform-specific error doesn't crash the app -
                // still logged so it's possible to diagnose from the Debug Output window.
                Debug.WriteLine($"[WeatherService] Unexpected error for '{city}, {country}': {ex}");
                return null;
            }
        }

        // ================= Trip-day forecast (TP-402) =================

        private static readonly TimeSpan TripForecastCacheDuration = TimeSpan.FromHours(1);

        /// <summary>Test seam for "today" - the forecast/climate split depends on it.</summary>
        internal Func<DateTime> Today { get; set; } = () => DateTime.Today;

        public async Task<List<DailyForecast>?> GetTripForecastAsync(Trip trip)
        {
            if (trip is null || string.IsNullOrWhiteSpace(trip.City))
                return null;

            var start = trip.StartDate.Date;
            var end = trip.EndDate.Date < start ? start : trip.EndDate.Date;
            if ((end - start).Days >= AppConstants.MaxItineraryDays)
                end = start.AddDays(AppConstants.MaxItineraryDays - 1);

            var today = Today().Date;
            var cacheKey = $"tripforecast:{trip.City.Trim().ToLowerInvariant()}|{trip.Country?.Trim().ToLowerInvariant()}|{start:yyyyMMdd}|{end:yyyyMMdd}|{today:yyyyMMdd}|{LocalizationHelper.ApiLanguage}";
            if (_cache.TryGet<List<DailyForecast>>(cacheKey, out var cached) && cached is not null)
                return cached.ToList();

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                return null;

            try
            {
                var coords = await _geocodingService.GeocodeAsync(trip.City, trip.Country);
                if (coords is null)
                    return null;

                var (lat, lon) = (coords.Value.Latitude, coords.Value.Longitude);
                var plan = PlanTripForecast(start, end, today);
                var result = new List<DailyForecast>();

                // The three ranges are independent - fetch them in parallel.
                var forecastTask = plan.Forecast is { } f ? FetchDailyAsync(AppConstants.OpenMeteoForecastBaseUrl, lat, lon, f.Start, f.End, includeProbability: true) : Task.FromResult<List<DailyValues>?>(null);
                var observedTask = plan.Observed is { } o ? FetchDailyAsync(AppConstants.OpenMeteoArchiveBaseUrl, lat, lon, o.Start, o.End, includeProbability: false) : Task.FromResult<List<DailyValues>?>(null);
                var climateTask = plan.Climate is { } c ? FetchClimateAverageAsync(lat, lon, c.Start, c.End, today) : Task.FromResult<List<DailyForecast>?>(null);

                await Task.WhenAll(forecastTask, observedTask, climateTask);

                if (await observedTask is { } observed)
                    result.AddRange(observed.Select(d => ToForecast(d, ForecastKind.Observed)));

                if (await forecastTask is { } forecast)
                    result.AddRange(forecast.Select(d => ToForecast(d, d.Date < today ? ForecastKind.Observed : ForecastKind.Forecast)));

                if (await climateTask is { } climate)
                    result.AddRange(climate);

                result = result
                    .Where(d => d.Date >= start && d.Date <= end)
                    .GroupBy(d => d.Date)
                    .Select(g => g.First())
                    .OrderBy(d => d.Date)
                    .ToList();

                if (result.Count == 0)
                    return null;

                _cache.Set(cacheKey, result, TripForecastCacheDuration);
                return result.ToList();
            }
            catch (Exception ex)
            {
                // Same policy as GetWeatherAsync: never crash the page, log and report "unavailable".
                Debug.WriteLine($"[WeatherService] Trip forecast failed for '{trip.City}, {trip.Country}': {ex}");
                return null;
            }
        }

        internal readonly record struct DateRange(DateTime Start, DateTime End);

        internal readonly record struct TripForecastPlan(DateRange? Observed, DateRange? Forecast, DateRange? Climate);

        /// <summary>
        /// Splits the trip into: days older than the forecast endpoint's past window (archive = recorded
        /// weather), days inside [today - ForecastPastDays, today + ForecastHorizonDays - 1] (forecast endpoint),
        /// and days beyond the horizon (climate average).
        /// </summary>
        internal static TripForecastPlan PlanTripForecast(DateTime start, DateTime end, DateTime today)
        {
            var windowStart = today.AddDays(-AppConstants.ForecastPastDays);
            var windowEnd = today.AddDays(AppConstants.ForecastHorizonDays - 1);

            DateRange? Clip(DateTime from, DateTime to)
            {
                var s = from > start ? from : start;
                var e = to < end ? to : end;
                return s <= e ? new DateRange(s, e) : null;
            }

            return new TripForecastPlan(
                Observed: Clip(DateTime.MinValue, windowStart.AddDays(-1)),
                Forecast: Clip(windowStart, windowEnd),
                Climate: Clip(windowEnd.AddDays(1), DateTime.MaxValue.Date));
        }

        /// <summary>
        /// Averages the same calendar days over the previous <see cref="AppConstants.ClimateAverageYears"/> years.
        /// The whole range is shifted by the same number of years so it stays contiguous (and 29 February
        /// simply maps to the 28th in non-leap years).
        /// </summary>
        private async Task<List<DailyForecast>?> FetchClimateAverageAsync(double lat, double lon, DateTime start, DateTime end, DateTime today)
        {
            var dayCount = (end - start).Days + 1;
            var requests = Enumerable.Range(1, AppConstants.ClimateAverageYears).Select(k =>
            {
                var years = start.Year - (today.Year - k);
                return (Years: years, Task: FetchDailyAsync(AppConstants.OpenMeteoArchiveBaseUrl, lat, lon, start.AddYears(-years), end.AddYears(-years), includeProbability: false));
            }).ToList();

            await Task.WhenAll(requests.Select(r => r.Task));

            var samples = new List<List<DailyValues>>();
            foreach (var request in requests)
            {
                if (await request.Task is { Count: > 0 } values)
                    samples.Add(values);
            }

            return samples.Count == 0 ? null : AverageYears(start, dayCount, samples);
        }

        /// <summary>Combines per-year samples into per-day averages, matching days by month and day.</summary>
        internal static List<DailyForecast> AverageYears(DateTime start, int dayCount, IReadOnlyList<IReadOnlyList<DailyValues>> samples)
        {
            var result = new List<DailyForecast>(dayCount);

            // Match each past year's values by calendar day (month + day), not by position: a range that crosses
            // 29 February has one day more or less in a leap year, which would shift every later day.
            var byCalendarDay = samples
                .Select(sample => sample
                    .GroupBy(v => (v.Date.Month, v.Date.Day))
                    .ToDictionary(g => g.Key, g => g.First()))
                .ToList();

            for (var i = 0; i < dayCount; i++)
            {
                var date = start.AddDays(i);
                var day = byCalendarDay
                    .Select(sample => sample.TryGetValue((date.Month, date.Day), out var value) ? value : null)
                    .Where(value => value is not null)
                    .Select(value => value!)
                    .ToList();
                if (day.Count == 0)
                    continue;

                var maxValues = day.Where(d => d.TemperatureMax is not null).Select(d => d.TemperatureMax!.Value).ToList();
                var minValues = day.Where(d => d.TemperatureMin is not null).Select(d => d.TemperatureMin!.Value).ToList();
                var rainValues = day.Where(d => d.PrecipitationSum is not null).Select(d => d.PrecipitationSum!.Value).ToList();

                // Most frequent weather code; on a tie the more "eventful" (higher) code wins.
                var code = day.Where(d => d.WeatherCode is not null)
                    .GroupBy(d => d.WeatherCode!.Value)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .Select(g => g.Key)
                    .FirstOrDefault(-1);

                var (description, emoji) = DescribeWeatherCode(code);

                result.Add(new DailyForecast
                {
                    Date = date,
                    TemperatureMax = maxValues.Count > 0 ? Math.Round(maxValues.Average(), 1) : null,
                    TemperatureMin = minValues.Count > 0 ? Math.Round(minValues.Average(), 1) : null,
                    PrecipitationSum = rainValues.Count > 0 ? Math.Round(rainValues.Average(), 1) : null,
                    PrecipitationProbability = rainValues.Count > 0
                        ? (int)Math.Round(100.0 * rainValues.Count(r => r >= 1.0) / rainValues.Count)
                        : null,
                    WeatherCode = code,
                    Condition = description,
                    IconEmoji = emoji,
                    Kind = ForecastKind.ClimateAverage
                });
            }

            return result;
        }

        private static DailyForecast ToForecast(DailyValues values, ForecastKind kind)
        {
            var code = values.WeatherCode ?? -1;
            var (description, emoji) = DescribeWeatherCode(code);
            return new DailyForecast
            {
                Date = values.Date,
                TemperatureMax = values.TemperatureMax,
                TemperatureMin = values.TemperatureMin,
                PrecipitationSum = values.PrecipitationSum,
                PrecipitationProbability = values.PrecipitationProbability,
                WeatherCode = code,
                Condition = description,
                IconEmoji = emoji,
                Kind = kind
            };
        }

        private async Task<List<DailyValues>?> FetchDailyAsync(string baseUrl, double lat, double lon, DateTime start, DateTime end, bool includeProbability)
        {
            try
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var daily = "weather_code,temperature_2m_max,temperature_2m_min,precipitation_sum" +
                            (includeProbability ? ",precipitation_probability_max" : string.Empty);

                var url = $"{baseUrl}?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                          $"&daily={daily}&timezone=auto" +
                          $"&start_date={start.ToString("yyyy-MM-dd", inv)}&end_date={end.ToString("yyyy-MM-dd", inv)}";

                var response = await _httpClient.GetFromJsonAsync<DailyResponse>(url);
                return response?.Daily?.ToValues();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WeatherService] Daily request failed ({baseUrl}, {start:yyyy-MM-dd}..{end:yyyy-MM-dd}): {ex.Message}");
                return null;
            }
        }

        /// <summary>One day of Open-Meteo's "daily" block.</summary>
        internal sealed record DailyValues(DateTime Date, int? WeatherCode, double? TemperatureMax, double? TemperatureMin, double? PrecipitationSum, int? PrecipitationProbability);

        private class DailyResponse
        {
            [JsonPropertyName("daily")]
            public DailyBlock? Daily { get; set; }
        }

        private class DailyBlock
        {
            [JsonPropertyName("time")]
            public List<string>? Time { get; set; }

            [JsonPropertyName("weather_code")]
            public List<int?>? WeatherCode { get; set; }

            [JsonPropertyName("temperature_2m_max")]
            public List<double?>? TemperatureMax { get; set; }

            [JsonPropertyName("temperature_2m_min")]
            public List<double?>? TemperatureMin { get; set; }

            [JsonPropertyName("precipitation_sum")]
            public List<double?>? PrecipitationSum { get; set; }

            [JsonPropertyName("precipitation_probability_max")]
            public List<int?>? PrecipitationProbability { get; set; }

            public List<DailyValues> ToValues()
            {
                var values = new List<DailyValues>();
                if (Time is null)
                    return values;

                for (var i = 0; i < Time.Count; i++)
                {
                    if (!DateTime.TryParseExact(Time[i], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var date))
                        continue;

                    values.Add(new DailyValues(
                        date,
                        At(WeatherCode, i),
                        At(TemperatureMax, i),
                        At(TemperatureMin, i),
                        At(PrecipitationSum, i),
                        At(PrecipitationProbability, i)));
                }

                return values;
            }

            private static T? At<T>(List<T?>? list, int index) where T : struct =>
                list is not null && index < list.Count ? list[index] : null;
        }

        /// <summary>
        /// Maps Open-Meteo's WMO weather codes (https://open-meteo.com/en/docs, "WMO Weather
        /// interpretation codes") to a localized description + emoji, since the API itself only
        /// returns numeric codes rather than ready-made text.
        /// </summary>
        internal static (string Description, string Emoji) DescribeWeatherCode(int code) => code switch
        {
            0 => (AppResources.Weather_Clear, "☀️"),
            1 => (AppResources.Weather_MainlyClear, "🌤️"),
            2 => (AppResources.Weather_PartlyCloudy, "⛅"),
            3 => (AppResources.Weather_Overcast, "☁️"),
            45 or 48 => (AppResources.Weather_Fog, "🌫️"),
            51 or 53 or 55 => (AppResources.Weather_Drizzle, "🌦️"),
            56 or 57 => (AppResources.Weather_FreezingDrizzle, "🌧️"),
            61 or 63 or 65 => (AppResources.Weather_Rain, "🌧️"),
            66 or 67 => (AppResources.Weather_FreezingRain, "🌧️"),
            71 or 73 or 75 or 77 => (AppResources.Weather_Snow, "❄️"),
            80 or 81 or 82 => (AppResources.Weather_RainShowers, "🌧️"),
            85 or 86 => (AppResources.Weather_SnowShowers, "🌨️"),
            95 => (AppResources.Weather_Thunderstorm, "⛈️"),
            96 or 99 => (AppResources.Weather_ThunderstormHail, "⛈️"),
            _ => (AppResources.Weather_Unknown, "🌡️")
        };

        // ---- Internal DTOs matching Open-Meteo's JSON shape (kept private, only WeatherInfo is exposed) ----

        private class OpenMeteoResponse
        {
            [JsonPropertyName("current")]
            public CurrentWeather? Current { get; set; }
        }

        private class CurrentWeather
        {
            [JsonPropertyName("temperature_2m")]
            public double Temperature2m { get; set; }

            [JsonPropertyName("relative_humidity_2m")]
            public int RelativeHumidity2m { get; set; }

            [JsonPropertyName("wind_speed_10m")]
            public double WindSpeed10m { get; set; }

            [JsonPropertyName("weather_code")]
            public int WeatherCode { get; set; }
        }
    }
}
