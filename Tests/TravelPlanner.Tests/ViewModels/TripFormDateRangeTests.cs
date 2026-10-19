using System.Globalization;
using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class TripFormDateRangeTests
    {
        private readonly Mock<IDateRangePicker> _picker = new();

        public TripFormDateRangeTests() => _picker.SetupGet(p => p.IsSupported).Returns(true);

        private TripFormViewModel Create(IDateRangePicker? picker) =>
            new(new Mock<ITripService>().Object, new Mock<IGeocodingService>().Object, new Mock<IExpenseService>().Object,
                new Mock<IExchangeRateService>().Object, new Mock<IBudgetAlertService>().Object,
                new SettingsService(new InMemoryPreferences()), Fakes.Connectivity(), Fakes.Dialogs().Object,
                dateRangePicker: picker);

        private void PickReturns(DateTime start, DateTime end) =>
            _picker.Setup(p => p.PickAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>()))
                   .ReturnsAsync((start, end));

        [Fact]
        public void WithoutRangePicker_KeepsTwoDatePickers()
        {
            var vm = Create(null);

            Assert.False(vm.UsesRangePicker);
            Assert.True(vm.UsesTwoDatePickers);
        }

        [Fact]
        public async Task PickingARange_SetsBothDates()
        {
            var vm = Create(_picker.Object);
            PickReturns(new DateTime(2026, 11, 20), new DateTime(2026, 11, 27));

            await vm.PickDatesCommand.ExecuteAsync(null);

            Assert.True(vm.UsesRangePicker);
            Assert.Equal(new DateTime(2026, 11, 20), vm.StartDate);
            Assert.Equal(new DateTime(2026, 11, 27), vm.EndDate);
            Assert.False(vm.HasValidationError);
        }

        [Fact]
        public async Task PickingARange_OpensOnTheCurrentDates()
        {
            var vm = Create(_picker.Object);
            vm.StartDate = new DateTime(2026, 12, 1);
            vm.EndDate = new DateTime(2026, 12, 5);
            PickReturns(new DateTime(2026, 12, 1), new DateTime(2026, 12, 5));

            await vm.PickDatesCommand.ExecuteAsync(null);

            _picker.Verify(p => p.PickAsync(new DateTime(2026, 12, 1), new DateTime(2026, 12, 5), It.IsAny<string>()));
        }

        [Fact]
        public async Task CancellingThePicker_KeepsTheDates()
        {
            var vm = Create(_picker.Object);
            var start = vm.StartDate;
            var end = vm.EndDate;
            _picker.Setup(p => p.PickAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<string>()))
                   .ReturnsAsync(((DateTime, DateTime)?)null);

            await vm.PickDatesCommand.ExecuteAsync(null);

            Assert.Equal(start, vm.StartDate);
            Assert.Equal(end, vm.EndDate);
        }

        [Fact]
        public async Task PickingATooLongRange_WarnsRightAway()
        {
            var vm = Create(_picker.Object);
            var start = new DateTime(2026, 1, 1);
            PickReturns(start, start.AddDays(AppConstants.MaxTripDays));

            await vm.PickDatesCommand.ExecuteAsync(null);

            Assert.Equal(start.AddDays(AppConstants.MaxTripDays), vm.EndDate);
            Assert.True(vm.HasValidationError);
        }

        [Fact]
        public void DateRangeText_ShowsTheYearOnce_OrBothWhenItChanges()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                var vm = Create(_picker.Object);
                vm.StartDate = new DateTime(2026, 8, 20);
                vm.EndDate = new DateTime(2026, 8, 27);
                Assert.Equal("20 Aug – 27 Aug 2026", vm.DateRangeText);
                Assert.Contains("8", vm.DayCountText);

                vm.EndDate = new DateTime(2027, 1, 3);
                vm.StartDate = new DateTime(2026, 12, 28);
                Assert.Equal("28 Dec 2026 – 3 Jan 2027", vm.DateRangeText);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }
    }
}
