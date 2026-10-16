using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TravelPlanner.Helpers;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Frankfurter (https://frankfurter.dev) - free, open-source, no API key and no quotas - serving the
    /// European Central Bank's daily reference rates. ECB publishes once per working day, so answers are
    /// cached for a few hours in memory and the latest rate per pair is also kept in Preferences as an
    /// offline fallback.
    /// </summary>
    public class ExchangeRateService : IExchangeRateService
    {
        private static readonly TimeSpan LatestCacheDuration = TimeSpan.FromHours(6);
        private static readonly TimeSpan HistoricalCacheDuration = TimeSpan.FromDays(7);
        private const string PreferenceKeyPrefix = "fx.";

        private readonly HttpClient _httpClient;
        private readonly IConnectivity _connectivity;
        private readonly IResponseCache _cache;
        private readonly IPreferences _preferences;

        public ExchangeRateService(HttpClient httpClient, IConnectivity connectivity, IResponseCache cache, IPreferences preferences)
        {
            _httpClient = httpClient;
            _connectivity = connectivity;
            _cache = cache;
            _preferences = preferences;
        }

        /// <summary>Test seam for "today" (historical vs latest decision).</summary>
        internal Func<DateTime> Today { get; set; } = () => DateTime.Today;

        public async Task<ExchangeRateQuote?> GetRateAsync(string from, string to, DateTime? date = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return null;

            from = from.Trim().ToUpperInvariant();
            to = to.Trim().ToUpperInvariant();

            if (from == to)
                return new ExchangeRateQuote(from, to, 1m, Today());

            // ECB only has rates for past working days; today/future -> latest published rate.
            var historicalDate = date?.Date < Today() ? date.Value.Date : (DateTime?)null;
            var cacheKey = $"fx:{from}:{to}:{historicalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "latest"}";

            if (_cache.TryGet<ExchangeRateQuote>(cacheKey, out var cached) && cached is not null)
                return cached;

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                return ReadStoredRate(from, to);

            try
            {
                var path = historicalDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "latest";
                var url = $"{AppConstants.FrankfurterBaseUrl}/{path}?base={from}&symbols={to}";

                var response = await _httpClient.GetFromJsonAsync<FrankfurterResponse>(url, cancellationToken);
                // Outside the plausible range = a broken answer; never price expenses with it.
                if (response?.Rates is null || !response.Rates.TryGetValue(to, out var rate) || !InputLimits.IsValidRate(rate))
                {
                    Debug.WriteLine($"[ExchangeRateService] No {from}->{to} rate in the response.");
                    return ReadStoredRate(from, to);
                }

                var rateDate = DateTime.TryParseExact(response.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : historicalDate ?? Today();

                var quote = new ExchangeRateQuote(from, to, rate, rateDate);
                _cache.Set(cacheKey, quote, historicalDate is null ? LatestCacheDuration : HistoricalCacheDuration);

                if (historicalDate is null)
                    StoreRate(quote);

                return quote;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException)
            {
                Debug.WriteLine($"[ExchangeRateService] {from}->{to} failed: {ex}");
                return ReadStoredRate(from, to);
            }
            catch (Exception ex)
            {
                // Catch-all so a platform-specific error never crashes the expense form.
                Debug.WriteLine($"[ExchangeRateService] Unexpected error for {from}->{to}: {ex}");
                return ReadStoredRate(from, to);
            }
        }

        public bool TryGetCachedRate(string from, string to, out decimal rate)
        {
            rate = 0m;
            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
                return false;

            from = from.Trim().ToUpperInvariant();
            to = to.Trim().ToUpperInvariant();

            if (from == to)
            {
                rate = 1m;
                return true;
            }

            if (_cache.TryGet<ExchangeRateQuote>($"fx:{from}:{to}:latest", out var cached) && cached is not null)
            {
                rate = cached.Rate;
                return true;
            }

            var stored = ReadStoredRate(from, to);
            if (stored is null)
                return false;

            rate = stored.Rate;
            return true;
        }

        // ---- Offline fallback: "rate|yyyy-MM-dd" per pair in Preferences ----

        private void StoreRate(ExchangeRateQuote quote)
        {
            var value = string.Create(CultureInfo.InvariantCulture, $"{quote.Rate}|{quote.RateDate:yyyy-MM-dd}");
            _preferences.Set(PreferenceKeyPrefix + quote.From + "." + quote.To, value);
        }

        private ExchangeRateQuote? ReadStoredRate(string from, string to)
        {
            var direct = Parse(from, to, _preferences.Get(PreferenceKeyPrefix + from + "." + to, string.Empty));
            if (direct is not null)
                return direct;

            // The inverse pair is just as good: 1 / rate.
            var inverse = Parse(to, from, _preferences.Get(PreferenceKeyPrefix + to + "." + from, string.Empty));
            return inverse is null
                ? null
                : new ExchangeRateQuote(from, to, Math.Round(1m / inverse.Rate, 6), inverse.RateDate, IsOfflineFallback: true);
        }

        private static ExchangeRateQuote? Parse(string from, string to, string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return null;

            var parts = stored.Split('|');
            if (parts.Length != 2 ||
                !decimal.TryParse(parts[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || !InputLimits.IsValidRate(rate) ||
                !DateTime.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                return null;

            return new ExchangeRateQuote(from, to, rate, date, IsOfflineFallback: true);
        }

        // {"amount":1.0,"base":"EUR","date":"2026-10-07","rates":{"TRY":38.1234}}
        private sealed class FrankfurterResponse
        {
            [JsonPropertyName("date")]
            public string? Date { get; set; }

            [JsonPropertyName("rates")]
            public Dictionary<string, decimal>? Rates { get; set; }
        }
    }
}
