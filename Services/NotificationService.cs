using Plugin.LocalNotification;
using Plugin.LocalNotification.Core.Models;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    public class NotificationService : INotificationService
    {
        private readonly ISettingsService _settings;
        private readonly IActivityService _activities;
        private readonly ITodoService _todos;

        public NotificationService(ISettingsService settings, IActivityService activities, ITodoService todos)
        {
            _settings = settings;
            _activities = activities;
            _todos = todos;
        }

        /// <summary>
        /// Plugin.LocalNotification only works on Android, iOS and Mac Catalyst. On Windows (and anything
        /// else) every call is a silent no-op, so the app still runs there - just without reminders (TP-209).
        /// </summary>
        private static bool IsSupported =>
            DeviceInfo.Current.Platform == DevicePlatform.Android ||
            DeviceInfo.Current.Platform == DevicePlatform.iOS ||
            DeviceInfo.Current.Platform == DevicePlatform.MacCatalyst;

        public Task<bool> RequestPermissionAsync()
        {
            if (!IsSupported)
                return Task.FromResult(false);

            return LocalNotificationCenter.Current.RequestNotificationPermission();
        }

        /// <summary>
        /// Schedules the trip's reminders (see <see cref="TripReminderPlanner"/>): a prep reminder while to-dos are open,
        /// each planned day's programme, and the expense summary after the trip - all at the hour chosen in Settings.
        /// </summary>
        public async Task ScheduleTripNotificationsAsync(Trip trip)
        {
            if (!IsSupported)
                return;

            IReadOnlyCollection<TripActivity> activities = Array.Empty<TripActivity>();
            var openTodos = 0;
            try
            {
                activities = await _activities.GetByTripIdAsync(trip.Id);
                openTodos = (await _todos.GetByTripIdAsync(trip.Id)).Count(t => !t.IsCompleted);
            }
            catch (Exception ex)
            {
                // Still schedule what we can (travel day, after the trip) without the plan.
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Reading the plan failed: {ex.Message}");
            }

            foreach (var reminder in TripReminderPlanner.Plan(trip, activities, openTodos, _settings.ReminderHour, DateTime.Now))
            {
                await ShowNotificationAsync(
                    id: GetNotificationId(trip.Id, reminder.Slot),
                    title: reminder.Title,
                    message: reminder.Body,
                    notifyTime: reminder.NotifyAt);
            }
        }

        public void CancelTripNotifications(int tripId)
        {
            if (!IsSupported)
                return;

            var ids = TripReminderPlanner.ReminderSlots.Select(slot => GetNotificationId(tripId, slot))
                .Concat(GetLegacyNotificationIds(tripId))
                .ToArray();
            LocalNotificationCenter.Current.Cancel(ids);
            // The budget alert (TP-404) is deliberately left alone: this runs on every trip save, and a delivered
            // 80% / 100% notification must not vanish from the tray because the trip was edited.
        }

        public async Task<bool> ShowBudgetAlertAsync(Trip trip, int thresholdPercent, double usagePercent)
        {
            if (!IsSupported)
                return false;

            try
            {
                if (!await LocalNotificationCenter.Current.AreNotificationsEnabled())
                    return false;

                var (title, message) = BudgetAlertService.BuildMessage(trip.Name, thresholdPercent, usagePercent);

                // No Schedule = shown immediately. Same id per trip, so a 100% alert replaces the 80% one.
                await LocalNotificationCenter.Current.Show(new NotificationRequest
                {
                    NotificationId = GetNotificationId(trip.Id, TripReminderPlanner.BudgetAlertSlot),
                    Title = title,
                    Description = message
                });
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NotificationService] Budget alert failed: {ex}");
                return false;
            }
        }

        private static Task ShowNotificationAsync(int id, string title, string message, DateTime notifyTime)
        {
            var request = new NotificationRequest
            {
                NotificationId = id,
                Title = title,
                Description = message,
                // Plugin.LocalNotification 14: NotifyTime is a DateTimeOffset (local wall-clock time, as before).
                Schedule = new NotificationRequestSchedule { NotifyTime = new DateTimeOffset(notifyTime) }
            };

            return LocalNotificationCenter.Current.Show(request);
        }

        /// <summary>
        /// Deterministic id per trip + slot (<see cref="TripReminderPlanner"/>), so one trip's reminders can be cancelled
        /// or replaced together: 1 000 000 000 + trip × 100 + slot. Stays below int.MaxValue for any trip id, and far above
        /// the ids the old scheme (trip × 10 + n) gave ordinary trip ids, so cancelling those never hits a new reminder.
        /// </summary>
        private static int GetNotificationId(int tripId, int slot) =>
            (int)(1_000_000_000L + Math.Abs((long)tripId) % 10_000_000L * 100 + slot);

        /// <summary>The "1 day before" / "travel day" ids of earlier versions - cancelled so those reminders don't fire after an update.</summary>
        private static IEnumerable<int> GetLegacyNotificationIds(int tripId)
        {
            var legacyBase = Math.Abs((long)tripId) % 200_000_000L * 10;
            yield return (int)(legacyBase + 1);
            yield return (int)(legacyBase + 2);
        }
    }
}
