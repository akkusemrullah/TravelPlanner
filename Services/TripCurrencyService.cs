using System.Text.Json.Nodes;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>How changing a trip's currency ended (TP-11).</summary>
    public enum TripCurrencyChangeOutcome
    {
        Converted,
        /// <summary>The trip is in the cloud and the device is offline - nothing was changed.</summary>
        Offline,
        /// <summary>Another member changed the currency first - nothing was changed; the current state is being synced.</summary>
        ChangedElsewhere,
        /// <summary>The user may not edit this trip (any more).</summary>
        Forbidden,
        /// <summary>A converted amount would exceed the money limit - nothing was changed.</summary>
        TooLarge,
    }

    /// <summary>
    /// Changes a trip's currency together with all its expenses (TP-11). A trip in the cloud is converted by the
    /// server in one transaction (convert_trip_currency), so members editing at the same time can't end up with
    /// amounts in one currency and the label in another; that needs a connection. A guest's trip, or one not
    /// uploaded yet, is converted on the device as before.
    /// </summary>
    public interface ITripCurrencyService
    {
        Task<TripCurrencyChangeOutcome> ConvertAsync(int tripId, string fromCurrency, string toCurrency, decimal rate);
    }

    public class TripCurrencyService : ITripCurrencyService
    {
        public const string RpcName = "convert_trip_currency";

        private readonly IExpenseService _expenses;
        private readonly ITripAccessService _access;
        private readonly ISupabaseClient _client;
        private readonly ISyncService _sync;
        private readonly IConnectivity _connectivity;

        public TripCurrencyService(IExpenseService expenses, ITripAccessService access, ISupabaseClient client, ISyncService sync, IConnectivity connectivity)
        {
            _expenses = expenses;
            _access = access;
            _client = client;
            _sync = sync;
            _connectivity = connectivity;
        }

        public async Task<TripCurrencyChangeOutcome> ConvertAsync(int tripId, string fromCurrency, string toCurrency, decimal rate)
        {
            var access = await _access.GetAccessAsync(tripId);
            if (!access.IsSignedIn || !access.IsUploaded || string.IsNullOrEmpty(access.TripSyncId))
            {
                try
                {
                    await _expenses.ConvertTripExpensesAsync(tripId, fromCurrency, toCurrency, rate);
                    return TripCurrencyChangeOutcome.Converted;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return TripCurrencyChangeOutcome.TooLarge;
                }
            }

            if (!access.CanEdit)
                return TripCurrencyChangeOutcome.Forbidden;
            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                return TripCurrencyChangeOutcome.Offline;

            // This device's own waiting expenses go up first, so the server converts them too.
            await SyncQuietlyAsync();

            try
            {
                await _client.RpcAsync(RpcName, new JsonObject
                {
                    ["p_trip_id"] = access.TripSyncId,
                    ["p_from"] = fromCurrency,
                    ["p_to"] = toCurrency,
                    ["p_rate"] = rate,
                });
            }
            catch (SyncException ex)
            {
                if (MapError(ex) is not { } outcome)
                    throw;
                if (outcome == TripCurrencyChangeOutcome.ChangedElsewhere)
                    _sync.RequestSync(TimeSpan.Zero);
                return outcome;
            }

            // The converted trip and expenses come back with the next pull.
            await SyncQuietlyAsync();
            return TripCurrencyChangeOutcome.Converted;
        }

        internal static TripCurrencyChangeOutcome? MapError(SyncException ex) => ex.Kind switch
        {
            SyncErrorKind.Network => TripCurrencyChangeOutcome.Offline,
            _ => ex.ServerCode switch
            {
                "TPC01" => TripCurrencyChangeOutcome.ChangedElsewhere,
                "42501" or "P0002" => TripCurrencyChangeOutcome.Forbidden,
                "22003" => TripCurrencyChangeOutcome.TooLarge,
                _ => ex.Status == 403 ? TripCurrencyChangeOutcome.Forbidden : null,
            },
        };

        private async Task SyncQuietlyAsync()
        {
            try
            {
                await _sync.SyncNowAsync();
            }
            catch (Exception ex)
            {
                // A failed round is retried by the sync engine; the conversion itself is what matters here.
                System.Diagnostics.Debug.WriteLine($"[TripCurrency] Sync round failed: {ex.Message}");
            }
        }
    }
}
