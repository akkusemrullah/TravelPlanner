using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    [Collection("Currency")]
    public class ExpenseFormCurrencyTests
    {
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IExchangeRateService> _rates = new();
        private readonly Mock<IBudgetAlertService> _alerts = new();

        public ExpenseFormCurrencyTests()
        {
            _trips.Setup(t => t.GetByIdAsync(3)).ReturnsAsync(new Trip
            {
                Id = 3, Name = "Roma", City = "Roma", CurrencyCode = "TRY",
                StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27)
            });
            _expenses.Setup(e => e.SaveAsync(It.IsAny<Expense>())).ReturnsAsync(1);
        }

        private async Task<ExpenseFormViewModel> CreateLoadedAsync()
        {
            var vm = new ExpenseFormViewModel(_expenses.Object, _trips.Object, _rates.Object, _alerts.Object, Fakes.Split().Object, Fakes.Dialogs().Object);
            vm.TripId = 3;
            await Task.Delay(50); // trip loads in the background
            return vm;
        }

        [Fact]
        public async Task NewExpense_StartsInTheTripsCurrency()
        {
            var vm = await CreateLoadedAsync();

            Assert.Equal("TRY", vm.SelectedCurrency!.Value);
        }

        [Fact]
        public async Task ForeignCurrency_IsConvertedWithTheExpenseDatesRate_AndTheOriginalIsKept()
        {
            _rates.Setup(r => r.GetRateAsync("EUR", "TRY", new DateTime(2026, 8, 21), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new ExchangeRateQuote("EUR", "TRY", 37m, new DateTime(2026, 8, 21)));
            var vm = await CreateLoadedAsync();

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "EUR");
            vm.Date = new DateTime(2026, 8, 21);
            vm.Amount = 50m;
            await vm.SaveCommand.ExecuteAsync(null);

            _expenses.Verify(e => e.SaveAsync(It.Is<Expense>(x =>
                x.Amount == 1850m && x.OriginalAmount == 50m && x.OriginalCurrencyCode == "EUR" && x.ExchangeRate == 37d)), Times.Once);
            _alerts.Verify(a => a.CheckAsync(3), Times.Once);
        }

        [Fact]
        public async Task EditingOnlyTheDescription_KeepsTheStoredConversion_WithoutARate()
        {
            _expenses.Setup(e => e.GetByIdAsync(11)).ReturnsAsync(new Expense
            {
                Id = 11, TripId = 3, TripCurrencyCode = "TRY", Date = new DateTime(2026, 8, 21),
                Amount = 1850m, OriginalAmount = 50m, OriginalCurrencyCode = "EUR", ExchangeRate = 37d
            });
            var vm = new ExpenseFormViewModel(_expenses.Object, _trips.Object, _rates.Object, _alerts.Object, Fakes.Split().Object, Fakes.Dialogs().Object);
            vm.ExpenseId = 11;
            vm.TripId = 3;
            await Task.Delay(50);

            vm.Description = "Müze bileti";
            await vm.SaveCommand.ExecuteAsync(null);

            // The rate mock answers null for everything, so a re-fetch would have blocked the save.
            Assert.False(vm.HasValidationError);
            _expenses.Verify(e => e.SaveAsync(It.Is<Expense>(x => x.Amount == 1850m && x.Description == "Müze bileti")), Times.Once);
        }

        [Fact]
        public async Task MissingRate_BlocksSaving_WithAClearMessage()
        {
            var vm = await CreateLoadedAsync();

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "USD");
            vm.Amount = 10m;
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(AppResources.Fx_Unavailable, vm.ValidationMessage);
            _expenses.Verify(e => e.SaveAsync(It.IsAny<Expense>()), Times.Never);
        }

        [Fact]
        public async Task ConversionPreview_ShowsTheConvertedAmountAndRate()
        {
            _rates.Setup(r => r.GetRateAsync("EUR", "TRY", It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new ExchangeRateQuote("EUR", "TRY", 37.25m, new DateTime(2026, 8, 19)));
            var vm = await CreateLoadedAsync();

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "EUR");
            vm.Amount = 10m;
            await Task.Delay(450); // debounce + lookup

            Assert.True(vm.HasConversionPreview);
            Assert.Contains("372,50", vm.ConversionPreview);
            Assert.Contains("1 EUR = 37,25 TRY", vm.ConversionPreview);
        }
    }

    [Collection("Currency")]
    public class TripFormCurrencyTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IGeocodingService> _geocoding = new();
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<IExchangeRateService> _rates = new();
        private readonly Mock<IBudgetAlertService> _alerts = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs(confirm: true);

        private async Task<TripFormViewModel> EditTripAsync(int expenseCount)
        {
            _trips.Setup(t => t.GetByIdAsync(9)).ReturnsAsync(new Trip
            {
                Id = 9, Name = "Roma", City = "Roma", CurrencyCode = "TRY",
                StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27), PlannedBudget = 1000m
            });
            _trips.Setup(t => t.GetRelatedCountsAsync(9)).ReturnsAsync((0, expenseCount, 0, 0));

            var vm = new TripFormViewModel(_trips.Object, _geocoding.Object, _expenses.Object, _rates.Object, _alerts.Object,
                new SettingsService(new InMemoryPreferences()), Fakes.Connectivity(), _dialogs.Object);
            vm.TripId = 9;
            await Task.Delay(50);
            return vm;
        }

        [Fact]
        public async Task ChangingCurrency_WithExpenses_AsksAndConvertsThem()
        {
            _rates.Setup(r => r.GetRateAsync("TRY", "EUR", null, It.IsAny<CancellationToken>()))
                  .ReturnsAsync(new ExchangeRateQuote("TRY", "EUR", 0.027m, DateTime.Today));
            var vm = await EditTripAsync(expenseCount: 4);

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "EUR");
            await vm.SaveCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ConfirmAsync(AppResources.TripForm_CurrencyChangeTitle, It.Is<string>(m => m.Contains("4")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _expenses.Verify(e => e.ConvertTripExpensesAsync(9, "TRY", "EUR", 0.027m), Times.Once);
            _trips.Verify(t => t.SaveAsync(It.Is<Trip>(x => x.CurrencyCode == "EUR")), Times.Once);
        }

        [Fact]
        public async Task ChangingCurrency_WithoutExpenses_JustSaves()
        {
            var vm = await EditTripAsync(expenseCount: 0);

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "USD");
            await vm.SaveCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _trips.Verify(t => t.SaveAsync(It.Is<Trip>(x => x.CurrencyCode == "USD")), Times.Once);
            _alerts.Verify(a => a.CheckAsync(9), Times.Once);
        }

        [Fact]
        public async Task ChangingCurrency_WithoutARate_DoesNotSave()
        {
            var vm = await EditTripAsync(expenseCount: 2);

            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "GBP");
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(AppResources.Fx_Unavailable, vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
            _expenses.Verify(e => e.ConvertTripExpensesAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<decimal>()), Times.Never);
        }
    }

    public class ItineraryViewModelTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IActivityService> _activities = new();
        private readonly Mock<IPlaceService> _places = new();

        private static readonly DateTime Start = new(2026, 8, 20);

        public ItineraryViewModelTests()
        {
            _trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma", StartDate = Start, EndDate = Start.AddDays(2) });
            _places.Setup(p => p.GetByTripIdAsync(1)).ReturnsAsync(new List<Place> { new() { Id = 7, TripId = 1, Name = "Kolezyum" } });
            _activities.Setup(a => a.GetByTripIdAsync(1)).ReturnsAsync(new List<TripActivity>
            {
                new() { Id = 1, TripId = 1, Date = Start, Title = "A", SortOrder = 0, PlaceId = 7 },
                new() { Id = 2, TripId = 1, Date = Start, Title = "B", SortOrder = 1 },
                new() { Id = 3, TripId = 1, Date = Start, Title = "C", SortOrder = 2 },
                new() { Id = 4, TripId = 1, Date = Start.AddDays(1), Title = "D", SortOrder = 0 },
            });
        }

        private async Task<ItineraryViewModel> LoadAsync()
        {
            var vm = new ItineraryViewModel(_trips.Object, _activities.Object, _places.Object, Fakes.Dialogs().Object) { TripId = 1 };
            await vm.LoadCommand.ExecuteAsync(null);
            return vm;
        }

        [Fact]
        public async Task Load_BuildsOneTabPerDay_AndShowsTheFirstDay()
        {
            var vm = await LoadAsync();

            Assert.Equal(3, vm.Days.Count);
            Assert.Equal("1. Gün", vm.Days[0].Title);
            Assert.Equal(3, vm.Days[0].ActivityCount);
            Assert.True(vm.Days[0].IsSelected);
            Assert.Equal(new[] { "A", "B", "C" }, vm.DayActivities.Select(a => a.Title));
            Assert.Equal("Kolezyum", vm.DayActivities[0].PlaceName);
        }

        [Fact]
        public async Task SelectingADay_ShowsItsActivities_OrTheEmptyState()
        {
            var vm = await LoadAsync();

            vm.SelectDayCommand.Execute(vm.Days[1]);
            Assert.Equal("D", Assert.Single(vm.DayActivities).Title);

            vm.SelectDayCommand.Execute(vm.Days[2]);
            Assert.Empty(vm.DayActivities);
            Assert.True(vm.HasNoActivities);
        }

        [Fact]
        public async Task DragAndDrop_ReordersAndPersists()
        {
            var vm = await LoadAsync();
            var c = vm.DayActivities[2];
            var a = vm.DayActivities[0];

            vm.DragStartingCommand.Execute(c);
            await vm.DropCommand.ExecuteAsync(a);

            Assert.Equal(new[] { "C", "A", "B" }, vm.DayActivities.Select(x => x.Title));
            _activities.Verify(s => s.ReorderAsync(It.Is<IReadOnlyList<TripActivity>>(l => l.Select(x => x.Title).SequenceEqual(new[] { "C", "A", "B" }))), Times.Once);
        }

        [Fact]
        public async Task GripDrag_MovesStepByStep_AndSavesOnceWhenReleased()
        {
            var vm = await LoadAsync();
            var a = vm.DayActivities[0];

            Assert.True(vm.BeginReorder(a));
            Assert.True(vm.MoveStep(a, +1));
            Assert.True(vm.MoveStep(a, +1));
            Assert.False(vm.MoveStep(a, +1)); // already last
            _activities.Verify(s => s.ReorderAsync(It.IsAny<IReadOnlyList<TripActivity>>()), Times.Never); // nothing saved while dragging

            await vm.EndReorderAsync();

            Assert.Equal(new[] { "B", "C", "A" }, vm.DayActivities.Select(x => x.Title));
            _activities.Verify(s => s.ReorderAsync(It.Is<IReadOnlyList<TripActivity>>(l => l.Select(x => x.Title).SequenceEqual(new[] { "B", "C", "A" }))), Times.Once);
        }

        [Fact]
        public async Task GripDrag_WithoutMoving_SavesNothing()
        {
            var vm = await LoadAsync();

            Assert.True(vm.BeginReorder(vm.DayActivities[1]));
            await vm.EndReorderAsync();

            _activities.Verify(s => s.ReorderAsync(It.IsAny<IReadOnlyList<TripActivity>>()), Times.Never);
        }

        [Fact]
        public async Task SelectedActivities_AreDeletedTogether_AndTheDayClosesUp()
        {
            var dialogs = Fakes.Dialogs();
            var vm = new ItineraryViewModel(_trips.Object, _activities.Object, _places.Object, dialogs.Object) { TripId = 1 };
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.Start();
            await vm.ActivityTappedCommand.ExecuteAsync(vm.DayActivities[0]);
            await vm.ActivityTappedCommand.ExecuteAsync(vm.DayActivities[2]);
            Assert.False(vm.BeginReorder(vm.DayActivities[1])); // no dragging while ticking rows
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ConfirmAsync(AppResources.Selection_DeleteSelectedTitle, It.Is<string>(m => m.Contains("2 plan etkinliği")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _activities.Verify(s => s.DeleteAsync(It.IsAny<TripActivity>()), Times.Exactly(2));
            Assert.Equal(new[] { "B" }, vm.DayActivities.Select(x => x.Title));
            _activities.Verify(s => s.ReorderAsync(It.Is<IReadOnlyList<TripActivity>>(l => l.Count == 1 && l[0].Title == "B")), Times.Once);
            Assert.Equal(1, vm.Days[0].ActivityCount);
            Assert.False(vm.Selection.IsActive);
        }

        [Fact]
        public async Task SelectAll_DeletesTheOpenDay_AndKeepsTheOtherDays()
        {
            var dialogs = Fakes.Dialogs();
            var vm = new ItineraryViewModel(_trips.Object, _activities.Object, _places.Object, dialogs.Object) { TripId = 1 };
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.StartCommand.Execute(null);
            vm.Selection.ToggleAllCommand.Execute(null);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ConfirmAsync(AppResources.Selection_DeleteAllTitle, It.Is<string>(m => m.Contains("3 plan etkinliği")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _activities.Verify(s => s.DeleteAsync(It.IsAny<TripActivity>()), Times.Exactly(3));
            Assert.True(vm.HasNoActivities);
            Assert.True(vm.HasAnyActivities);
            Assert.Equal(1, vm.Days[1].ActivityCount);
        }

        [Fact]
        public void BuildDays_IsCapped()
        {
            var trip = new Trip { StartDate = Start, EndDate = Start.AddYears(3) };

            Assert.Equal(TravelPlanner.Helpers.AppConstants.MaxItineraryDays, ItineraryViewModel.BuildDays(trip).Count);
        }
    }

    public class ActivityFormViewModelTests
    {
        private ActivityFormViewModel Create() =>
            new(new Mock<ITripService>().Object, new Mock<IActivityService>().Object, new Mock<IPlaceService>().Object, Fakes.Dialogs().Object);

        [Fact]
        public void TitleIsRequired()
        {
            Assert.Equal(AppResources.Activity_TitleRequired, Create().Validate());
        }

        [Fact]
        public void TappingTheTimeRows_TogglesThem()
        {
            var vm = Create();

            vm.ToggleHasTimeCommand.Execute(null);
            vm.ToggleHasEndTimeCommand.Execute(null);
            Assert.True(vm.HasTime);
            Assert.True(vm.HasEndTime);

            vm.ToggleHasTimeCommand.Execute(null);
            Assert.False(vm.HasTime);
        }

        [Fact]
        public void EndTimeMustBeAfterStart()
        {
            var vm = Create();
            vm.Title = "Müze";
            vm.HasTime = true;
            vm.StartTime = new TimeSpan(14, 0, 0);
            vm.HasEndTime = true;
            vm.EndTime = new TimeSpan(13, 0, 0);

            Assert.Equal(AppResources.Activity_EndBeforeStart, vm.Validate());

            vm.EndTime = new TimeSpan(16, 0, 0);
            Assert.Null(vm.Validate());
        }

        [Fact]
        public async Task Load_ClampsTheRequestedDayIntoTheTrip_AndListsPlaces()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) });
            var places = new Mock<IPlaceService>();
            places.Setup(p => p.GetByTripIdAsync(1)).ReturnsAsync(new List<Place> { new() { Id = 4, Name = "Pantheon" } });
            var vm = new ActivityFormViewModel(trips.Object, new Mock<IActivityService>().Object, places.Object, Fakes.Dialogs().Object)
            {
                TripId = 1,
                DateParameter = "2026-09-30"
            };

            await vm.LoadCommand.ExecuteAsync(null);

            Assert.Equal(new DateTime(2026, 8, 22), vm.Date);
            Assert.Equal(2, vm.PlaceOptions.Count); // "Yer yok" + Pantheon
            Assert.Null(vm.SelectedPlace!.Value);
            Assert.True(vm.HasPlaces);
        }
    }

    public class ExpenseAnalysisTests
    {
        [Fact]
        public void DailyBars_CoverEveryTripDay_AndExpensesOutsideIt()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            var daily = new List<DailyExpenseTotal>
            {
                new() { Date = new DateTime(2026, 8, 19), Total = 40m },
                new() { Date = new DateTime(2026, 8, 21), Total = 15m },
            };

            var bars = ExpenseAnalysisViewModel.BuildDailyBars(trip, daily);

            Assert.Equal(4, bars.Count);
            Assert.Equal(new DateTime(2026, 8, 19), bars[0].Date);
            Assert.Equal(new[] { 40m, 0m, 15m, 0m }, bars.Select(b => b.Total));
        }

        [Fact]
        public void DailyBars_KeepTheTripInView_WhenAnExpenseIsMonthsEarlier()
        {
            var trip = new Trip { StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            var daily = new List<DailyExpenseTotal> { new() { Date = new DateTime(2026, 1, 5), Total = 900m } }; // flight booked in January

            var bars = ExpenseAnalysisViewModel.BuildDailyBars(trip, daily);

            Assert.Equal(TravelPlanner.Helpers.AppConstants.MaxItineraryDays, bars.Count);
            Assert.Equal(new DateTime(2026, 8, 22), bars[^1].Date);
            Assert.Contains(bars, b => b.Date == new DateTime(2026, 8, 20));
        }

        [Fact]
        public async Task Load_ComputesShares_AndDailyBudget()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 23), PlannedBudget = 400m, CurrencyCode = "EUR" });
            var expenses = new Mock<IExpenseService>();
            expenses.Setup(e => e.GetCategorySummariesAsync(1)).ReturnsAsync(new List<ExpenseCategorySummary>
            {
                new() { Category = ExpenseCategory.Food, Total = 75m, Count = 3 },
                new() { Category = ExpenseCategory.Transportation, Total = 25m, Count = 1 },
            });
            expenses.Setup(e => e.GetDailyTotalsAsync(1)).ReturnsAsync(new List<DailyExpenseTotal> { new() { Date = new DateTime(2026, 8, 20), Total = 100m } });

            var vm = new ExpenseAnalysisViewModel(trips.Object, expenses.Object, Fakes.Dialogs().Object) { TripId = 1 };
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.True(vm.HasData);
            Assert.Equal(0.75, vm.Categories[0].Fraction, precision: 6);
            Assert.Equal("%75", vm.Categories[0].PercentText);
            Assert.Equal(100m, vm.DailyChart.DailyBudget); // 400 / 4 days
            Assert.Equal(2, vm.CategoryChart.Shares.Count);
            Assert.True(vm.HasDailyBudget);
        }
    }

    public class TodoPackingTests
    {
        private readonly Mock<ITodoService> _todos = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IWeatherService> _weather = new();
        private readonly Mock<IPackingService> _packing = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        public TodoPackingTests()
        {
            _trips.Setup(t => t.GetByIdAsync(2)).ReturnsAsync(new Trip { Id = 2, Name = "Roma", City = "Roma" });
            _todos.Setup(t => t.GetByTripIdAsync(2)).ReturnsAsync(new List<TodoItem> { new() { Id = 1, TripId = 2, Title = "Pasaport" } });
        }

        private async Task<TodoListViewModel> LoadAsync()
        {
            var vm = new TodoListViewModel(_todos.Object, _trips.Object, _weather.Object, _packing.Object, _dialogs.Object) { TripId = 2 };
            await vm.LoadTasksCommand.ExecuteAsync(null);
            return vm;
        }

        [Fact]
        public async Task OnlySelectedSuggestions_AreAdded()
        {
            _packing.Setup(p => p.GetSuggestions(It.IsAny<Trip>(), It.IsAny<IReadOnlyList<DailyForecast>?>(), It.IsAny<IEnumerable<string>>(),
                                                 It.IsAny<IEnumerable<PackingPlanEntry>?>()))
                    .Returns(new[] { new PackingSuggestion("Şemsiye", "r"), new PackingSuggestion("Mont", "r") });
            var vm = await LoadAsync();

            await vm.ShowSuggestionsCommand.ExecuteAsync(null);
            Assert.True(vm.IsSuggestionPanelVisible);
            Assert.Equal(AppResources.Packing_NoForecastHint, vm.SuggestionHint); // weather mock returns no forecast

            vm.ToggleSuggestionCommand.Execute(vm.Suggestions[1]); // untick "Mont"
            await vm.AddSelectedSuggestionsCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "Pasaport", "Şemsiye" }, vm.Tasks.Select(t => t.Title));
            Assert.False(vm.IsSuggestionPanelVisible);
        }

        [Fact]
        public async Task Suggestions_AreBuiltFromTheTripsPlan()
        {
            var plan = new[] { new PackingPlanEntry("Kaputaş Plajı", "Kaputaş Plajı") };
            _packing.Setup(p => p.GetPlanAsync(It.IsAny<Trip>())).ReturnsAsync(plan);
            _packing.Setup(p => p.GetSuggestions(It.IsAny<Trip>(), It.IsAny<IReadOnlyList<DailyForecast>?>(), It.IsAny<IEnumerable<string>>(),
                                                 It.IsAny<IEnumerable<PackingPlanEntry>?>()))
                    .Returns(Array.Empty<PackingSuggestion>());
            var vm = await LoadAsync();

            await vm.ShowSuggestionsCommand.ExecuteAsync(null);

            _packing.Verify(p => p.GetSuggestions(It.IsAny<Trip>(), It.IsAny<IReadOnlyList<DailyForecast>?>(), It.IsAny<IEnumerable<string>>(), plan), Times.Once);
        }

        [Fact]
        public async Task Suggestions_StillShow_WhenThePlanCannotBeRead()
        {
            _packing.Setup(p => p.GetPlanAsync(It.IsAny<Trip>())).ThrowsAsync(new InvalidOperationException("db"));
            _packing.Setup(p => p.GetSuggestions(It.IsAny<Trip>(), It.IsAny<IReadOnlyList<DailyForecast>?>(), It.IsAny<IEnumerable<string>>(), null))
                    .Returns(new[] { new PackingSuggestion("Şemsiye", "r") });
            var vm = await LoadAsync();

            await vm.ShowSuggestionsCommand.ExecuteAsync(null);

            Assert.Single(vm.Suggestions);
        }

        [Fact]
        public async Task ApplyingATemplate_SkipsItemsAlreadyOnTheList()
        {
            var template = new PackingTemplate { Id = 1, Name = "Kış", Items = "pasaport\nMont\nBere" };
            _packing.Setup(p => p.GetTemplatesAsync()).ReturnsAsync(new List<PackingTemplate> { template });
            _dialogs.SetupSequence(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(AppResources.Packing_ApplyTemplate)
                    .ReturnsAsync(template.Display);
            var vm = await LoadAsync();

            await vm.ManageTemplatesCommand.ExecuteAsync(null);

            Assert.Equal(new[] { "Pasaport", "Mont", "Bere" }, vm.Tasks.Select(t => t.Title));
            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("2"))), Times.Once);
        }

        [Fact]
        public async Task SavingATemplate_UsesTheCurrentList()
        {
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(AppResources.Packing_SaveAsTemplate);
            _dialogs.Setup(d => d.PromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                    .ReturnsAsync("Yaz");
            _packing.Setup(p => p.SaveTemplateAsync("Yaz", It.IsAny<IEnumerable<string>>()))
                    .ReturnsAsync(new PackingTemplate { Name = "Yaz", Items = "Pasaport" });
            var vm = await LoadAsync();

            await vm.ManageTemplatesCommand.ExecuteAsync(null);

            _packing.Verify(p => p.SaveTemplateAsync("Yaz", It.Is<IEnumerable<string>>(i => i.SequenceEqual(new[] { "Pasaport" }))), Times.Once);
        }
    }

    public class WeatherTripForecastViewModelTests
    {
        [Fact]
        public async Task ShowsTheTripDays_AndTheAverageNote()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma" });
            var weather = new Mock<IWeatherService>();
            weather.Setup(w => w.GetWeatherAsync(It.IsAny<Trip>())).ReturnsAsync(new WeatherInfo { Temperature = 20 });
            weather.Setup(w => w.GetTripForecastAsync(It.IsAny<Trip>())).ReturnsAsync(new List<DailyForecast>
            {
                new() { Date = DateTime.Today, Kind = ForecastKind.Forecast },
                new() { Date = DateTime.Today.AddDays(20), Kind = ForecastKind.ClimateAverage },
            });

            var vm = new WeatherViewModel(trips.Object, weather.Object, Fakes.Connectivity(), Fakes.Dialogs().Object);
            vm.TripId = 1;
            await vm.RefreshCommand.ExecuteAsync(null);

            Assert.True(vm.HasTripForecast);
            Assert.Equal(2, vm.TripForecast.Count);
            Assert.True(vm.HasClimateAverages);
            Assert.False(vm.TripForecastUnavailable);
        }
    }
}
