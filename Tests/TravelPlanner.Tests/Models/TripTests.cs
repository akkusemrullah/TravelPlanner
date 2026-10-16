using Moq;
using Xunit;
using TravelPlanner.Models;

namespace TravelPlanner.Tests
{
    public class TripTests
    {
        private static Trip TripStarting(int daysFromToday, int lengthInDays) => new()
        {
            Name = "Test",
            City = "Roma",
            StartDate = DateTime.Today.AddDays(daysFromToday),
            EndDate = DateTime.Today.AddDays(daysFromToday + lengthInDays - 1)
        };

        [Fact]
        public void DurationInDays_CountsBothEnds()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27) };
            Assert.Equal(8, trip.DurationInDays);
        }

        [Fact]
        public void StatusTag_Upcoming()
        {
            var trip = TripStarting(daysFromToday: 5, lengthInDays: 3);
            Assert.True(trip.IsUpcoming);
            Assert.False(trip.IsOngoing);
            Assert.Equal("Yaklaşan", trip.StatusTag);
        }

        [Fact]
        public void StatusTag_Ongoing()
        {
            var trip = TripStarting(daysFromToday: -1, lengthInDays: 3);
            Assert.True(trip.IsOngoing);
            Assert.Equal("Devam Ediyor", trip.StatusTag);
        }

        [Fact]
        public void StatusTag_Completed()
        {
            var trip = TripStarting(daysFromToday: -10, lengthInDays: 3);
            Assert.False(trip.IsOngoing);
            Assert.False(trip.IsUpcoming);
            Assert.Equal("Tamamlandı", trip.StatusTag);
        }

        [Fact]
        public void PlannedBudget_IsStoredInMinorUnits()
        {
            var trip = new Trip { PlannedBudget = 1234.56m };

            Assert.Equal(123456L, trip.PlannedBudgetMinor);
            Assert.Equal(1234.56m, trip.PlannedBudget);
        }

        [Fact]
        public void CompactDateRange_SameMonth_ShowsMonthOnce()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27) };
            Assert.Equal("20 – 27 Ağustos", trip.CompactDateRangeDisplay);
        }

        [Fact]
        public void CompactDateRange_AcrossMonths_ShowsBothMonths()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 8, 28), EndDate = new DateTime(2026, 9, 3) };
            Assert.Equal("28 Ağustos – 3 Eylül", trip.CompactDateRangeDisplay);
        }

        [Fact]
        public void CompactDateRange_AcrossYears_KeepsTheYear()
        {
            // TP-307: "28 Aralık – 3 Ocak" lost the year; both ends now carry it.
            var trip = new Trip { StartDate = new DateTime(2026, 12, 28), EndDate = new DateTime(2027, 1, 3) };
            Assert.Equal("28 Aralık 2026 – 3 Ocak 2027", trip.CompactDateRangeDisplay);
        }

        [Fact]
        public void Status_MatchesTheDates()
        {
            Assert.Equal(TripStatus.Upcoming, TripStarting(daysFromToday: 5, lengthInDays: 2).Status);
            Assert.Equal(TripStatus.Ongoing, TripStarting(daysFromToday: 0, lengthInDays: 2).Status);
            Assert.Equal(TripStatus.Completed, TripStarting(daysFromToday: -5, lengthInDays: 2).Status);
        }

        [Fact]
        public void ExpenseAmount_IsStoredInMinorUnits()
        {
            var expense = new Expense { Amount = 49.90m };

            Assert.Equal(4990L, expense.AmountMinor);
            Assert.Equal(49.90m, expense.Amount);
        }
    }
}
