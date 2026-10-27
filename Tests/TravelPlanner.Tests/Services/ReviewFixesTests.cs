using System.Globalization;
using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>"Seyahati kopyala" (TripService.DuplicateAsync).</summary>
    public class TripDuplicateTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly Mock<INotificationService> _notifications = new();
        private TripService _trips = null!;

        public Task InitializeAsync()
        {
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), _notifications.Object,
                                     new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task Duplicate_CopiesPlanPlacesTodosAndParticipants_ButNotExpenses()
        {
            var start = DateTime.Today.AddDays(20);
            var trip = new Trip { Name = "Roma", City = "Roma", Country = "İtalya", StartDate = start, EndDate = start.AddDays(3), PlannedBudget = 900m, CurrencyCode = "EUR" };
            await _trips.SaveAsync(trip);

            var places = new PlaceService(_db.Service);
            var colosseum = new Place { TripId = trip.Id, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49, IsVisited = true };
            await places.SaveAsync(colosseum);
            await new ActivityService(_db.Service).SaveAsync(new TripActivity { TripId = trip.Id, Date = start.AddDays(1), Title = "Tur", PlaceId = colosseum.Id });
            await new TodoService(_db.Service).SaveAsync(new TodoItem { TripId = trip.Id, Title = "Pasaport", IsCompleted = true });
            await new SplitService(_db.Service).SaveParticipantAsync(trip.Id, "Ali");
            await new ExpenseService(_db.Service).SaveAsync(new Expense { TripId = trip.Id, Amount = 50m });

            var copy = await _trips.DuplicateAsync(trip.Id, "Roma (kopya)");

            Assert.NotNull(copy);
            Assert.NotEqual(trip.Id, copy!.Id);
            Assert.NotEqual(trip.SyncId, copy.SyncId);
            Assert.Equal("Roma (kopya)", copy.Name);
            Assert.Equal(start, copy.StartDate);
            Assert.Equal(900m, copy.PlannedBudget);
            Assert.Equal("EUR", copy.CurrencyCode);

            var copiedPlace = Assert.Single(await places.GetByTripIdAsync(copy.Id));
            Assert.False(copiedPlace.IsVisited);
            Assert.NotEqual(colosseum.Id, copiedPlace.Id);

            var copiedActivity = Assert.Single(await new ActivityService(_db.Service).GetByTripIdAsync(copy.Id));
            Assert.Equal(copiedPlace.Id, copiedActivity.PlaceId); // linked to the COPIED place

            var copiedTodo = Assert.Single(await new TodoService(_db.Service).GetByTripIdAsync(copy.Id));
            Assert.Equal("Pasaport", copiedTodo.Title);
            Assert.False(copiedTodo.IsCompleted);

            Assert.Equal("Ali", Assert.Single(await new SplitService(_db.Service).GetParticipantsAsync(copy.Id)).Name);
            Assert.Empty(await new ExpenseService(_db.Service).GetByTripIdAsync(copy.Id));

            // The original is untouched.
            Assert.Single(await new ExpenseService(_db.Service).GetByTripIdAsync(trip.Id));
            Assert.True(Assert.Single(await places.GetByTripIdAsync(trip.Id)).IsVisited);
        }

        [Fact]
        public async Task Duplicate_OfAMissingTrip_ReturnsNull()
        {
            Assert.Null(await _trips.DuplicateAsync(12345, "x"));
        }

        [Fact]
        public void DuplicateName_StaysWithinTheNameLimit()
        {
            Assert.Equal("Roma (kopya)", TripDetailViewModel.DuplicateName("Roma"));

            var longName = new string('a', 100);
            var copyName = TripDetailViewModel.DuplicateName(longName);
            Assert.True(copyName.Length <= TripFormViewModel.NameMaxLength);
            Assert.EndsWith("(kopya)", copyName);
        }
    }

    /// <summary>Expense CSV export.</summary>
    public class ExpenseCsvTests
    {
        [Fact]
        public void Build_UsesSemicolonsAndCommaDecimals_InTurkish()
        {
            var tr = new CultureInfo("tr-TR");
            var expenses = new[]
            {
                new Expense { Id = 1, Date = new DateTime(2026, 8, 20), Category = ExpenseCategory.Food, Description = "Akşam yemeği; pizza",
                              Amount = 1850m, OriginalAmount = 50m, OriginalCurrencyCode = "EUR", ExchangeRate = 37, PaidByParticipantId = 7 },
                new Expense { Id = 2, Date = new DateTime(2026, 8, 21), Category = ExpenseCategory.Transportation, Amount = 120.5m },
            };

            var csv = ExpenseCsv.Build(expenses, "TRY", new Dictionary<int, string> { [7] = "Ayşe" }, tr);
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(3, lines.Length);
            Assert.StartsWith("Tarih;Kategori;Açıklama;Tutar", lines[0]);
            Assert.Equal("2026-08-20;Yemek;\"Akşam yemeği; pizza\";1850,00;TRY;50,00;EUR;37;Ayşe", lines[1]);
            // An expense from before multi-currency support counts as typed in the trip currency.
            Assert.Equal("2026-08-21;Ulaşım;;120,50;TRY;120,50;TRY;1;", lines[2]);
        }

        [Fact]
        public void Build_UsesCommas_WhereTheDecimalSeparatorIsADot()
        {
            var csv = ExpenseCsv.Build(new[] { new Expense { Date = new DateTime(2026, 1, 2), Amount = 3.5m } },
                                       "EUR", new Dictionary<int, string>(), new CultureInfo("en-US"));

            Assert.Contains("2026-01-02,Ulaşım,,3.50,EUR,3.50,EUR,1,", csv); // default category = Transportation
        }

        [Theory]
        [InlineData("=SUM(A1:A9)", "'=SUM(A1:A9)")]
        [InlineData("+90 555", "'+90 555")]
        [InlineData("@home", "'@home")]
        [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
        [InlineData("Taksi", "Taksi")]
        public void Field_NeutralisesFormulasAndQuotes(string value, string expected)
        {
            Assert.Equal(expected, ExpenseCsv.Field(value, ";"));
        }

        [Fact]
        public void FileName_DropsCharactersAFileCantHold()
        {
            Assert.Equal("Roma Tatili - harcamalar.csv", ExpenseCsv.FileName("Roma Tatili"));
            Assert.Equal("RomaParis - harcamalar.csv", ExpenseCsv.FileName("Roma/Paris"));
            Assert.Equal("TravelPlanner - harcamalar.csv", ExpenseCsv.FileName("  "));
        }
    }

    public class ReviewFixesTests
    {
        [Fact]
        public void BudgetAlertKey_UsesTheGlobalTripId_WhenThereIsOne()
        {
            Assert.Equal("budget_alert.5", BudgetAlertService.KeyFor(5, null));
            Assert.Equal("budget_alert.0b7c", BudgetAlertService.KeyFor(5, "0b7c"));
        }

        [Fact]
        public async Task BudgetAlert_MovesTheOldIdKey_SoAShownAlertIsNotRepeated()
        {
            var trip = new Trip { Id = 5, SyncId = "abc", Name = "Roma", City = "Roma", PlannedBudget = 1000m };
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(5)).ReturnsAsync(trip);
            var expenses = new Mock<IExpenseService>();
            expenses.Setup(e => e.GetBudgetUsagePercentageAsync(5, 1000m)).ReturnsAsync(85d);
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), It.IsAny<int>(), It.IsAny<double>())).ReturnsAsync(true);
            var preferences = new InMemoryPreferences();
            preferences.Set("budget_alert.5", 80); // shown by an older version

            await new BudgetAlertService(trips.Object, expenses.Object, notifications.Object, Fakes.Dialogs().Object, preferences).CheckAsync(5);

            notifications.Verify(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), It.IsAny<int>(), It.IsAny<double>()), Times.Never);
            Assert.Equal(80, preferences.Get("budget_alert.abc", 0));
            Assert.False(preferences.ContainsKey("budget_alert.5"));
        }

        [Fact]
        public async Task EditingAnExistingPlace_RenamingKeepsItsLocation()
        {
            var places = new Mock<IPlaceService>();
            places.Setup(p => p.GetByIdAsync(3)).ReturnsAsync(new Place { Id = 3, TripId = 2, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 });
            var geocoding = new Mock<IGeocodingService>();
            geocoding.Setup(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<PlaceSuggestion>());

            var vm = new PlaceFormViewModel(places.Object, geocoding.Object, Fakes.Connectivity(online: false), Fakes.Dialogs().Object) { TripId = 2 };
            vm.PlaceId = 3;
            await Task.Delay(50);

            vm.Name = "Colosseum"; // a typo fix / rename - offline, no suggestion to pick
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.False(vm.HasValidationError);
            places.Verify(p => p.SaveAsync(It.Is<Place>(x => x.Id == 3 && x.Name == "Colosseum" && x.Latitude == 41.89 && x.Longitude == 12.49)), Times.Once);
        }

        [Fact]
        public async Task ExpenseList_ShowsTheTripCurrency_EvenWithoutExpenses()
        {
            var expenses = new Mock<IExpenseService>();
            expenses.Setup(e => e.GetByTripIdAsync(4)).ReturnsAsync(new List<Expense>());
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(4)).ReturnsAsync(new Trip { Id = 4, Name = "Paris", City = "Paris", CurrencyCode = "EUR" });

            var vm = new ExpenseListViewModel(expenses.Object, new Mock<IBudgetAlertService>().Object, Fakes.Dialogs().Object, trips.Object) { TripId = 4 };
            await vm.LoadExpensesCommand.ExecuteAsync(null);

            Assert.Equal("EUR", vm.CurrencyCode);
            Assert.Contains("€", vm.TotalExpensesFormatted);
            Assert.False(vm.ShowExport);
        }
    
        [Fact]
        public async Task TP104_ExpenseList_CardsChipsAndPayerNames()
        {
            var expenses = new Mock<IExpenseService>();
            expenses.Setup(e => e.GetByTripIdAsync(4)).ReturnsAsync(new List<Expense>
            {
                new() { Id = 1, TripId = 4, Amount = 80m, Category = ExpenseCategory.Food, PaidByParticipantId = 9 },
                new() { Id = 2, TripId = 4, Amount = 40m, Category = ExpenseCategory.Shopping, Description = "Hediye" },
            });
            expenses.Setup(e => e.GetMyTotalForTripAsync(4, It.IsAny<string?>())).ReturnsAsync(60m);
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(4)).ReturnsAsync(new Trip { Id = 4, Name = "Paris", City = "Paris", CurrencyCode = "EUR", PlannedBudget = 100m });
            var split = Fakes.Split();
            split.Setup(s => s.GetParticipantsAsync(4)).ReturnsAsync(new List<Participant> { new() { Id = 9, TripId = 4, Name = "Ali" } });

            var vm = new ExpenseListViewModel(expenses.Object, new Mock<IBudgetAlertService>().Object, Fakes.Dialogs().Object, trips.Object, split.Object) { TripId = 4 };
            await vm.LoadExpensesCommand.ExecuteAsync(null);

            Assert.Equal(120m, vm.AllTotal);
            Assert.Equal(60m, vm.MyTotal);
            Assert.True(vm.IsOverBudget);
            Assert.Equal("Paris", vm.TripName);
            Assert.Equal("Ali", vm.Expenses.Single(e => e.Id == 1).PaidByName);
            Assert.Equal("Hediye", vm.Expenses.Single(e => e.Id == 2).DisplayTitle);

            vm.SelectCategoryFilterCommand.Execute(vm.CategoryFilters.Single(c => c.Key == nameof(ExpenseCategory.Shopping)));

            Assert.Single(vm.Expenses);
            Assert.Single(vm.CategoryFilters, c => c.IsSelected);
            Assert.Equal(120m, vm.AllTotal); // the card keeps the whole trip
        }
    }
}
