using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Models
{
    public enum TripStatus
    {
        Upcoming = 0,
        Ongoing = 1,
        Completed = 2
    }

    public static class TripStatusExtensions
    {
        public static string ToDisplayText(this TripStatus status) => status switch
        {
            TripStatus.Ongoing => AppResources.Status_Ongoing,
            TripStatus.Upcoming => AppResources.Status_Upcoming,
            _ => AppResources.Status_Completed,
        };
    }
}
