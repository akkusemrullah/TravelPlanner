using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class ActivityService : IActivityService
    {
        private readonly IDatabaseService _databaseService;

        public ActivityService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task<List<TripActivity>> GetByTripIdAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            return await db.Table<TripActivity>()
                .Where(a => a.TripId == tripId)
                .OrderBy(a => a.Date)
                .ThenBy(a => a.SortOrder)
                .ToListAsync();
        }

        public async Task<List<TripActivity>> GetByDateAsync(int tripId, DateTime date)
        {
            var day = date.Date;
            var db = await _databaseService.Connection;
            return await db.Table<TripActivity>()
                .Where(a => a.TripId == tripId && a.Date == day)
                .OrderBy(a => a.SortOrder)
                .ToListAsync();
        }

        public async Task<TripActivity?> GetByIdAsync(int id)
        {
            var db = await _databaseService.Connection;
            return await db.Table<TripActivity>().Where(a => a.Id == id).FirstOrDefaultAsync();
        }

        public async Task SaveAsync(TripActivity activity)
        {
            activity.Date = activity.Date.Date;
            Helpers.InputLimits.EnforceTextLimits(activity);
            activity.Title = Helpers.TextCase.CapitalizeFirst(activity.Title) ?? string.Empty;
            activity.Note = Helpers.TextCase.CapitalizeFirst(activity.Note);
            if (activity.StartTime is null)
                activity.EndTime = null;

            var db = await _databaseService.Connection;

            await db.RunInTransactionAsync(conn =>
            {
                var stored = activity.Id == 0 ? null : conn.Find<TripActivity>(activity.Id);
                var needsSlot = stored is null || stored.Date.Date != activity.Date || stored.StartTime != activity.StartTime;

                if (!needsSlot)
                {
                    conn.UpdateTracked(activity);
                    return;
                }

                var tripId = activity.TripId;
                var day = activity.Date;
                var id = activity.Id;
                var others = conn.Table<TripActivity>()
                    .Where(a => a.TripId == tripId && a.Date == day && a.Id != id)
                    .OrderBy(a => a.SortOrder)
                    .ToList();

                var index = ItineraryOrdering.FindInsertIndex(others, activity.StartTime);
                others.Insert(index, activity);

                if (activity.Id == 0)
                    conn.InsertTracked(activity);

                for (var i = 0; i < others.Count; i++)
                {
                    others[i].SortOrder = i;
                    conn.UpdateTracked(others[i]);
                }
            });

            Helpers.TripPlanChanges.Notify(activity.TripId);
        }

        public async Task<int> DeleteAsync(TripActivity activity)
        {
            var db = await _databaseService.Connection;
            var deleted = await db.DeleteTrackedAsync(activity);
            Helpers.TripPlanChanges.Notify(activity.TripId);
            return deleted;
        }

        public async Task ApplyRealignmentAsync(TripDateRealignment realignment)
        {
            var changes = realignment.GetChanges();
            if (changes.Count == 0)
                return;

            var db = await _databaseService.Connection;
            await db.RunInTransactionAsync(conn =>
            {
                var appended = new HashSet<int>();
                var affectedDays = new HashSet<DateTime>();
                var originalDates = new Dictionary<int, DateTime>();

                foreach (var change in changes)
                {
                    var activity = change.Activity;
                    if (change.NewDate is not DateTime newDate)
                    {
                        conn.DeleteTracked<TripActivity>(activity.Id);
                        continue;
                    }

                    activity.Date = newDate.Date;
                    conn.UpdateTracked(activity);
                    originalDates[activity.Id] = change.OldDate;
                    if (change.AppendToDay)
                    {
                        appended.Add(activity.Id);
                        affectedDays.Add(newDate.Date);
                    }
                }

                // Activities pulled onto the first/last day go after that day's own plan, in their original
                // chronological order; every affected day is renumbered 0..n-1.
                var tripId = realignment.TripId;
                foreach (var day in affectedDays)
                {
                    var ordered = conn.Table<TripActivity>()
                        .Where(a => a.TripId == tripId && a.Date == day)
                        .ToList()
                        .OrderBy(a => appended.Contains(a.Id) ? 1 : 0)
                        .ThenBy(a => originalDates.TryGetValue(a.Id, out var original) ? original : a.Date)
                        .ThenBy(a => a.SortOrder)
                        .ToList();

                    for (var i = 0; i < ordered.Count; i++)
                    {
                        if (ordered[i].SortOrder == i)
                            continue;
                        ordered[i].SortOrder = i;
                        conn.UpdateTracked(ordered[i]);
                    }
                }
            });

            Helpers.TripPlanChanges.Notify(realignment.TripId);
        }

        public async Task ReorderAsync(IReadOnlyList<TripActivity> orderedDay)
        {
            if (orderedDay.Count == 0)
                return;

            var db = await _databaseService.Connection;
            await db.RunInTransactionAsync(conn =>
            {
                for (var i = 0; i < orderedDay.Count; i++)
                {
                    orderedDay[i].SortOrder = i;
                    conn.UpdateTracked(orderedDay[i]);
                }
            });

            Helpers.TripPlanChanges.Notify(orderedDay[0].TripId);
        }
    }

    /// <summary>Pure ordering rules for a day's activities - kept separate so they're easy to unit-test.</summary>
    public static class ItineraryOrdering
    {
        /// <summary>
        /// Where a new activity goes in an already-ordered day: right after the last timed activity that starts
        /// at or before it; if none does, just before the first timed activity. Untimed activities (and days
        /// without any timed activity) go to the end. Manually reordered untimed items are never jumped over
        /// needlessly.
        /// </summary>
        public static int FindInsertIndex(IReadOnlyList<TripActivity> orderedDay, TimeSpan? startTime)
        {
            if (startTime is null)
                return orderedDay.Count;

            var lastEarlier = -1;
            var firstTimed = -1;
            for (var i = 0; i < orderedDay.Count; i++)
            {
                if (orderedDay[i].StartTime is not TimeSpan other)
                    continue;

                if (firstTimed < 0)
                    firstTimed = i;
                if (other <= startTime.Value)
                    lastEarlier = i;
            }

            if (lastEarlier >= 0)
                return lastEarlier + 1;

            return firstTimed >= 0 ? firstTimed : orderedDay.Count;
        }

        /// <summary>Moves <paramref name="item"/> to <paramref name="target"/>'s position. Returns false when nothing changed.</summary>
        public static bool Move<T>(IList<T> list, T item, T target) where T : class
        {
            var from = list.IndexOf(item);
            var to = list.IndexOf(target);
            if (from < 0 || to < 0 || from == to)
                return false;

            list.RemoveAt(from);
            list.Insert(to, item);
            return true;
        }
    }
}
