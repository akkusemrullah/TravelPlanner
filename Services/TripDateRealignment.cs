using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// What happens to a trip's day-plan activities when the trip's dates are edited. Pure and deterministic
    /// (no database access), so the rules are unit-tested directly; <see cref="IActivityService.ApplyRealignmentAsync"/>
    /// writes the result.
    ///
    /// Rules:
    /// - The trip was MOVED (the start changed and either the length stayed the same, or the old and new date
    ///   ranges don't overlap at all): every activity shifts by the same number of days, so "Day 3" stays day 3.
    /// - Otherwise (the start and/or end was extended or trimmed): activities keep their calendar dates.
    /// - Whatever still falls outside the new range afterwards is "outside": the caller asks the user, then each
    ///   one is either moved to the nearest trip day (appended after that day's own activities) or deleted.
    /// Activities that were already outside the old range (stranded by earlier versions of the app, which let
    /// the dates change freely) are picked up by the same rules, so editing and saving the trip repairs them.
    /// </summary>
    public sealed class TripDateRealignment
    {
        private readonly List<(TripActivity Activity, DateTime Target)> _shifted;
        private readonly List<(TripActivity Activity, DateTime Target)> _outside;

        private TripDateRealignment(int tripId, DateTime newStart, DateTime newEnd, int shiftDays,
            List<(TripActivity, DateTime)> shifted, List<(TripActivity, DateTime)> outside)
        {
            TripId = tripId;
            NewStart = newStart;
            NewEnd = newEnd;
            ShiftDays = shiftDays;
            _shifted = shifted;
            _outside = outside;
        }

        public int TripId { get; }
        public DateTime NewStart { get; }
        public DateTime NewEnd { get; }

        /// <summary>Days every activity moves by (0 = activities keep their calendar dates).</summary>
        public int ShiftDays { get; }

        /// <summary>Activities that end up outside the new trip range (after any shift).</summary>
        public int OutsideCount => _outside.Count;

        /// <summary>The user's answer for the outside activities: true = delete them, false = move to the nearest day.</summary>
        public bool DeleteOutside { get; set; }

        /// <summary>Set when the user dismissed the question - the save is abandoned.</summary>
        public bool Cancelled { get; set; }

        public bool HasChanges => _shifted.Count > 0 || _outside.Count > 0;

        /// <summary>
        /// The trip moved as a whole when the start changed and either the length is unchanged or the new range
        /// doesn't overlap the old one; only then do activities shift with it.
        /// </summary>
        public static int ComputeShiftDays(DateTime oldStart, DateTime oldEnd, DateTime newStart, DateTime newEnd)
        {
            (oldStart, oldEnd) = Normalize(oldStart, oldEnd);
            (newStart, newEnd) = Normalize(newStart, newEnd);

            var delta = (newStart - oldStart).Days;
            if (delta == 0)
                return 0;

            var sameLength = (oldEnd - oldStart).Days == (newEnd - newStart).Days;
            var overlaps = newStart <= oldEnd && newEnd >= oldStart;
            return sameLength || !overlaps ? delta : 0;
        }

        public static TripDateRealignment Create(int tripId, DateTime oldStart, DateTime oldEnd, DateTime newStart, DateTime newEnd,
            IEnumerable<TripActivity> activities)
        {
            (newStart, newEnd) = Normalize(newStart, newEnd);
            var shift = ComputeShiftDays(oldStart, oldEnd, newStart, newEnd);

            var shifted = new List<(TripActivity, DateTime)>();
            var outside = new List<(TripActivity, DateTime)>();

            foreach (var activity in activities)
            {
                var target = SafeAddDays(activity.Date.Date, shift);
                if (target < newStart || target > newEnd)
                    outside.Add((activity, target));
                else if (target != activity.Date.Date)
                    shifted.Add((activity, target));
            }

            return new TripDateRealignment(tripId, newStart, newEnd, shift, shifted, outside);
        }

        /// <summary>
        /// Every activity whose row changes: its new date, or null when it is deleted. <c>AppendToDay</c> marks
        /// outside activities pulled onto the first/last trip day - they go after that day's own activities.
        /// </summary>
        public IReadOnlyList<ActivityDateChange> GetChanges()
        {
            var changes = _shifted
                .Select(s => new ActivityDateChange(s.Activity, s.Activity.Date.Date, s.Target, AppendToDay: false))
                .ToList();

            foreach (var (activity, target) in _outside)
            {
                var newDate = DeleteOutside ? (DateTime?)null : (target < NewStart ? NewStart : NewEnd);
                changes.Add(new ActivityDateChange(activity, activity.Date.Date, newDate, AppendToDay: !DeleteOutside));
            }

            return changes;
        }

        private static (DateTime Start, DateTime End) Normalize(DateTime start, DateTime end)
        {
            start = start.Date;
            end = end.Date < start ? start : end.Date;
            return (start, end);
        }

        private static DateTime SafeAddDays(DateTime date, int days)
        {
            if (days == 0)
                return date;

            // Dates come from DatePickers (1900-2100), but a corrupt/restored row must not crash the save.
            try
            {
                return date.AddDays(days);
            }
            catch (ArgumentOutOfRangeException)
            {
                return days < 0 ? DateTime.MinValue.Date : DateTime.MaxValue.Date;
            }
        }
    }

    /// <summary>One activity's date change from <see cref="TripDateRealignment.GetChanges"/>. NewDate null = delete.</summary>
    public sealed record ActivityDateChange(TripActivity Activity, DateTime OldDate, DateTime? NewDate, bool AppendToDay);
}
