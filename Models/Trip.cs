using SQLite;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using TravelPlanner.Helpers;

namespace TravelPlanner.Models
{
    public class Trip : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required, System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string Country { get; set; } = string.Empty;

        [Required, System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string City { get; set; } = string.Empty;

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        /// <summary>Stored value: planned budget in minor units (kuruş/cent) - see Expense.AmountMinor (TP-109).</summary>
        public long PlannedBudgetMinor { get; set; }

        [Ignore, Range(0, double.MaxValue)]
        public decimal PlannedBudget
        {
            get => MoneyConverter.FromMinor(PlannedBudgetMinor);
            set => PlannedBudgetMinor = MoneyConverter.ToMinor(value);
        }

        [System.ComponentModel.DataAnnotations.MaxLength(500)]
        public string? Description { get; set; }

        /// <summary>
        /// ISO code of the trip's budget currency (TP-403). The planned budget and every expense's
        /// <see cref="Expense.AmountMinor"/> are in this currency. Null for trips created before multi-currency
        /// support - those keep following the user's default currency (see <see cref="EffectiveCurrencyCode"/>).
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(3)]
        public string? CurrencyCode { get; set; }

        /// <summary>
        /// Cloud path of the cover photo, "trips/&lt;trip SyncId&gt;/cover-&lt;id&gt;.jpg" (its thumbnail: the same name +
        /// "-thumb"); null = no cover. Set only after both files are uploaded (TripCoverService), so every device that
        /// sees it can download them. Server rule: supabase/migrations/…32_trip_cover.sql.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(200)]
        public string? CoverPath { get; set; }

        /// <summary>
        /// Device only (not synced): the id of a cover picked on this device whose files wait for upload; it is shown
        /// instead of <see cref="CoverPath"/> until then. Null = nothing waiting.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(32)]
        public string? PendingCoverId { get; set; }

        // ---- Computed properties for UI binding (not stored in DB) ----

        private string? _coverThumbFile;

        /// <summary>The cover's thumbnail on this device (set by the list once it's there), or null: the list shows an icon.</summary>
        [Ignore, System.Text.Json.Serialization.JsonIgnore]
        public string? CoverThumbFile
        {
            get => _coverThumbFile;
            set
            {
                if (_coverThumbFile == value)
                    return;
                _coverThumbFile = value;
                OnPropertyChanged(nameof(CoverThumbFile));
                OnPropertyChanged(nameof(HasCoverThumb));
            }
        }

        [Ignore, System.Text.Json.Serialization.JsonIgnore]
        public bool HasCoverThumb => _coverThumbFile is not null;

        [Ignore]
        public int DurationInDays => (EndDate.Date - StartDate.Date).Days + 1;

        /// <summary>The currency the budget and expenses are actually in: <see cref="CurrencyCode"/>, or the default.</summary>
        [Ignore]
        public string EffectiveCurrencyCode => MoneyFormatter.Normalize(CurrencyCode);

        [Ignore]
        public string FormattedBudget => MoneyFormatter.FormatIn(PlannedBudget, EffectiveCurrencyCode);

        /// <summary>TP-98: the list row's budget - "—" when none was set (instead of "0 ₺").</summary>
        [Ignore]
        public string BudgetOrDash => PlannedBudget > 0 ? FormattedBudget : "—";

        /// <summary>TP-98: "City, Country" for the list row (just the city when there is no country).</summary>
        [Ignore]
        public string PlaceDisplay => string.IsNullOrWhiteSpace(Country) ? City.Trim() : $"{City.Trim()}, {Country.Trim()}";

        [Ignore]
        public bool IsUpcoming => StartDate.Date > DateTime.Today;

        [Ignore]
        public bool IsOngoing => DateTime.Today >= StartDate.Date && DateTime.Today <= EndDate.Date;

        /// <summary>Where the trip is relative to today - drives the status pill colour (TripTagColorConverter).</summary>
        [Ignore]
        public TripStatus Status => IsOngoing ? TripStatus.Ongoing : IsUpcoming ? TripStatus.Upcoming : TripStatus.Completed;

        /// <summary>Localized status pill text ("Devam Ediyor" / "Yaklaşan" / "Tamamlandı" in Turkish).</summary>
        [Ignore]
        public string StatusTag => Status.ToDisplayText();

        /// <summary>
        /// Compact date range for the Dashboard's featured-trip card and trip lists, in the current culture:
        /// "20 – 27 Ağustos" (same month), "28 Ağustos – 3 Eylül" (spanning months), or - when the trip
        /// crosses into another year - "28 Aralık 2026 – 3 Ocak 2027" so the year isn't lost (TP-307).
        /// </summary>
        [Ignore]
        public string CompactDateRangeDisplay
        {
            get
            {
                var culture = CultureInfo.CurrentCulture;

                if (StartDate.Year != EndDate.Year)
                    return $"{StartDate.ToString("d MMMM yyyy", culture)} – {EndDate.ToString("d MMMM yyyy", culture)}";

                var endText = EndDate.ToString("d MMMM", culture);
                return StartDate.Month == EndDate.Month
                    ? $"{StartDate.Day} – {endText}"
                    : $"{StartDate.ToString("d MMMM", culture)} – {endText}";
            }
        }
    }
}