using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-50: "Yol tarifi" on the map page.</summary>
    public class DirectionsTests
    {
        private readonly Mock<IMap> _map = new();
        private readonly Mock<ILauncher> _launcher = new();

        private DirectionsService CreateService() => new(_map.Object, _launcher.Object);

        [Fact]
        public async Task Opens_map_app_in_driving_mode()
        {
            _map.Setup(m => m.TryOpenAsync(41.0082, 28.9784, It.IsAny<MapLaunchOptions>())).ReturnsAsync(true);

            Assert.True(await CreateService().OpenAsync(41.0082, 28.9784, "Ayasofya"));

            _map.Verify(m => m.TryOpenAsync(41.0082, 28.9784,
                It.Is<MapLaunchOptions>(o => o.NavigationMode == NavigationMode.Driving && o.Name == "Ayasofya")), Times.Once);
            _launcher.Verify(l => l.TryOpenAsync(It.IsAny<Uri>()), Times.Never);
        }

        [Fact]
        public async Task Falls_back_to_browser_when_no_map_app()
        {
            _map.Setup(m => m.TryOpenAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<MapLaunchOptions>())).ReturnsAsync(false);
            _launcher.Setup(l => l.TryOpenAsync(It.IsAny<Uri>())).ReturnsAsync(true);

            Assert.True(await CreateService().OpenAsync(41.0082, 28.9784, null));

            _launcher.Verify(l => l.TryOpenAsync(It.Is<Uri>(u =>
                u.ToString() == "https://www.google.com/maps/dir/?api=1&destination=41.0082,28.9784")), Times.Once);
        }

        [Fact]
        public async Task Falls_back_to_browser_when_map_app_throws()
        {
            _map.Setup(m => m.TryOpenAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<MapLaunchOptions>()))
                .ThrowsAsync(new InvalidOperationException());
            _launcher.Setup(l => l.TryOpenAsync(It.IsAny<Uri>())).ReturnsAsync(true);

            Assert.True(await CreateService().OpenAsync(1, 2, null));
        }

        [Theory]
        [InlineData(double.NaN, 28.9)]
        [InlineData(91, 28.9)]
        [InlineData(41, 181)]
        public async Task Invalid_coordinate_opens_nothing(double lat, double lon)
        {
            Assert.False(await CreateService().OpenAsync(lat, lon, null));
            _map.VerifyNoOtherCalls();
            _launcher.VerifyNoOtherCalls();
        }

        [Fact]
        public void Web_url_uses_invariant_culture()
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
                Assert.Equal("https://www.google.com/maps/dir/?api=1&destination=-33.8568,151.2153",
                    DirectionsService.BuildWebUrl(-33.8568, 151.2153).ToString());
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
            }
        }

        [Theory]
        [InlineData("travelplanner://selectplace?id=12", true, 12)]
        [InlineData("TRAVELPLANNER://selectplace?id=3", true, 3)]
        [InlineData("travelplanner://selectplace?id=abc", false, 0)]
        [InlineData("travelplanner://selectplace", false, 0)]
        [InlineData("travelplanner://addplace?lat=1&lon=2", false, 0)]
        [InlineData(null, false, 0)]
        public void Parses_select_place_url(string? url, bool ok, int id)
        {
            Assert.Equal(ok, MapViewModel.TryParseSelectPlaceUrl(url, out var parsed));
            Assert.Equal(id, parsed);
        }

        private static MapViewModel CreateViewModel(Mock<IDirectionsService> directions, Mock<IDialogService>? dialogs = null) =>
            new(Mock.Of<IPlaceService>(), Mock.Of<ITripService>(), Mock.Of<IGeocodingService>(), Mock.Of<IConnectivity>(),
                Mock.Of<IActivityService>(), directions.Object, (dialogs ?? new Mock<IDialogService>()).Object);

        [Fact]
        public async Task Selecting_a_pin_shows_directions_and_opens_it()
        {
            var directions = new Mock<IDirectionsService>();
            directions.Setup(d => d.OpenAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string?>())).ReturnsAsync(true);
            var vm = CreateViewModel(directions);
            vm.Pins.Add(new Place { Id = 1, Name = "A", Latitude = 41, Longitude = 29 });
            vm.Pins.Add(new Place { Id = 2, Name = "B", Latitude = 40, Longitude = 30 });

            Assert.False(vm.CanGetDirections);
            vm.SelectPlace(2);
            Assert.True(vm.CanGetDirections);
            Assert.Equal("B", vm.SelectedPlace?.Name);

            await vm.OpenDirectionsCommand.ExecuteAsync(null);
            directions.Verify(d => d.OpenAsync(40, 30, "B"), Times.Once);

            vm.ClearSelectionCommand.Execute(null);
            Assert.False(vm.CanGetDirections);
        }

        [Fact]
        public void Unknown_pin_clears_selection()
        {
            var vm = CreateViewModel(new Mock<IDirectionsService>());
            vm.Pins.Add(new Place { Id = 1, Name = "A", Latitude = 41, Longitude = 29 });
            vm.SelectPlace(1);
            vm.SelectPlace(99);
            Assert.Null(vm.SelectedPlace);
        }

        [Fact]
        public void Place_without_usable_coordinate_hides_the_button()
        {
            var vm = CreateViewModel(new Mock<IDirectionsService>());
            vm.SelectedPlace = new Place { Id = 1, Name = "A", Latitude = double.NaN, Longitude = 29 };
            Assert.False(vm.CanGetDirections);
        }

        [Fact]
        public async Task Shows_error_when_nothing_opens()
        {
            var directions = new Mock<IDirectionsService>();
            directions.Setup(d => d.OpenAsync(It.IsAny<double>(), It.IsAny<double>(), It.IsAny<string?>())).ReturnsAsync(false);
            var dialogs = new Mock<IDialogService>();
            var vm = CreateViewModel(directions, dialogs);
            vm.SelectedPlace = new Place { Id = 1, Name = "A", Latitude = 41, Longitude = 29 };

            await vm.OpenDirectionsCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception?>()), Times.Once);
        }
    }
}
