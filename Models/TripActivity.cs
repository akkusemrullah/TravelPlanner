using SQLite;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace TravelPlanner.Models
{
    /// <summary>
    /// One entry of a trip's day-by-day plan (TP-401): what happens on which day, optionally at what time and
    /// at which saved <see cref="Place"/>. Within a day the user's order (drag &amp; drop) is kept in
    /// <see cref="SortOrder"/>; new activities with a time are slotted in chronologically.
    /// Named TripActivity (table "Activity") so it never clashes with Android.App.Activity.
    /// </summary>
    [Table("Activity")]
    public class TripActivity : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        /// <summary>The day (date part only) this activity belongs to.</summary>
        [Indexed]
        public DateTime Date { get; set; }

        /// <summary>Optional start time of day. Null = "any time that day".</summary>
        public TimeSpan? StartTime { get; set; }

        /// <summary>Optional end time of day (only meaningful together with <see cref="StartTime"/>).</summary>
        public TimeSpan? EndTime { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(150)]
        public string Title { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(500)]
        public string? Note { get; set; }

        /// <summary>Optional link to one of the trip's saved places. Cleared when that place is deleted.</summary>
        public int? PlaceId { get; set; }

        /// <summary>Position within its day (0 = first).</summary>
        public int SortOrder { get; set; }

        // ---- UI helpers (not stored) ----

        /// <summary>Name of the linked place, filled in by the ViewModel that loads the activity.</summary>
        [Ignore]
        public string? PlaceName { get; set; }

        [Ignore]
        public bool HasPlace => !string.IsNullOrWhiteSpace(PlaceName);

        [Ignore]
        public bool HasTime => StartTime is not null;

        [Ignore]
        public bool HasNote => !string.IsNullOrWhiteSpace(Note);

        /// <summary>"09:30" or "09:30 – 11:00"; empty when there is no time.</summary>
        [Ignore]
        public string TimeDisplay
        {
            get
            {
                if (StartTime is null)
                    return string.Empty;

                var start = FormatTime(StartTime.Value);
                return EndTime is null ? start : $"{start} – {FormatTime(EndTime.Value)}";
            }
        }

        /// <summary>TP-103: the start time alone ("09:30"), the plan row's bold top line.</summary>
        [Ignore]
        public string StartTimeDisplay => StartTime is null ? string.Empty : FormatTime(StartTime.Value);

        /// <summary>TP-103: the end time alone, the plan row's small second line (empty when none).</summary>
        [Ignore]
        public string EndTimeDisplay => StartTime is null || EndTime is null ? string.Empty : FormatTime(EndTime.Value);

        [Ignore]
        public bool HasEndTime => StartTime is not null && EndTime is not null;

        private static string FormatTime(TimeSpan time) =>
            DateTime.Today.Add(time).ToString("HH:mm", CultureInfo.InvariantCulture);
    }
}
