using Moq;
using Xunit;
using Microsoft.Maui.Media;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    [Collection("Currency")]
    public class ExpenseFormSplitTests
    {
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<ISplitService> _split = Fakes.Split();

        public ExpenseFormSplitTests()
        {
            _trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma", CurrencyCode = "TRY", StartDate = DateTime.Today.AddDays(-1), EndDate = DateTime.Today.AddDays(2) });
            _split.Setup(s => s.GetParticipantsAsync(1)).ReturnsAsync(new List<Participant>
            {
                new() { Id = 5, TripId = 1, Name = "Ali" },
                new() { Id = 6, TripId = 1, Name = "Can" },
            });
            _expenses.Setup(e => e.SaveAsync(It.IsAny<Expense>())).ReturnsAsync(1);
        }

        private async Task<ExpenseFormViewModel> CreateAsync()
        {
            var vm = new ExpenseFormViewModel(_expenses.Object, _trips.Object, new Mock<IExchangeRateService>().Object,
                new Mock<IBudgetAlertService>().Object, _split.Object, Fakes.Dialogs().Object);
            vm.TripId = 1;
            await Task.Delay(50);
            return vm;
        }

        [Fact]
        public async Task ChoosingAPayer_SelectsEveryone_AndSavesTheSplit()
        {
            var vm = await CreateAsync();
            Assert.True(vm.HasParticipants);
            Assert.False(vm.IsSplitting);

            vm.SelectedPayer = vm.PayerOptions.Single(p => p.Value == 5);
            Assert.True(vm.IsSplitting);
            Assert.All(vm.SplitParticipants, p => Assert.True(p.IsSelected));

            vm.SplitParticipants[1].IsSelected = false; // only Ali shares it
            vm.Amount = 40m;
            await vm.SaveCommand.ExecuteAsync(null);

            _split.Verify(s => s.SaveSplitAsync(It.IsAny<Expense>(), 5, It.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(new[] { 5 }))), Times.Once);
        }

        [Fact]
        public async Task PayerWithoutParticipants_IsRejected()
        {
            var vm = await CreateAsync();
            vm.SelectedPayer = vm.PayerOptions.Single(p => p.Value == 6);
            foreach (var p in vm.SplitParticipants) p.IsSelected = false;
            vm.Amount = 10m;

            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal(AppResources.Split_ParticipantsRequired, vm.ValidationMessage);
            _expenses.Verify(e => e.SaveAsync(It.IsAny<Expense>()), Times.Never);
        }

        [Fact]
        public async Task NoPayer_ClearsAnyEarlierSplit()
        {
            var vm = await CreateAsync();
            vm.Amount = 10m;

            await vm.SaveCommand.ExecuteAsync(null);

            _split.Verify(s => s.SaveSplitAsync(It.IsAny<Expense>(), null, It.Is<IReadOnlyCollection<int>>(ids => ids.Count == 0)), Times.Once);
        }
    }

    [Collection("Currency")]
    public class SplitViewModelTests
    {
        [Fact]
        public async Task Load_ShowsBalancesAndSettlements()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma", CurrencyCode = "TRY" });
            var ali = new Participant { Id = 1, Name = "Ali" };
            var can = new Participant { Id = 2, Name = "Can" };
            var balances = new List<ParticipantBalance> { new(ali, 10000, 5000), new(can, 0, 5000) };
            var split = new Mock<ISplitService>();
            split.Setup(s => s.GetSummaryAsync(1)).ReturnsAsync(new SplitSummary(balances, SplitCalculator.Settle(balances), 10000, 2));

            var vm = new SplitViewModel(trips.Object, split.Object, Fakes.Dialogs().Object) { TripId = 1 };
            await vm.LoadCommand.ExecuteAsync(null);

            Assert.Equal(2, vm.Balances.Count);
            Assert.True(vm.Balances[0].IsCreditor);
            Assert.True(vm.Balances[1].IsDebtor);
            Assert.Contains("Can, Ali için", Assert.Single(vm.Settlements));
            Assert.True(vm.HasUnsplit);
            Assert.Contains("2", vm.UnsplitText);
        }

        [Fact]
        public async Task AddingADuplicateName_ShowsAMessage()
        {
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma" });
            var split = new Mock<ISplitService>();
            split.Setup(s => s.SaveParticipantAsync(1, "Ali", 0)).ThrowsAsync(new DuplicateParticipantException("Ali"));
            var dialogs = Fakes.Dialogs();

            var vm = new SplitViewModel(trips.Object, split.Object, dialogs.Object) { TripId = 1, NewParticipantName = "Ali" };
            await vm.AddParticipantCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("Ali"))), Times.Once);
            Assert.Equal("Ali", vm.NewParticipantName); // kept so the user can fix it
        }
    }

    public class AttachmentsViewModelTests
    {
        private readonly Mock<IAttachmentService> _attachments = new();
        private readonly Mock<IMediaPicker> _media = new();
        private readonly Mock<IFilePicker> _files = new();
        private readonly Mock<ILauncher> _launcher = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        private AttachmentsViewModel Create() =>
            new(_attachments.Object, _media.Object, _files.Object, _launcher.Object, _dialogs.Object) { TripId = 4 };

        [Fact]
        public async Task CancelledPick_AddsNothing()
        {
            _files.Setup(f => f.PickAsync(It.IsAny<PickOptions?>())).ReturnsAsync((FileResult?)null);

            await Create().PickDocumentCommand.ExecuteAsync(null);

            _attachments.Verify(a => a.AddAsync(It.IsAny<int>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
            _dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception?>()), Times.Never);
        }

        [Fact]
        public async Task CameraWithoutPermission_ExplainsWhy()
        {
            _media.Setup(m => m.CapturePhotoAsync(It.IsAny<MediaPickerOptions?>())).ThrowsAsync(new PermissionException("camera"));

            await Create().TakePhotoCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), AppResources.Attachments_PermissionDenied), Times.Once);
        }

        [Fact]
        public async Task Delete_AsksFirst_ThenRemoves()
        {
            var item = new TripAttachment { Id = 1, TripId = 4, DisplayName = "bilet.pdf", RelativePath = "attachments/4/x.pdf" };
            _attachments.Setup(a => a.GetByTripIdAsync(4)).ReturnsAsync(new List<TripAttachment> { item });
            var vm = Create();
            await vm.LoadCommand.ExecuteAsync(null);

            vm.Selection.Start();
            await vm.AttachmentTappedCommand.ExecuteAsync(item);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ConfirmAsync(AppResources.Attachments_DeleteTitle, It.Is<string>(m => m.Contains("bilet.pdf")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _attachments.Verify(a => a.DeleteAsync(item), Times.Once);
            Assert.True(vm.HasNoAttachments);
        }
    }

    public class MapRouteTests
    {
        [Fact]
        public void Routes_FollowPlanOrder_AndNeedTwoStops()
        {
            var day1 = new DateTime(2026, 8, 20);
            var places = new List<Place>
            {
                new() { Id = 1, Name = "Kolezyum" }, new() { Id = 2, Name = "Forum" }, new() { Id = 3, Name = "Pantheon" },
            };
            var activities = new List<TripActivity>
            {
                new() { Date = day1, SortOrder = 2, PlaceId = 3 },
                new() { Date = day1, SortOrder = 0, PlaceId = 1 },
                new() { Date = day1, SortOrder = 1, PlaceId = 1 },   // same place twice in a row - one stop
                new() { Date = day1, SortOrder = 3, PlaceId = 99 },  // deleted place - ignored
                new() { Date = day1.AddDays(1), SortOrder = 0, PlaceId = 2 }, // single stop - no route
            };

            var routes = MapViewModel.BuildRoutes(activities, places);

            var route = Assert.Single(routes);
            Assert.Equal(day1, route.Key);
            Assert.Equal(new[] { "Kolezyum", "Pantheon" }, route.Value.Select(p => p.Name));
        }

        [Theory]
        [InlineData("travelplanner://addplace?lat=41.89021&lon=12.492231", true, 41.89021, 12.492231)]
        [InlineData("TRAVELPLANNER://addplace?lon=-0.1276&lat=51.5072", true, 51.5072, -0.1276)]
        [InlineData("travelplanner://addplace?lat=95&lon=10", false, 0, 0)]
        [InlineData("https://tiles.openfreemap.org/styles/liberty", false, 0, 0)]
        [InlineData(null, false, 0, 0)]
        public void ParsesLongPressUrls(string? url, bool ok, double lat, double lon)
        {
            Assert.Equal(ok, MapViewModel.TryParseAddPlaceUrl(url, out var la, out var lo));
            if (ok)
            {
                Assert.Equal(lat, la, precision: 6);
                Assert.Equal(lon, lo, precision: 6);
            }
        }
    }

    public class PlaceFromMapTests
    {
        [Fact]
        public async Task CoordinatesFromTheMap_SurviveTypingAName_AndGetANameSuggestion()
        {
            var places = new Mock<IPlaceService>();
            var geocoding = new Mock<IGeocodingService>();
            geocoding.Setup(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<PlaceSuggestion>());
            geocoding.Setup(g => g.ReverseGeocodeAsync(41.9, 12.5, It.IsAny<CancellationToken>()))
                     .ReturnsAsync(new PlaceSuggestion { Name = "Fontana di Trevi", Latitude = 41.9, Longitude = 12.5 });

            var vm = new PlaceFormViewModel(places.Object, geocoding.Object, Fakes.Connectivity(), Fakes.Dialogs().Object) { TripId = 2 };
            vm.LatitudeParameter = "41.9";
            vm.LongitudeParameter = "12.5";
            await Task.Delay(50);

            Assert.True(vm.IsLocationFromMap);
            Assert.Equal("Fontana di Trevi", vm.Name);

            vm.Name = "Trevi Çeşmesi"; // the user renames it - coordinates must stay
            await vm.SaveCommand.ExecuteAsync(null);

            places.Verify(p => p.SaveAsync(It.Is<Place>(x => x.Name == "Trevi Çeşmesi" && x.Latitude == 41.9 && x.Longitude == 12.5)), Times.Once);
        }
    }
}
