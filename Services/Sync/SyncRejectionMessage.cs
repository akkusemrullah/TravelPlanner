using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services.Sync
{
    /// <summary>
    /// TP-6: the text that tells the user which of their changes the server refused for good, and why - one
    /// paragraph per reason ("Bu seyahatte artık düzenleme yetkin yok, değişikliğin geri alındı: “Taksi”").
    /// </summary>
    public static class SyncRejectionMessage
    {
        /// <summary>Records named per paragraph; the rest are counted ("… ve 2 kayıt daha").</summary>
        internal const int MaxTitles = 3;

        public static string Format(IReadOnlyCollection<RejectedChange> rejections)
        {
            var paragraphs = rejections
                .GroupBy(r => FormatFor(r.Reason))
                .Select(group => string.Format(group.Key, Records(group.ToList())));
            return string.Join(Environment.NewLine + Environment.NewLine, paragraphs);
        }

        private static string FormatFor(string reason) => reason switch
        {
            "forbidden" => AppResources.Sync_RejectedForbiddenFormat,
            "row_deleted" or "trip_deleted" => AppResources.Sync_RejectedDeletedFormat,
            "merged" => AppResources.Sync_MergedFormat,
            _ => AppResources.Sync_RejectedOtherFormat,
        };

        /// <summary>
        /// 28: "“Roma” seyahati silindi ya da artık erişimin yok." - or, for several, how many.
        /// </summary>
        public static string FormatLostTrips(IReadOnlyCollection<string> tripNames) => tripNames.Count == 1
            ? string.Format(AppResources.Sync_SharedTripRemovedFormat, Helpers.InputLimits.Truncate(tripNames.First().Trim(), 60))
            : string.Format(AppResources.Sync_SharedTripsRemovedFormat, tripNames.Count);

        /// <summary>“Taksi”, “Müze” - or "2 kayıt" when none of them has a name (e.g. expense shares).</summary>
        private static string Records(List<RejectedChange> group)
        {
            var titles = group
                .Select(r => r.Title?.Trim())
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (titles.Count == 0)
                return string.Format(AppResources.Sync_RejectedCountFormat, group.Count);

            var named = string.Join(", ", titles.Take(MaxTitles).Select(t => $"“{Helpers.InputLimits.Truncate(t!, 60)}”"));
            return titles.Count > MaxTitles
                ? string.Format(AppResources.Sync_RejectedMoreFormat, named, titles.Count - MaxTitles)
                : named;
        }
    }
}
