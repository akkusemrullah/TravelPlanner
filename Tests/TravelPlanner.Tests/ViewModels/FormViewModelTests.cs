using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    [Collection("Currency")] // SettingsService sets the static MoneyFormatter.CurrencyCode
    public class TripFormViewModelTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IGeocodingService> _geocoding = new();
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<IExchangeRateService> _rates = new();
        private readonly Mock<IBudgetAlertService> _alerts = new();

        public TripFormViewModelTests()
        {
            _geocoding.Setup(g => g.SearchCitiesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<CitySuggestion>());
        }

        private TripFormViewModel CreateViewModel(bool online = true, Mock<IDialogService>? dialogs = null) => new(
            _trips.Object, _geocoding.Object, _expenses.Object, _rates.Object, _alerts.Object,
            new SettingsService(new InMemoryPreferences()), Fakes.Connectivity(online), (dialogs ?? Fakes.Dialogs()).Object);

        [Fact]
        public async Task Save_WithoutName_ShowsMessage_AndDoesNotSave()
        {
            var vm = CreateViewModel();
            vm.City = "Roma";

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.True(vm.HasValidationError);
            Assert.Equal("Lütfen seyahat adını girin.", vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }

        [Fact]
        public async Task Save_WithoutCity_ShowsMessage()
        {
            var vm = CreateViewModel();
            vm.Name = "Yaz tatili";

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Lütfen gidilecek şehri girin.", vm.ValidationMessage);
        }

        [Fact]
        public async Task Save_WithEndBeforeStart_ShowsMessage()
        {
            var vm = CreateViewModel();
            vm.Name = "Yaz tatili";
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            vm.StartDate = new DateTime(2026, 8, 20);
            vm.EndDate = new DateTime(2026, 8, 10);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Bitiş tarihi başlangıç tarihinden önce olamaz.", vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }

        [Fact]
        public async Task Save_WithNegativeBudget_ShowsMessage()
        {
            var vm = CreateViewModel();
            vm.Name = "Yaz tatili";
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            vm.PlannedBudget = -1m;

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Planlanan bütçe negatif olamaz.", vm.ValidationMessage);
        }

        [Fact]
        public void MovingStartDatePastEndDate_DragsEndDateAlong()
        {
            var vm = CreateViewModel();
            vm.StartDate = new DateTime(2026, 8, 1);
            vm.EndDate = new DateTime(2026, 8, 3);

            vm.StartDate = new DateTime(2026, 8, 10);

            Assert.Equal(new DateTime(2026, 8, 10), vm.EndDate);
        }

        [Fact]
        public async Task ValidationMessage_Clears_OnceTheProblemIsFixed()
        {
            var vm = CreateViewModel();
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            await vm.SaveCommand.ExecuteAsync(null);
            Assert.True(vm.HasValidationError);

            vm.Name = "Yaz tatili";

            Assert.False(vm.HasValidationError);
        }

        [Fact]
        public void SelectCity_FillsCityAndCountry_AndClosesSuggestions()
        {
            var vm = CreateViewModel();

            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });

            Assert.Equal("Roma", vm.City);
            Assert.Equal("İtalya", vm.Country);
            Assert.False(vm.IsCitySuggestionsVisible);
        }

        [Fact]
        public async Task Save_WithATypedCity_AsksToPickASuggestion()
        {
            var vm = CreateViewModel();
            vm.Name = "Yaz tatili";
            vm.SelectCityCommand.Execute(new CitySuggestion { Name = "Roma", Country = "İtalya" });
            vm.City = "Romaa"; // typed over the picked city

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(TravelPlanner.Resources.Strings.AppResources.TripForm_PickCity, vm.ValidationMessage);
            _trips.Verify(t => t.SaveAsync(It.IsAny<Trip>()), Times.Never);
        }

        [Fact]
        public void CityFromHome_CountsAsPicked()
        {
            var vm = CreateViewModel();

            vm.InitialCity = "Roma";
            vm.InitialCountry = "İtalya";

            Assert.Equal("Roma", vm.City);
            Assert.Equal("İtalya", vm.Country);
            Assert.Equal("Roma", vm.Name);
        }

        [Fact]
        public void TypingACityWhileOffline_ShowsTheOfflineHint()
        {
            var vm = CreateViewModel(online: false);

            vm.City = "Rom";

            Assert.True(vm.IsOffline);
        }

        [Fact]
        public void NewTrip_UsesTheLocalizedAddTitle()
        {
            Assert.Equal("Seyahat Ekle", CreateViewModel().PageTitle); // TP-302: "Seyehat" typo fixed
        }
    }

    public class ExpenseFormViewModelTests
    {
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IExchangeRateService> _rates = new();
        private readonly Mock<IBudgetAlertService> _alerts = new();

        private ExpenseFormViewModel CreateViewModel() => new(_expenses.Object, _trips.Object, _rates.Object, _alerts.Object, Fakes.Split().Object, Fakes.Dialogs().Object);

        [Fact]
        public async Task Save_WithZeroAmount_ShowsMessage_AndDoesNotSave()
        {
            var vm = CreateViewModel();

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Lütfen sıfırdan büyük bir tutar girin.", vm.ValidationMessage);
            _expenses.Verify(e => e.SaveAsync(It.IsAny<Expense>()), Times.Never);
        }

        [Fact]
        public async Task EnteringAPositiveAmount_ClearsTheMessage()
        {
            var vm = CreateViewModel();
            await vm.SaveCommand.ExecuteAsync(null);

            vm.Amount = 25m;

            Assert.False(vm.HasValidationError);
        }

        [Fact]
        public async Task DateOutsideTheTrip_ShowsANonBlockingWarning()
        {
            var trip = new Trip { Id = 3, Name = "Roma", City = "Roma", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 27) };
            _trips.Setup(t => t.GetByIdAsync(3)).ReturnsAsync(trip);
            var vm = CreateViewModel();

            vm.TripId = 3;
            await Task.Delay(50); // trip loads in the background

            Assert.Equal(new DateTime(2026, 8, 20), vm.Date); // new expense for a future trip starts on day 1
            Assert.False(vm.HasDateWarning);

            vm.Date = new DateTime(2026, 9, 2);
            Assert.True(vm.HasDateWarning);
            Assert.Contains("20 – 27 Ağustos", vm.DateWarning);

            // Still saveable - the warning doesn't block (TP-308).
            vm.Amount = 10m;
            _expenses.Setup(e => e.SaveAsync(It.IsAny<Expense>())).ReturnsAsync(1);
            await vm.SaveCommand.ExecuteAsync(null);
            _expenses.Verify(e => e.SaveAsync(It.Is<Expense>(x => x.Date == new DateTime(2026, 9, 2))), Times.Once);
        }
    }

    public class PlaceFormViewModelTests
    {
        private readonly Mock<IPlaceService> _places = new();
        private readonly Mock<IGeocodingService> _geocoding = new();

        public PlaceFormViewModelTests()
        {
            _geocoding.Setup(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new List<PlaceSuggestion>());
        }

        [Fact]
        public async Task Save_WithoutPickingASuggestion_AsksToPickOne()
        {
            var vm = new PlaceFormViewModel(_places.Object, _geocoding.Object, Fakes.Connectivity(), Fakes.Dialogs().Object);
            vm.Name = "Kolezyum";

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Lütfen yazarken çıkan öneri listesinden bir konum seçin.", vm.ValidationMessage);
            _places.Verify(p => p.SaveAsync(It.IsAny<Place>()), Times.Never);
        }

        [Fact]
        public async Task Save_WithoutName_ShowsMessage()
        {
            var vm = new PlaceFormViewModel(_places.Object, _geocoding.Object, Fakes.Connectivity(), Fakes.Dialogs().Object);

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Lütfen yer adını girin.", vm.ValidationMessage);
        }

        [Fact]
        public void SelectingASuggestion_SetsCoordinates_AndClearsTheMessage()
        {
            var vm = new PlaceFormViewModel(_places.Object, _geocoding.Object, Fakes.Connectivity(), Fakes.Dialogs().Object);

            vm.SelectPlaceCommand.Execute(new PlaceSuggestion { Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 });

            Assert.Equal("Kolezyum", vm.Name);
            Assert.Equal(41.89, vm.Latitude);
            Assert.Equal(12.49, vm.Longitude);
            Assert.False(vm.HasValidationError);
        }
    }
}
