using System.Globalization;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class PersonNameTests
    {
        private static readonly CultureInfo Tr = new("tr-TR");

        [Theory]
        [InlineData("Emrullah Akkuş", "EA")]
        [InlineData("  emrullah   can  akkuş ", "EA")]   // first + last name, extra spaces ignored
        [InlineData("ismail", "İ")]                       // Turkish upper-casing
        [InlineData("", "")]
        [InlineData(null, "")]
        public void Initials_UseFirstAndLastName(string? name, string expected) =>
            Assert.Equal(expected, PersonName.Initials(name, Tr));

        [Fact]
        public void FirstName_IsTheFirstWord()
        {
            Assert.Equal("Emrullah", PersonName.FirstName("Emrullah Akkuş"));
            Assert.Equal(string.Empty, PersonName.FirstName("   "));
        }
    }

    public class TripTicketTests
    {
        private static readonly DateTime Start = new(2026, 8, 20);

        private static Trip Roma(decimal budget = 0m) => new()
        {
            Name = "Roma Tatili", City = "Roma", Country = "İtalya",
            StartDate = Start, EndDate = Start.AddDays(7), PlannedBudget = budget, CurrencyCode = "TRY"
        };

        [Fact]
        public void Upcoming_CountsDownToDeparture()
        {
            var ticket = new TripTicket(Roma(), today: Start.AddDays(-12));

            Assert.Equal(TripStatus.Upcoming, ticket.Status);
            Assert.Equal(AppResources.Dashboard_StartsIn, ticket.CountdownLabel);
            Assert.Equal(string.Format(AppResources.Common_DaysFormat, 12), ticket.CountdownValue);
            Assert.Equal(0d, ticket.Progress);
            Assert.Equal(string.Format(AppResources.Common_DaysFormat, 8), ticket.DurationText);
            Assert.True(ticket.ShowName);
            Assert.Equal("20", ticket.StartDay);
            Assert.Equal("27", ticket.EndDay);
        }

        [Fact]
        public void DayBefore_SaysTomorrow()
        {
            Assert.Equal(AppResources.Ticket_Tomorrow, new TripTicket(Roma(), today: Start.AddDays(-1)).CountdownValue);
        }

        [Fact]
        public void Ongoing_ShowsDayNumberAndDaysLeft()
        {
            var ticket = new TripTicket(Roma(), today: Start.AddDays(3));

            Assert.Equal(TripStatus.Ongoing, ticket.Status);
            Assert.Equal(string.Format(AppResources.Ticket_DayOfFormat, 4, 8), ticket.CountdownLabel);
            Assert.Equal(string.Format(AppResources.Ticket_DaysLeftFormat, 4), ticket.CountdownValue);
            Assert.InRange(ticket.Progress, 0.42, 0.44); // 3 of 7 days
            Assert.Equal(AppResources.Ticket_LastDay, new TripTicket(Roma(), today: Start.AddDays(7)).CountdownValue);
        }

        [Fact]
        public void Completed_HasNoCountdownLabel()
        {
            var ticket = new TripTicket(Roma(), today: Start.AddDays(30));

            Assert.Equal(TripStatus.Completed, ticket.Status);
            Assert.False(ticket.HasCountdownLabel);
            Assert.Equal(AppResources.Ticket_Done, ticket.CountdownValue);
            Assert.Equal(1d, ticket.Progress);
        }

        [Fact]
        public void Budget_FallsBackWhenNotSet_AndNameHiddenWhenSameAsCity()
        {
            Assert.Equal(AppResources.Ticket_NoBudget, new TripTicket(Roma(), today: Start).BudgetText);
            Assert.NotEqual(AppResources.Ticket_NoBudget, new TripTicket(Roma(1500m), today: Start).BudgetText);

            var sameName = Roma();
            sameName.Name = "roma";
            Assert.False(new TripTicket(sameName, today: Start).ShowName);
        }
    }
}
