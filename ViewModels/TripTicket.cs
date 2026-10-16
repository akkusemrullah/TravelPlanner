using System.Globalization;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Everything the boarding-pass style trip card shows (Dashboard featured trip, Trip detail header), computed
    /// once from a <see cref="Trip"/>. Pure, so it's unit-tested directly; <paramref name="today"/> is a seam.
    /// </summary>
    public sealed class TripTicket
    {
        public TripTicket(Trip trip, DateTime? today = null, CultureInfo? culture = null)
        {
            culture ??= CultureInfo.CurrentCulture;
            var now = (today ?? DateTime.Today).Date;
            var start = trip.StartDate.Date;
            var end = trip.EndDate.Date < start ? start : trip.EndDate.Date;

            Name = trip.Name;
            City = string.IsNullOrWhiteSpace(trip.City) ? trip.Name : trip.City.Trim();
            Country = trip.Country?.Trim() ?? string.Empty;
            HasCountry = Country.Length > 0;
            // The trip's own name is shown under the city only when it adds something ("Roma Tatili" vs "Roma").
            ShowName = !string.IsNullOrWhiteSpace(Name) && !string.Equals(Name.Trim(), City, StringComparison.CurrentCultureIgnoreCase);

            Status = now < start ? TripStatus.Upcoming : now > end ? TripStatus.Completed : TripStatus.Ongoing;
            StatusText = Status.ToDisplayText();

            StartDay = start.ToString("dd", culture);
            StartMonth = start.ToString("MMM yyyy", culture);
            StartWeekday = start.ToString("dddd", culture);
            EndDay = end.ToString("dd", culture);
            EndMonth = end.ToString("MMM yyyy", culture);
            EndWeekday = end.ToString("dddd", culture);

            var days = (end - start).Days + 1;
            DurationText = string.Format(culture, AppResources.Common_DaysFormat, days);

            switch (Status)
            {
                case TripStatus.Upcoming:
                    var until = (start - now).Days;
                    CountdownLabel = AppResources.Dashboard_StartsIn;
                    CountdownValue = until == 1 ? AppResources.Ticket_Tomorrow : string.Format(culture, AppResources.Common_DaysFormat, until);
                    break;
                case TripStatus.Ongoing:
                    var left = (end - now).Days;
                    CountdownLabel = string.Format(culture, AppResources.Ticket_DayOfFormat, (now - start).Days + 1, days);
                    CountdownValue = left == 0 ? AppResources.Ticket_LastDay : string.Format(culture, AppResources.Ticket_DaysLeftFormat, left);
                    break;
                default:
                    CountdownLabel = string.Empty;
                    CountdownValue = AppResources.Ticket_Done;
                    break;
            }

            // How far along the trip is (0 before it starts, 1 once it's over) - the plane on the route line.
            Progress = Status switch
            {
                TripStatus.Upcoming => 0d,
                TripStatus.Completed => 1d,
                _ => days <= 1 ? 0.5d : Math.Clamp((now - start).TotalDays / (days - 1), 0d, 1d)
            };

            BudgetText = trip.PlannedBudget > 0 ? trip.FormattedBudget : AppResources.Ticket_NoBudget;
        }

        public string Name { get; }
        public string City { get; }
        public string Country { get; }
        public bool HasCountry { get; }
        public bool ShowName { get; }
        public TripStatus Status { get; }
        public string StatusText { get; }

        public string StartDay { get; }
        public string StartMonth { get; }
        public string StartWeekday { get; }
        public string EndDay { get; }
        public string EndMonth { get; }
        public string EndWeekday { get; }

        public string DurationText { get; }
        public string CountdownLabel { get; }
        public bool HasCountdownLabel => CountdownLabel.Length > 0;
        public string CountdownValue { get; }

        /// <summary>0..1 position of the plane between departure and return.</summary>
        public double Progress { get; }

        public string BudgetText { get; }
    }
}
