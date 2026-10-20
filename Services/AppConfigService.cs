using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Helpers;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Settings set in the admin panel (public.app_config, migration 29): AI maintenance, the home page notice,
    /// the lowest app version and the defaults for users who haven't picked their own currency / reminder hour.
    /// </summary>
    /// <param name="MinAppVersion">Older app versions are asked to update; null = nobody.</param>
    public sealed record AppConfig(
        bool AiEnabled = true,
        string? AiPausedMessage = null,
        string? Announcement = null,
        string? MinAppVersion = null,
        string DefaultCurrency = MoneyFormatter.DefaultCurrencyCode,
        int DefaultReminderHour = SettingsService.DefaultReminderHour)
    {
        /// <summary>True when <paramref name="current"/> is older than <see cref="MinAppVersion"/> ("1.2" &lt; "1.10.0").</summary>
        public bool NeedsUpdate(string? current) => IsOlder(current, MinAppVersion);

        internal static bool IsOlder(string? current, string? minimum)
        {
            if (Parse(minimum) is not { } min || Parse(current) is not { } now)
                return false;
            for (var i = 0; i < 4; i++)
            {
                if (now[i] != min[i])
                    return now[i] < min[i];
            }
            return false;
        }

        private static int[]? Parse(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return null;
            var parts = version.Trim().Split('.');
            if (parts.Length > 4)
                return null;
            var numbers = new int[4];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return null;
            }
            return numbers;
        }
    }

    public interface IAppConfigService
    {
        /// <summary>The last settings fetched (kept between starts); the defaults before the first fetch.</summary>
        AppConfig Current { get; }

        /// <summary>Fetches the settings (at most every few minutes unless forced); on failure keeps <see cref="Current"/>.</summary>
        Task<AppConfig> RefreshAsync(bool force = false, CancellationToken cancellationToken = default);
    }

    public sealed class AppConfigService : IAppConfigService
    {
        internal const string CacheKey = "remote.app_config";
        internal static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseClient _cloud;
        private readonly IPreferences _preferences;
        private readonly ISettingsService _settings;
        private readonly Func<DateTime> _now;
        private DateTime _lastFetch = DateTime.MinValue;

        public AppConfigService(ISupabaseClient cloud, IPreferences preferences, ISettingsService settings, Func<DateTime>? now = null)
        {
            _cloud = cloud;
            _preferences = preferences;
            _settings = settings;
            _now = now ?? (() => DateTime.UtcNow);
            Current = Load();
        }

        public AppConfig Current { get; private set; }

        public async Task<AppConfig> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
        {
            if (!force && _now() - _lastFetch < MinInterval)
                return Current;
            _lastFetch = _now();

            try
            {
                var result = await _cloud.PublicRpcAsync("app_config", new JsonObject(), cancellationToken).ConfigureAwait(false);
                if (result.ValueKind != JsonValueKind.Object)
                    return Current;
                Current = Read(result);
                _preferences.Set(CacheKey, JsonSerializer.Serialize(Current, Json));
                _settings.UseDefaults(Current.DefaultCurrency, Current.DefaultReminderHour);
            }
            catch (Exception ex) when (ex is SyncException or JsonException or InvalidOperationException or FormatException)
            {
                System.Diagnostics.Debug.WriteLine($"[Config] Refresh failed: {ex.Message}");
            }
            return Current;
        }

        internal static AppConfig Read(JsonElement json) => new(
            AiEnabled: !(json.TryGetProperty("ai_enabled", out var ai) && ai.ValueKind == JsonValueKind.False),
            AiPausedMessage: Text(json, "ai_paused_message"),
            Announcement: Text(json, "announcement"),
            MinAppVersion: Text(json, "min_app_version"),
            DefaultCurrency: Text(json, "default_currency") is { } c && MoneyFormatter.SupportedCurrencies.Contains(c) ? c : MoneyFormatter.DefaultCurrencyCode,
            DefaultReminderHour: json.TryGetProperty("default_reminder_hour", out var h) && h.ValueKind == JsonValueKind.Number
                ? Math.Clamp(h.GetInt32(), 0, 23)
                : SettingsService.DefaultReminderHour);

        private static string? Text(JsonElement json, string key) =>
            json.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;

        private AppConfig Load()
        {
            try
            {
                var cached = _preferences.Get(CacheKey, string.Empty);
                if (cached.Length > 0 && JsonSerializer.Deserialize<AppConfig>(cached, Json) is { } config)
                    return config;
            }
            catch (JsonException)
            {
            }
            return new AppConfig();
        }
    }
}
