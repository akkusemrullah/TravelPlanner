namespace TravelPlanner.Services
{
    /// <summary>A conversion rate: 1 <see cref="From"/> = <see cref="Rate"/> <see cref="To"/>, as published on <see cref="RateDate"/>.</summary>
    public sealed record ExchangeRateQuote(string From, string To, decimal Rate, DateTime RateDate, bool IsOfflineFallback = false)
    {
        public decimal Convert(decimal amount) => Math.Round(amount * Rate, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Currency conversion via Frankfurter's free, keyless ECB reference rates (TP-403). The last rate fetched
    /// for each pair is remembered on the device, so a conversion still works offline with a slightly older rate.
    /// </summary>
    public interface IExchangeRateService
    {
        /// <summary>
        /// Rate from <paramref name="from"/> to <paramref name="to"/>. <paramref name="date"/> asks for that day's
        /// reference rate (past days only; today/future use the latest). Null when no rate can be obtained.
        /// </summary>
        Task<ExchangeRateQuote?> GetRateAsync(string from, string to, DateTime? date = null, CancellationToken cancellationToken = default);

        /// <summary>Synchronous lookup of the last known rate (memory or device storage) - never hits the network.</summary>
        bool TryGetCachedRate(string from, string to, out decimal rate);
    }
}
