using System.Globalization;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    /// <summary>One local notification to schedule for a trip. <see cref="Slot"/> keeps its id stable per trip.</summary>
    public sealed record TripReminder(int Slot, DateTime NotifyAt, string Title, string Body);

    /// <summary>
    /// Decides which reminders a trip gets - pure, so the rules are unit-tested; NotificationService only schedules
    /// the result. Replaces the old generic "trip starts tomorrow" reminder with ones that say something useful:
    /// <list type="bullet">
    /// <item><b>Hazırlık</b> - <see cref="PrepDaysBeforeTrip"/> days before, only while to-dos are still open
    ///   ("4 iş hâlâ bekliyor"), early enough to still buy/pack things.</item>
    /// <item><b>Günün planı</b> - every trip day that has activities: how many and which comes first. The first day
    ///   always gets one ("Bugün yolculuk günü!"). Sent at the reminder hour, or an hour before the first timed
    ///   activity if that is earlier (an early flight).</item>
    /// <item><b>Dönüş</b> - the morning after the trip: the expense summary and who owes whom.</item>
    /// </list>
    /// </summary>
    public static class TripReminderPlanner
    {
        /// <summary>The prep reminder comes this many days before the first day.</summary>
        public const int PrepDaysBeforeTrip = 3;

        /// <summary>
        /// Day reminders are only scheduled this far ahead (phones cap how many alarms an app may hold). The plan is
        /// refreshed on every app start/resume, so later days are added as they come closer.
        /// </summary>
        public const int HorizonDays = 30;

        /// <summary>A day reminder comes at most this long before the day's first timed activity.</summary>
        public static readonly TimeSpan LeadBeforeFirstActivity = TimeSpan.FromHours(1);

        public const int PrepSlot = 1;
        public const int AfterTripSlot = 2;

        /// <summary>Slot 3 is the budget alert (TP-404) - never cancelled together with the reminders.</summary>
        public const int BudgetAlertSlot = 3;

        /// <summary>Day <c>n</c> of the trip (0-based) uses slot <c>FirstDaySlot + n</c>.</summary>
        public const int FirstDaySlot = 10;

        /// <summary>Highest slot in use: one per possible trip day.</summary>
        public const int LastDaySlot = FirstDaySlot + AppConstants.MaxTripDays - 1;

        /// <summary>Every slot that belongs to the reminders (cancel these to clear a trip's schedule).</summary>
        public static IEnumerable<int> ReminderSlots =>
            new[] { PrepSlot, AfterTripSlot }.Concat(Enumerable.Range(FirstDaySlot, LastDaySlot - FirstDaySlot + 1));

        public static IReadOnlyList<TripReminder> Plan(
            Trip trip, IReadOnlyCollection<TripActivity> activities, int openTodos, int reminderHour, DateTime now)
        {
            var reminders = new List<TripReminder>();
            var start = trip.StartDate.Date;
            var end = trip.EndDate.Date < start ? start : trip.EndDate.Date;
            var place = string.IsNullOrWhiteSpace(trip.City) ? trip.Name : trip.City;
            var hour = TimeSpan.FromHours(Math.Clamp(reminderHour, 0, 23));

            // 1) Hazırlık: only worth a notification while something is still to be done.
            if (openTodos > 0)
            {
                var prepAt = start.AddDays(-PrepDaysBeforeTrip).Add(hour);
                if (prepAt > now)
                {
                    reminders.Add(new TripReminder(PrepSlot, prepAt,
                        string.Format(AppResources.Notification_PrepTitleFormat, place, PrepDaysBeforeTrip),
                        string.Format(AppResources.Notification_PrepBodyFormat, openTodos)));
                }
            }

            // 2) Günün planı: one per day that has something planned (the first day always), within the horizon.
            var days = Math.Min((end - start).Days + 1, AppConstants.MaxTripDays);
            var horizon = now.Date.AddDays(HorizonDays);
            var byDay = activities
                .GroupBy(a => a.Date.Date)
                .ToDictionary(g => g.Key, g => g.OrderBy(a => a.SortOrder).ToList());

            for (var index = 0; index < days; index++)
            {
                var date = start.AddDays(index);
                if (date > horizon)
                    break;
                if (date < now.Date)
                    continue;

                byDay.TryGetValue(date, out var plan);
                plan ??= new List<TripActivity>();
                if (index > 0 && plan.Count == 0)
                    continue;

                var notifyAt = date.Add(hour);
                var firstTimed = plan.Where(a => a.StartTime is not null).Min(a => a.StartTime);
                if (firstTimed is { } first && first - LeadBeforeFirstActivity < hour)
                    notifyAt = date.Add(first > LeadBeforeFirstActivity ? first - LeadBeforeFirstActivity : TimeSpan.Zero);

                if (notifyAt <= now)
                    continue;

                var title = index == 0
                    ? AppResources.Notification_TravelDayTitle
                    : string.Format(AppResources.Notification_DayTitleFormat, index + 1, place);
                var body = plan.Count == 0
                    ? string.Format(AppResources.Notification_TravelDayBody, place)
                    : DayBody(plan);

                reminders.Add(new TripReminder(FirstDaySlot + index, notifyAt, title, body));
            }

            // 3) Dönüş: the morning after - totals and settling up.
            var afterAt = end.AddDays(1).Add(hour);
            if (afterAt > now)
            {
                reminders.Add(new TripReminder(AfterTripSlot, afterAt,
                    string.Format(AppResources.Notification_AfterTripTitleFormat, place),
                    AppResources.Notification_AfterTripBody));
            }

            return reminders.OrderBy(r => r.NotifyAt).ToList();
        }

        /// <summary>"3 etkinlik · ilk: 09:30 Ayasofya" / "Bugün: Ayasofya".</summary>
        internal static string DayBody(IReadOnlyList<TripActivity> plan)
        {
            // The earliest timed activity is "first" even if an untimed one was dragged above it.
            var first = plan.Where(a => a.StartTime is not null).OrderBy(a => a.StartTime).FirstOrDefault() ?? plan[0];
            var label = first.StartTime is { } time
                ? $"{DateTime.Today.Add(time).ToString("HH:mm", CultureInfo.InvariantCulture)} {first.Title}"
                : first.Title;

            return plan.Count == 1
                ? string.Format(AppResources.Notification_DayBodySingleFormat, label)
                : string.Format(AppResources.Notification_DayBodyFormat, plan.Count, label);
        }
    }
}
