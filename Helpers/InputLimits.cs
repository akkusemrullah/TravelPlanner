using System.Collections.Concurrent;
using System.Reflection;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Hard limits for everything a user (or a restored backup) can put into the app, kept in one place so the
    /// forms, the services and the backup import all agree:
    /// - money: one amount (an expense, a budget, a converted amount) is at most <see cref="MaxMoneyAmount"/>.
    ///   Amounts are stored as long minor units; this cap keeps every multiplication (currency conversion) and
    ///   every SQL SUM far away from the long/decimal overflow that would otherwise crash a save or a total.
    /// - exchange rates must lie in [<see cref="MinExchangeRate"/>, <see cref="MaxExchangeRate"/>] - anything
    ///   else is a broken API answer or a corrupt stored value, not a real ECB rate.
    /// - dates must lie in [<see cref="MinDate"/>, <see cref="MaxDate"/>] (the DatePicker range), so date math
    ///   such as "start - 1 day" for reminders can never fall off DateTime's range.
    /// - text: string properties carry DataAnnotations [MaxLength] on the models. SQLite does not enforce
    ///   lengths, so <see cref="EnforceTextLimits"/> trims them before every save.
    /// </summary>
    public static class InputLimits
    {
        /// <summary>Largest single amount accepted (≈10 billion in any currency - JPY/HUF budgets included).</summary>
        public const decimal MaxMoneyAmount = 9_999_999_999.99m;

        public static long MaxMoneyMinor => MoneyConverter.ToMinor(MaxMoneyAmount);

        public const decimal MinExchangeRate = 0.000001m;
        public const decimal MaxExchangeRate = 1_000_000m;

        public static readonly DateTime MinDate = new(1900, 1, 1);
        public static readonly DateTime MaxDate = new(2100, 12, 31);

        /// <summary>Settings → name (shown in the dashboard greeting).</summary>
        public const int MaxUserNameLength = 40;

        /// <summary>Search boxes / autocomplete queries sent to the free web APIs.</summary>
        public const int MaxSearchQueryLength = 100;

        public static bool IsValidAmount(decimal amount) => amount >= 0 && amount <= MaxMoneyAmount;

        public static bool IsValidMinor(long minor) => minor >= 0 && minor <= MaxMoneyMinor;

        public static bool IsValidRate(decimal rate) => rate >= MinExchangeRate && rate <= MaxExchangeRate;

        public static bool IsValidRate(double rate) =>
            !double.IsNaN(rate) && !double.IsInfinity(rate) && rate >= (double)MinExchangeRate && rate <= (double)MaxExchangeRate;

        public static bool IsValidDate(DateTime date) => date >= MinDate && date <= MaxDate.AddDays(1).AddTicks(-1);

        /// <summary>Text cut to <paramref name="maxLength"/> characters (null stays null).</summary>
        public static string? Truncate(string? text, int maxLength) =>
            text is null || text.Length <= maxLength ? text : text[..maxLength];

        private static readonly ConcurrentDictionary<Type, (PropertyInfo Property, int Length)[]> TextProperties = new();

        /// <summary>
        /// Trims every writable string property of <paramref name="entity"/> that has a DataAnnotations
        /// [MaxLength] down to that length. Returns the same instance for chaining.
        /// </summary>
        public static T EnforceTextLimits<T>(T entity) where T : class
        {
            var properties = TextProperties.GetOrAdd(entity.GetType(), type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.CanRead && p.CanWrite)
                .Select(p => (Property: p, Attribute: p.GetCustomAttribute<System.ComponentModel.DataAnnotations.MaxLengthAttribute>()))
                .Where(x => x.Attribute is { Length: > 0 })
                .Select(x => (x.Property, x.Attribute!.Length))
                .ToArray());

            foreach (var (property, length) in properties)
            {
                if (property.GetValue(entity) is string value && value.Length > length)
                    property.SetValue(entity, value[..length]);
            }

            return entity;
        }
    }
}
