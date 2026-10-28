using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>Trip-date changes: which activities shift, which fall outside, and how they're written back.</summary>
    public class TripDateRealignmentTests : IAsyncLifetime
    {
        private static readonly DateTime Start = new(2026, 8, 20);
        private readonly TestDatabase _db = new();
        private ActivityService _service = null!;

        public Task InitializeAsync()
        {
            _service = new ActivityService(_db.Service);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static TripActivity At(int id, int day, int order = 0) =>
            new() { Id = id, TripId = 1, Date = Start.AddDays(day), Title = $"A{id}", SortOrder = order };

        [Theory]
        [InlineData(0, 9, 30, 39, 30)]   // same length, moved a month later -> shift
        [InlineData(0, 9, -5, 4, -5)]    // same length, moved earlier (overlapping) -> shift
        [InlineData(0, 9, 100, 104, 100)] // shorter but no overlap -> moved trip, shift
        [InlineData(0, 9, -1, 9, 0)]     // one day added before -> keep dates
        [InlineData(0, 9, 2, 9, 0)]      // first two days trimmed -> keep dates
        [InlineData(0, 9, 0, 4, 0)]      // only the end moved -> keep dates
        public void ComputeShiftDays_FollowsTheMovedTripRule(int oldStart, int oldEnd, int newStart, int newEnd, int expected)
        {
            Assert.Equal(expected, TripDateRealignment.ComputeShiftDays(
                Start.AddDays(oldStart), Start.AddDays(oldEnd), Start.AddDays(newStart), Start.AddDays(newEnd)));
        }

        [Fact]
        public void MovedTrip_ShiftsEveryActivity_NothingOutside()
        {
            var r = TripDateRealignment.Create(1, Start, Start.AddDays(4), Start.AddDays(30), Start.AddDays(34),
                new[] { At(1, 0), At(2, 4) });

            Assert.Equal(0, r.OutsideCount);
            Assert.Equal(new[] { Start.AddDays(30), Start.AddDays(34) }, r.GetChanges().Select(c => c.NewDate!.Value));
        }

        [Fact]
        public void ShortenedTrip_ReportsOutside_AndMovesOrDeletesThem()
        {
            var r = TripDateRealignment.Create(1, Start, Start.AddDays(9), Start, Start.AddDays(4),
                new[] { At(1, 1), At(2, 6), At(3, 9) });

            Assert.Equal(2, r.OutsideCount);
            Assert.All(r.GetChanges(), c => Assert.Equal(Start.AddDays(4), c.NewDate));

            r.DeleteOutside = true;
            Assert.All(r.GetChanges(), c => Assert.Null(c.NewDate));
        }

        [Fact]
        public void ActivitiesAlreadyOutside_ArePickedUpEvenWithoutADateChange()
        {
            var r = TripDateRealignment.Create(1, Start, Start.AddDays(2), Start, Start.AddDays(2),
                new[] { At(1, 0), At(2, 120), At(3, -3) });

            Assert.Equal(2, r.OutsideCount);
            var targets = r.GetChanges().Select(c => c.NewDate).ToList();
            Assert.Contains(Start.AddDays(2), targets);
            Assert.Contains(Start, targets);
        }

        [Fact]
        public async Task Apply_MovesLeftoversAfterTheLastDaysOwnPlan_InOriginalOrder()
        {
            var keep = new TripActivity { TripId = 1, Date = Start.AddDays(4), Title = "Kalan" };
            var late1 = new TripActivity { TripId = 1, Date = Start.AddDays(6), Title = "Geç1" };
            var late2 = new TripActivity { TripId = 1, Date = Start.AddDays(8), Title = "Geç2" };
            foreach (var a in new[] { late2, keep, late1 })
                await _service.SaveAsync(a);

            var all = await _service.GetByTripIdAsync(1);
            var r = TripDateRealignment.Create(1, Start, Start.AddDays(9), Start, Start.AddDays(4), all);
            await _service.ApplyRealignmentAsync(r);

            var lastDay = (await _service.GetByDateAsync(1, Start.AddDays(4))).Select(a => a.Title).ToArray();
            Assert.Equal(new[] { "Kalan", "Geç1", "Geç2" }, lastDay);
            Assert.Equal(new[] { 0, 1, 2 }, (await _service.GetByDateAsync(1, Start.AddDays(4))).Select(a => a.SortOrder));
        }

        [Fact]
        public async Task Apply_DeleteChoice_RemovesTheLeftovers()
        {
            await _service.SaveAsync(new TripActivity { TripId = 1, Date = Start, Title = "Kalan" });
            await _service.SaveAsync(new TripActivity { TripId = 1, Date = Start.AddDays(7), Title = "Silinecek" });

            var r = TripDateRealignment.Create(1, Start, Start.AddDays(9), Start, Start.AddDays(2), await _service.GetByTripIdAsync(1));
            r.DeleteOutside = true;
            await _service.ApplyRealignmentAsync(r);

            Assert.Equal(new[] { "Kalan" }, (await _service.GetByTripIdAsync(1)).Select(a => a.Title));
        }
    }

    [Collection("Currency")]
    public class TripFormLimitTests
    {
        private static readonly DateTime Start = new(2026, 8, 20);
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IActivityService> _activities = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        private TripFormViewModel Create() => new(_trips.Object, new Mock<IGeocodingService>().Object, new Mock<IExpenseService>().Object,
            new Mock<IExchangeRateService>().Object, new Mock<IBudgetAlertService>().Object,
            new SettingsService(new InMemoryPreferences()), Fakes.Connectivity(), _dialogs.Object, _activities.Object);

        [Fact]
        public async Task TripLongerThanTheLimit_IsNotSaved()
        {
            var vm = Create();
            vm.Name = "Dünya turu";
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            vm.StartDate = Start;
            vm.EndDate = Start.AddDays(AppConstants.MaxTripDays); // limit + 1 days

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(string.Format(AppResources.TripForm_TooLongFormat, AppConstants.MaxTripDays), vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }

        [Fact]
        public void MovingTheStart_KeepsTheEndWithinTheLimit()
        {
            var vm = Create();
            vm.StartDate = Start;
            vm.EndDate = Start.AddDays(80);

            vm.StartDate = Start.AddDays(-30);

            Assert.Equal(Start.AddDays(-30 + AppConstants.MaxTripDays - 1), vm.MaximumEndDate);
            Assert.Equal(vm.MaximumEndDate, vm.EndDate);
        }

        [Fact]
        public async Task HugeBudget_IsRejected()
        {
            var vm = Create();
            vm.Name = "Roma";
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            vm.PlannedBudget = InputLimits.MaxMoneyAmount + 1m;

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.StartsWith(AppResources.Money_TooLargeFormat[..10], vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }

        [Fact]
        public async Task ShorteningATrip_AsksAboutLeftoverActivities_AndAppliesTheChoice()
        {
            _trips.Setup(t => t.GetByIdAsync(9)).ReturnsAsync(new Trip { Id = 9, Name = "Roma", City = "Roma", CurrencyCode = "TRY", StartDate = Start, EndDate = Start.AddDays(9) });
            _trips.Setup(t => t.GetRelatedCountsAsync(9)).ReturnsAsync((0, 0, 0, 1));
            _activities.Setup(a => a.GetByTripIdAsync(9)).ReturnsAsync(new List<TripActivity>
            {
                new() { Id = 1, TripId = 9, Date = Start.AddDays(8), Title = "Müze" }
            });
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(AppResources.TripForm_OutsideDelete);

            var vm = Create();
            vm.TripId = 9;
            await Task.Delay(50);
            vm.EndDate = Start.AddDays(4);
            await vm.SaveCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ChooseAsync(It.Is<string>(t => t.Contains('1')), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()), Times.Once);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Once);
            _activities.Verify(a => a.ApplyRealignmentAsync(It.Is<TripDateRealignment>(r => r.DeleteOutside && r.OutsideCount == 1)), Times.Once);
        }

        [Fact]
        public async Task DismissingTheLeftoverQuestion_SavesNothing()
        {
            _trips.Setup(t => t.GetByIdAsync(9)).ReturnsAsync(new Trip { Id = 9, Name = "Roma", City = "Roma", CurrencyCode = "TRY", StartDate = Start, EndDate = Start.AddDays(9) });
            _activities.Setup(a => a.GetByTripIdAsync(9)).ReturnsAsync(new List<TripActivity>
            {
                new() { Id = 1, TripId = 9, Date = Start.AddDays(8), Title = "Müze" }
            });
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync((string?)null);

            var vm = Create();
            vm.TripId = 9;
            await Task.Delay(50);
            vm.EndDate = Start.AddDays(4);
            await vm.SaveCommand.ExecuteAsync(null);

            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
            _activities.Verify(a => a.ApplyRealignmentAsync(It.IsAny<TripDateRealignment>()), Times.Never);
        }
    }

    public class LongTripScreenTests
    {
        [Fact]
        public void ItineraryNotice_ExplainsTruncationAndHiddenActivities()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 1).AddDays(119) };

            var notice = ItineraryViewModel.BuildRangeNotice(trip, AppConstants.MaxItineraryDays, hiddenActivities: 3);

            Assert.Contains("120", notice);
            Assert.Contains("3", notice);
            Assert.Equal(string.Empty, ItineraryViewModel.BuildRangeNotice(new Trip { StartDate = trip.StartDate, EndDate = trip.StartDate.AddDays(4) }, 5, 0));
        }

        [Fact]
        public void Packing_ClothesAreCappedAtAWeek_ForLongTrips()
        {
            var trip = new Trip { Country = "Türkiye", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 2, 28) };

            var titles = PackingSuggestionEngine.Suggest(trip, null, Array.Empty<string>()).Select(s => s.Title).ToList();

            Assert.Contains(string.Format(AppResources.Packing_ClothesFormat, PackingSuggestionEngine.LongTripDays), titles);
            Assert.Contains(AppResources.Packing_LaundryBag, titles);
        }
    }

    [Collection("Currency")]
    public class MoneyAndInputLimitTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private ExpenseService _expenses = null!;
        private TripService _trips = null!;

        public Task InitializeAsync()
        {
            MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;
            _expenses = new ExpenseService(_db.Service);
            _trips = new TripService(_db.Service, _expenses, new Mock<INotificationService>().Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private async Task<Trip> NewTripAsync()
        {
            var trip = new Trip { Name = "T", City = "C", CurrencyCode = "EUR", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            await _trips.SaveAsync(trip);
            return trip;
        }

        [Fact]
        public async Task Expense_AboveTheLimitOrNegative_IsRejected()
        {
            var trip = await NewTripAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = InputLimits.MaxMoneyAmount + 1m }));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _expenses.SaveAsync(new Expense { TripId = trip.Id, AmountMinor = -5 }));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 5m, ExchangeRate = double.NaN }));
            Assert.Equal(0m, await _expenses.GetTotalExpensesAsync(trip.Id));
        }

        [Fact]
        public async Task CurrencyConversion_ThatWouldOverflow_RollsBackCompletely()
        {
            var trip = await NewTripAsync();
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 10m });
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 9_000_000_000m });

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _expenses.ConvertTripExpensesAsync(trip.Id, "EUR", "HUF", 400m));

            Assert.Equal(9_000_000_010m, await _expenses.GetTotalExpensesAsync(trip.Id));
            Assert.Equal("EUR", (await _trips.GetByIdAsync(trip.Id))!.CurrencyCode);
        }

        [Fact]
        public async Task Trip_WithBrokenDatesOrBudget_IsRejected_AndLongTextIsTrimmed()
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _trips.SaveAsync(new Trip { Name = "X", City = "Y" })); // DateTime.MinValue
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _trips.SaveAsync(new Trip
            {
                Name = "X", City = "Y", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 2), PlannedBudgetMinor = -1
            }));

            var trip = new Trip { Name = new string('a', 500), City = "Y", StartDate = new DateTime(2026, 1, 1), EndDate = new DateTime(2026, 1, 2) };
            await _trips.SaveAsync(trip);
            Assert.Equal(100, (await _trips.GetByIdAsync(trip.Id))!.Name.Length);
        }

        [Theory]
        [InlineData(double.NaN, 0d, false)]
        [InlineData(0d, double.PositiveInfinity, false)]
        [InlineData(91d, 0d, false)]
        [InlineData(0d, -181d, false)]
        [InlineData(41.89, 12.49, true)]
        public void Coordinates_MustBeARealPoint(double lat, double lon, bool valid) =>
            Assert.Equal(valid, PlaceService.IsValidCoordinate(lat, lon));

        [Fact]
        public void InputLimits_RatesAndText()
        {
            Assert.False(InputLimits.IsValidRate(0m));
            Assert.False(InputLimits.IsValidRate(double.PositiveInfinity));
            Assert.True(InputLimits.IsValidRate(37.25m));
            Assert.Equal("abc", InputLimits.Truncate("abcdef", 3));
            Assert.Null(InputLimits.Truncate(null, 3));
        }
    }

    public class BackupSanitizeTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "tp-root", AttachmentService.FolderName);

        [Theory]
        [InlineData("attachments/3/abc.pdf", true)]
        [InlineData("attachments/../../secret.db3", false)]
        [InlineData("../attachments/3/x.pdf", false)]
        [InlineData("/etc/passwd", false)]
        [InlineData("other/3/x.pdf", false)]
        [InlineData("", false)]
        public void AttachmentPaths_MustStayInsideTheAttachmentsFolder(string path, bool safe) =>
            Assert.Equal(safe, BackupService.IsSafeAttachmentPath(path, Root));

        private static BackupService.BackupData ValidData() => new()
        {
            Trips = { new Trip { Id = 1, Name = "Roma", City = "Roma", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) } },
            Expenses = { new Expense { Id = 1, TripId = 1, Amount = 10m } },
            Attachments =
            {
                new TripAttachment { Id = 1, TripId = 1, DisplayName = "ok", RelativePath = "attachments/1/a.pdf" },
                new TripAttachment { Id = 2, TripId = 1, DisplayName = "evil", RelativePath = "attachments/../../travelplanner.db3" }
            }
        };

        [Fact]
        public void Sanitize_DropsUnsafeAttachments_KeepsTheRest()
        {
            var data = ValidData();
            BackupService.Sanitize(data, Root);
            Assert.Equal(new[] { 1 }, data.Attachments.Select(a => a.Id));
        }

        [Fact]
        public void Sanitize_RejectsCorruptMoneyAndDates()
        {
            var badAmount = ValidData();
            badAmount.Expenses[0].AmountMinor = long.MaxValue;
            Assert.Throws<InvalidBackupException>(() => BackupService.Sanitize(badAmount, Root));

            var badDate = ValidData();
            badDate.Trips[0].StartDate = DateTime.MinValue;
            Assert.Throws<InvalidBackupException>(() => BackupService.Sanitize(badDate, Root));

            var nullList = ValidData();
            nullList.Places = null!;
            BackupService.Sanitize(nullList, Root);
            Assert.NotNull(nullList.Places);

            var badCategory = ValidData();
            badCategory.Expenses[0].Category = (ExpenseCategory)42;
            Assert.Throws<InvalidBackupException>(() => BackupService.Sanitize(badCategory, Root));
        }
    }

    /// <summary>SEC-01: the rules shared by a restored backup and a row pulled from the server.</summary>
    public class EntityValidatorTests
    {
        [Fact]
        public void Expense_LimitsMatchTheServer()
        {
            Expense Valid() => new() { AmountMinor = 1250, OriginalAmountMinor = 1250, ExchangeRate = 1, Date = new DateTime(2026, 9, 2), Category = ExpenseCategory.Food };

            Assert.Null(EntityValidator.Validate(Valid()));

            var edge = Valid();
            edge.AmountMinor = InputLimits.MaxMoneyMinor;
            edge.ExchangeRate = (double)InputLimits.MaxExchangeRate;
            edge.Date = InputLimits.MaxDate;
            Assert.Null(EntityValidator.Validate(edge));

            var tooMuch = Valid();
            tooMuch.AmountMinor = InputLimits.MaxMoneyMinor + 1;
            Assert.Equal("invalid amount", EntityValidator.Validate(tooMuch));

            var original = Valid();
            original.OriginalAmountMinor = long.MaxValue;
            Assert.Equal("invalid amount", EntityValidator.Validate(original));

            foreach (var rate in new[] { 1e300, 0.0000001, double.NaN, double.PositiveInfinity, 0d })
            {
                var badRate = Valid();
                badRate.ExchangeRate = rate;
                Assert.Equal("invalid exchange rate", EntityValidator.Validate(badRate));
            }

            var oldDate = Valid();
            oldDate.Date = new DateTime(1800, 1, 1);
            Assert.Equal("invalid date", EntityValidator.Validate(oldDate));

            var category = Valid();
            category.Category = (ExpenseCategory)6;
            Assert.Equal("invalid category", EntityValidator.Validate(category));

            var currency = Valid();
            currency.OriginalCurrencyCode = "XXX";
            Assert.Equal("unknown currency", EntityValidator.Validate(currency));

            var longText = Valid();
            longText.Description = new string('a', 1000);
            Assert.Null(EntityValidator.Validate(longText));
            Assert.Equal(250, longText.Description!.Length);
        }

        [Fact]
        public void Trip_DatesBudgetAndCurrency()
        {
            Trip Valid() => new() { Name = "Roma", StartDate = new DateTime(2026, 8, 20, 15, 0, 0), EndDate = new DateTime(2026, 8, 22), CurrencyCode = "EUR" };

            var trip = Valid();
            Assert.Null(EntityValidator.Validate(trip));
            Assert.Equal(new DateTime(2026, 8, 20), trip.StartDate); // day value without time

            var reversed = Valid();
            reversed.EndDate = new DateTime(2026, 8, 1);
            Assert.Equal("invalid dates", EntityValidator.Validate(reversed));

            var far = Valid();
            far.EndDate = new DateTime(2101, 1, 1);
            Assert.Equal("invalid dates", EntityValidator.Validate(far));

            var budget = Valid();
            budget.PlannedBudgetMinor = -1;
            Assert.Equal("invalid budget", EntityValidator.Validate(budget));

            var currency = Valid();
            currency.CurrencyCode = "ZZZ";
            Assert.Null(EntityValidator.Validate(currency));
            Assert.Null(currency.CurrencyCode); // falls back to the default currency
        }

        [Fact]
        public void Activity_TimesAndOrderAreRepaired_DateIsChecked()
        {
            var activity = new TripActivity
            {
                Title = "Tur", Date = new DateTime(2026, 8, 21), StartTime = new TimeSpan(10, 0, 0), EndTime = new TimeSpan(9, 0, 0), SortOrder = int.MaxValue,
            };
            Assert.Null(EntityValidator.Validate(activity));
            Assert.Null(activity.EndTime);                                  // end before start is dropped
            Assert.Equal(EntityValidator.MaxSortOrder, activity.SortOrder);

            var noStart = new TripActivity { Title = "Tur", Date = new DateTime(2026, 8, 21), EndTime = new TimeSpan(9, 0, 0) };
            Assert.Null(EntityValidator.Validate(noStart));
            Assert.Null(noStart.EndTime);

            var badDay = new TripActivity { Title = "Tur", Date = new DateTime(2200, 1, 1) };
            Assert.Equal("invalid date", EntityValidator.Validate(badDay));
        }

        [Fact]
        public void PlaceAndAttachment()
        {
            Assert.Equal("invalid coordinates", EntityValidator.Validate(new Place { Name = "x", Latitude = double.NaN }));
            Assert.Null(EntityValidator.Validate(new Place { Name = "x", Latitude = 41.89, Longitude = 12.49 }));
            Assert.Equal("invalid kind", EntityValidator.Validate(new TripAttachment { DisplayName = "x", Kind = (AttachmentKind)9 }));
            Assert.Equal("invalid size", EntityValidator.Validate(new TripAttachment { DisplayName = "x", SizeBytes = -1 }));
        }
    }
}
