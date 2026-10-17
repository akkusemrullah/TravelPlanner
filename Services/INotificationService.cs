using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Schedules and cancels local notifications for upcoming trips via Plugin.LocalNotification.
    /// Implemented by NotificationService.
    /// </summary>
    public interface INotificationService
    {
        /// <summary>Requests local-notification permission (no-ops on platforms that don't need it).</summary>
        Task<bool> RequestPermissionAsync();

        /// <summary>Schedules a trip's reminders: prep (open to-dos), each planned day's programme, and the summary after the trip.</summary>
        Task ScheduleTripNotificationsAsync(Trip trip);

        /// <summary>Cancels any notifications previously scheduled for a trip (call on delete/update).</summary>
        void CancelTripNotifications(int tripId);

        /// <summary>
        /// Shows an immediate "budget at 80% / exceeded" notification (TP-404). Returns false when notifications
        /// aren't available (unsupported platform or permission denied) so the caller can fall back to an in-app message.
        /// </summary>
        Task<bool> ShowBudgetAlertAsync(Trip trip, int thresholdPercent, double usagePercent);

        /// <summary>
        /// TP-4: shows an immediate "someone joined your trip" notification. Returns false when notifications aren't
        /// available, so the caller can show the message in the app instead.
        /// </summary>
        Task<bool> ShowMemberJoinedAsync(string title, string message);
    }
}
