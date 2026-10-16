using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class TripReminderPlannerTests
    {
        private static readonly DateTime Start = new(2026, 8, 20);
        private static readonly DateTime LongBefore = new(2026, 8, 1, 12, 0, 0);

        private static Trip Roma(int days = 3) =>
            new() { Id = 5, Name = "Roma tatili", City = "Roma", StartDate = Start, EndDate = Start.AddDays(days - 1) };

        private static TripActivity Activity(int day, string title, int sort = 0, TimeSpan? at = null) =>
            new() { TripId = 5, Date = Start.AddDays(day), Title = title, SortOrder = sort, StartTime = at };

        [Fact]
        public void NoReminder_TheDayBeforeTheTrip()
        {
            var reminders = TripReminderPlanner.Plan(Roma(), Array.Empty<TripActivity>(), openTodos: 2, reminderHour: 9, LongBefore);

            Assert.DoesNotContain(reminders, r => r.NotifyAt.Date == Start.AddDays(-1));
        }

        [Fact]
        public void Prep_ComesThreeDaysBefore_OnlyWhileTodosAreOpen()
        {
            var withTodos = TripReminderPlanner.Plan(Roma(), Array.Empty<TripActivity>(), openTodos: 4, reminderHour: 9, LongBefore);
            var prep = Assert.Single(withTodos, r => r.Slot == TripReminderPlanner.PrepSlot);
            Assert.Equal(Start.AddDays(-3).AddHours(9), prep.NotifyAt);
            Assert.Contains("4", prep.Body);
            Assert.Contains("Roma", prep.Title);

            var allDone = TripReminderPlanner.Plan(Roma(), Array.Empty<TripActivity>(), openTodos: 0, reminderHour: 9, LongBefore);
            Assert.DoesNotContain(allDone, r => r.Slot == TripReminderPlanner.PrepSlot);
        }

        [Fact]
        public void FirstDay_AlwaysGetsOne_OtherDaysOnlyWithAPlan()
        {
            var activities = new[] { Activity(2, "Vatikan") };

            var reminders = TripReminderPlanner.Plan(Roma(), activities, 0, 9, LongBefore);

            Assert.Contains(reminders, r => r.Slot == TripReminderPlanner.FirstDaySlot && r.NotifyAt == Start.AddHours(9));
            Assert.DoesNotContain(reminders, r => r.Slot == TripReminderPlanner.FirstDaySlot + 1); // day 2: nothing planned
            var day3 = Assert.Single(reminders, r => r.Slot == TripReminderPlanner.FirstDaySlot + 2);
            Assert.Contains("Vatikan", day3.Body);
            Assert.Contains("3. gün", day3.Title);
        }

        [Fact]
        public void DayReminder_NamesTheCountAndTheFirstTimedActivity()
        {
            var activities = new[]
            {
                Activity(0, "Gelato", sort: 0),
                Activity(0, "Kolezyum", sort: 1, at: new TimeSpan(10, 30, 0)),
                Activity(0, "Forum", sort: 2, at: new TimeSpan(14, 0, 0)),
            };

            var first = Assert.Single(TripReminderPlanner.Plan(Roma(), activities, 0, 9, LongBefore), r => r.Slot == TripReminderPlanner.FirstDaySlot);

            Assert.Contains("3 etkinlik", first.Body);
            Assert.Contains("10:30 Kolezyum", first.Body);
        }

        [Fact]
        public void EarlyActivity_MovesTheReminderToAnHourBefore()
        {
            var activities = new[] { Activity(1, "Uçuş", at: new TimeSpan(6, 45, 0)) };

            var day2 = Assert.Single(TripReminderPlanner.Plan(Roma(), activities, 0, 9, LongBefore), r => r.Slot == TripReminderPlanner.FirstDaySlot + 1);

            Assert.Equal(Start.AddDays(1).Add(new TimeSpan(5, 45, 0)), day2.NotifyAt);
        }

        [Fact]
        public void AfterTrip_TheNextMorning()
        {
            var after = Assert.Single(TripReminderPlanner.Plan(Roma(), Array.Empty<TripActivity>(), 0, 9, LongBefore),
                r => r.Slot == TripReminderPlanner.AfterTripSlot);

            Assert.Equal(Start.AddDays(3).AddHours(9), after.NotifyAt);
        }

        [Fact]
        public void PastTimes_AreSkipped_AndDaysBeyondTheHorizonWait()
        {
            var longTrip = Roma(days: 60);
            var activities = Enumerable.Range(0, 60).Select(d => Activity(d, $"Plan {d}")).ToArray();
            var now = Start.AddDays(1).AddHours(12); // mid-trip, after today's reminder

            var reminders = TripReminderPlanner.Plan(longTrip, activities, 3, 9, now);

            Assert.All(reminders, r => Assert.True(r.NotifyAt > now));
            Assert.DoesNotContain(reminders, r => r.Slot == TripReminderPlanner.PrepSlot);
            var lastDay = reminders.Where(r => r.Slot >= TripReminderPlanner.FirstDaySlot).Max(r => r.NotifyAt.Date);
            Assert.Equal(now.Date.AddDays(TripReminderPlanner.HorizonDays), lastDay);
        }

        [Fact]
        public void Slots_FitInTheIdRange()
        {
            Assert.True(TripReminderPlanner.LastDaySlot < 100);
            Assert.DoesNotContain(TripReminderPlanner.BudgetAlertSlot, TripReminderPlanner.ReminderSlots);
        }
    }
}
