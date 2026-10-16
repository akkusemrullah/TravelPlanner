namespace TravelPlanner.Helpers
{
    /// <summary>Converts between decimal money values and the integer minor units (kuruş/cent) stored in SQLite.</summary>
    public static class MoneyConverter
    {
        private const decimal MinorUnitsPerMajor = 100m;

        public static long ToMinor(decimal amount) =>
            (long)Math.Round(amount * MinorUnitsPerMajor, MidpointRounding.AwayFromZero);

        public static decimal FromMinor(long minor) => minor / MinorUnitsPerMajor;
    }
}
