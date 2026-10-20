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
            Budget = trip.FormattedBudget;
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
    }
}
