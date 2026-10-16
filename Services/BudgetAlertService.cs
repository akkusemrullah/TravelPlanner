using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    public class BudgetAlertService : IBudgetAlertService
    {
        private const string PreferenceKeyPrefix = "budget_alert.";

        private readonly ITripService _tripService;
        private readonly IExpenseService _expenseService;
        private readonly INotificationService _notificationService;
        private readonly IDialogService _dialogs;
        private readonly IPreferences _preferences;

        public BudgetAlertService(
            ITripService tripService,
            IExpenseService expenseService,
            INotificationService notificationService,
            IDialogService dialogs,
            IPreferences preferences)
        {
            _tripService = tripService;
            _expenseService = expenseService;
            _notificationService = notificationService;
            _dialogs = dialogs;
            _preferences = preferences;
        }

        /// <summary>0, 80 or 100 - the highest threshold <paramref name="usagePercent"/> has reached.</summary>
        public static int GetThreshold(double usagePercent) =>
            usagePercent >= AppConstants.BudgetExceededPercent ? AppConstants.BudgetExceededPercent
            : usagePercent >= AppConstants.BudgetWarningPercent ? AppConstants.BudgetWarningPercent
            : 0;

        public async Task CheckAsync(int tripId)
        {
            // A failed alert must never turn a successful save into an error dialog - log and move on.
            try
            {
                await CheckCoreAsync(tripId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BudgetAlertService] Check failed for trip {tripId}: {ex}");
            }
        }

        /// <summary>
        /// Preference key of a trip's last alert level. Keyed by the trip's global SyncId: local ids repeat across
        /// the guest's and each account's database (G3), so trip 1 of one must not silence trip 1 of another.
        /// </summary>
        internal static string KeyFor(int tripId, string? syncId) =>
            PreferenceKeyPrefix + (string.IsNullOrEmpty(syncId) ? tripId.ToString(System.Globalization.CultureInfo.InvariantCulture) : syncId);

        private async Task CheckCoreAsync(int tripId)
        {
            var trip = await _tripService.GetByIdAsync(tripId);
            var key = KeyFor(tripId, trip?.SyncId);

            // One-time move of a level saved under the old id-based key, so an alert already shown isn't repeated.
            var legacyKey = KeyFor(tripId, null);
            if (key != legacyKey && _preferences.ContainsKey(legacyKey))
            {
                if (!_preferences.ContainsKey(key))
                    _preferences.Set(key, _preferences.Get(legacyKey, 0));
                _preferences.Remove(legacyKey);
            }

            if (trip is null || trip.PlannedBudget <= 0)
            {
                _preferences.Remove(key);
                return;
            }

            var usage = await _expenseService.GetBudgetUsagePercentageAsync(tripId, trip.PlannedBudget);
            var threshold = GetThreshold(usage);
            var lastNotified = _preferences.Get(key, 0);

            // Remember the current level either way, so dropping below a threshold re-arms it.
            if (threshold != lastNotified)
                _preferences.Set(key, threshold);

            if (threshold <= lastNotified)
                return;

            var shown = await _notificationService.ShowBudgetAlertAsync(trip, threshold, usage);
            if (!shown)
            {
                // No notification support/permission (e.g. Windows) - tell the user in the app instead.
                var (title, message) = BuildMessage(trip.Name, threshold, usage);
                await _dialogs.ShowMessageAsync(title, message);
            }
        }

        public static (string Title, string Message) BuildMessage(string tripName, int threshold, double usagePercent) =>
            threshold >= AppConstants.BudgetExceededPercent
                ? (AppResources.BudgetAlert_ExceededTitle, string.Format(AppResources.BudgetAlert_ExceededBody, tripName, Math.Round(usagePercent)))
                : (AppResources.BudgetAlert_WarningTitle, string.Format(AppResources.BudgetAlert_WarningBody, tripName, Math.Round(usagePercent)));
    }
}
