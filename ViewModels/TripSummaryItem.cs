using TravelPlanner.Models;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Read-only display wrapper around a Trip for the Dashboard's "Seyahatlerim" preview list.
    /// Exists mainly to compute the status pill text ("Yaklaşan" / "Devam Ediyor" / "Tamamlandı") -
    /// Trip itself has no status field, just dates, so this derives one purely from IsOngoing/IsUpcoming
    /// rather than adding a display-only concept to the persisted model.
    /// </summary>
    public class TripSummaryItem
    {
        public TripSummaryItem(Trip trip)
        {
            Trip = trip;
            Id = trip.Id;
            Name = trip.Name;
            Place = string.IsNullOrWhiteSpace(trip.Country) ? trip.City : $"{trip.City}, {trip.Country}";
            Dates = trip.CompactDateRangeDisplay;
            // TP-97: a trip without a budget shows a dash, as in the prototype (not "₺0")
            Budget = trip.PlannedBudget > 0 ? trip.FormattedBudget : "—";
            Status = trip.Status;
            Tag = trip.StatusTag; // single source of truth: Trip.Status / Trip.StatusTag
        }

        /// <summary>The trip itself: its cover thumbnail (<see cref="Trip.CoverThumbFile"/>) arrives after the row is shown.</summary>
        public Trip Trip { get; }
        public int Id { get; }
        public string Name { get; }
        public string Place { get; }
        public string Dates { get; }
        public string Budget { get; }
        public string Tag { get; }
        public TripStatus Status { get; }

        /// <summary>TP-97: the first row of a grouped list draws no divider above it.</summary>
        public bool IsFirst { get; init; }

        public bool HasDividerAbove => !IsFirst;
    }
}
