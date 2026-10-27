using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// TP-98: the small grey lines under the trip page's rows ("12 etkinlik", "3 bekleyen", "2/5 ziyaret edildi",
    /// "8 harcama · 4.250 ₺", "3 dosya"). Empty when there is nothing to count, so the row shows just its title.
    /// </summary>
    public static class TripDetailSummary
    {
        public static string Activities(int count) =>
            count > 0 ? string.Format(AppResources.TripDetail_ActivitiesFormat, count) : string.Empty;

        public static string Tasks(IReadOnlyCollection<TodoItem> todos)
        {
            if (todos.Count == 0)
                return string.Empty;
            var pending = todos.Count(t => !t.IsCompleted);
            return pending > 0 ? string.Format(AppResources.TripDetail_TasksPendingFormat, pending) : AppResources.TripDetail_TasksAllDone;
        }

        public static string Places(IReadOnlyCollection<Place> places) =>
            places.Count > 0 ? string.Format(AppResources.TripDetail_PlacesVisitedFormat, places.Count(p => p.IsVisited), places.Count) : string.Empty;

        public static string Expenses(int count, string formattedTotal) =>
            count > 0 ? string.Format(AppResources.TripDetail_ExpensesFormat, count, formattedTotal) : string.Empty;

        public static string Files(int count) =>
            count > 0 ? string.Format(AppResources.TripDetail_FilesFormat, count) : string.Empty;

        /// <summary>The budget bar's colour step: under 80 % normal, from 80 % a warning, over the budget danger.</summary>
        public static bool IsBudgetWarning(double usagePercentage, bool overBudget) => !overBudget && usagePercentage >= 80;
    }
}
