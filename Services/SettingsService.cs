using TravelPlanner.Helpers;

namespace TravelPlanner.Services
{
    public class SettingsService : ISettingsService
    {
        public const int DefaultReminderHour = 9;

        /// <summary>Before accounts had their own names, one name was kept for the whole device.</summary>
        internal const string LegacyUserNameKey = "settings.user_name";
        internal const string GuestUserNameKey = "settings.user_name.guest";
        private const string CurrencyKey = "settings.currency";
        private const string ReminderHourKey = "settings.reminder_hour";
        private const string ThemeKey = "settings.theme";
        private const string LanguageKey = "settings.language";
        internal const string DefaultCurrencyKey = "remote.default_currency";
        internal const string DefaultReminderHourKey = "remote.default_reminder_hour";

        private readonly IPreferences _preferences;
        private string _userNameKey = GuestUserNameKey;

        public SettingsService(IPreferences preferences)
        {
            _preferences = preferences;

            // Amounts are formatted through a static helper (used by model computed properties too),
            // so push the saved currency into it as soon as settings exist.
            MoneyFormatter.CurrencyCode = CurrencyCode;
        }

        public string UserName
        {
            get => _preferences.Get(_userNameKey, string.Empty);
            // "Ad Soyad": every word capitalised ("emrullah akkuş" -> "Emrullah Akkuş").
            set => _preferences.Set(_userNameKey, TextCase.CapitalizeWords(InputLimits.Truncate((value ?? string.Empty).Trim(), InputLimits.MaxUserNameLength)) ?? string.Empty);
        }

        public void UseAccount(string? userId)
        {
            if (userId is null)
            {
                _userNameKey = GuestUserNameKey;
                return;
            }

            _userNameKey = $"{LegacyUserNameKey}.{userId}";

            // The old device-wide name almost always came from signing in (the Google / sign-up name), so it
            // goes to the first account that opens here - the guest starts without a name ("Travel Planner").
            var legacy = _preferences.Get(LegacyUserNameKey, string.Empty);
            if (legacy.Length > 0)
            {
                if (_preferences.Get(_userNameKey, string.Empty).Length == 0)
                    _preferences.Set(_userNameKey, legacy);
                _preferences.Remove(LegacyUserNameKey);
            }
        }

        public string CurrencyCode
        {
            get
            {
                var code = _preferences.Get(CurrencyKey, _preferences.Get(DefaultCurrencyKey, MoneyFormatter.DefaultCurrencyCode));
                return MoneyFormatter.SupportedCurrencies.Contains(code) ? code : MoneyFormatter.DefaultCurrencyCode;
            }
            set
            {
                var code = MoneyFormatter.SupportedCurrencies.Contains(value) ? value : MoneyFormatter.DefaultCurrencyCode;
                _preferences.Set(CurrencyKey, code);
                MoneyFormatter.CurrencyCode = code;
            }
        }

        public int ReminderHour
        {
            get => Math.Clamp(_preferences.Get(ReminderHourKey, _preferences.Get(DefaultReminderHourKey, DefaultReminderHour)), 0, 23);
            set => _preferences.Set(ReminderHourKey, Math.Clamp(value, 0, 23));
        }

        public void UseDefaults(string currencyCode, int reminderHour)
        {
            _preferences.Set(DefaultCurrencyKey, MoneyFormatter.SupportedCurrencies.Contains(currencyCode) ? currencyCode : MoneyFormatter.DefaultCurrencyCode);
            _preferences.Set(DefaultReminderHourKey, Math.Clamp(reminderHour, 0, 23));
            MoneyFormatter.CurrencyCode = CurrencyCode;
        }

        public AppThemeOption Theme
        {
            get => Enum.IsDefined(typeof(AppThemeOption), _preferences.Get(ThemeKey, 0))
                ? (AppThemeOption)_preferences.Get(ThemeKey, 0)
                : AppThemeOption.System;
            set => _preferences.Set(ThemeKey, (int)value);
        }

        public string Language
        {
            get
            {
                var language = _preferences.Get(LanguageKey, AppLanguages.System);
                return language is AppLanguages.Turkish or AppLanguages.English ? language : AppLanguages.System;
            }
            set => _preferences.Set(LanguageKey, value is AppLanguages.Turkish or AppLanguages.English ? value : AppLanguages.System);
        }
    }
}
