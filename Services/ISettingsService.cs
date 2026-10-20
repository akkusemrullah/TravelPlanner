namespace TravelPlanner.Services
{
    public enum AppThemeOption
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>"system" follows the device; otherwise a two-letter language code ("tr", "en").</summary>
    public static class AppLanguages
    {
        public const string System = "system";
        public const string Turkish = "tr";
        public const string English = "en";
    }

    /// <summary>
    /// User preferences from the Ayarlar (Settings) page (TP-305), persisted with MAUI Preferences.
    /// </summary>
    public interface ISettingsService
    {
        /// <summary>
        /// Name shown in the Dashboard greeting and the profile. Each account has its own; the guest's starts empty
        /// (the profile then shows "Travel Planner"). Empty = greet without a name.
        /// </summary>
        string UserName { get; set; }

        /// <summary>Whose name <see cref="UserName"/> reads and writes: an account (uid) or the guest (null). Set by AccountService.</summary>
        void UseAccount(string? userId);

        /// <summary>ISO currency code amounts are displayed in (TRY, EUR, USD, GBP).</summary>
        string CurrencyCode { get; set; }

        /// <summary>Hour of day (0-23) the day-before trip reminder fires.</summary>
        int ReminderHour { get; set; }

        AppThemeOption Theme { get; set; }

        /// <summary>See <see cref="AppLanguages"/>.</summary>
        string Language { get; set; }

        /// <summary>
        /// Defaults from the admin panel (migration 29) for <see cref="CurrencyCode"/> and <see cref="ReminderHour"/>,
        /// used until the user picks their own.
        /// </summary>
        void UseDefaults(string currencyCode, int reminderHour);
    }
}
