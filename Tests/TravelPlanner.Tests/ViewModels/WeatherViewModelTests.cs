using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class WeatherViewModelTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IWeatherService> _weather = new();

        public WeatherViewModelTests()
        {
            _trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip { Id = 1, Name = "Roma", City = "Roma", Country = "İtalya" });
        }

        private async Task<WeatherViewModel> LoadAsync(bool online)
        {
            var vm = new WeatherViewModel(_trips.Object, _weather.Object, Fakes.Connectivity(online), Fakes.Dialogs().Object);
            vm.TripId = 1;
            await vm.RefreshCommand.ExecuteAsync(null); // joins the load TripId started
            return vm;
        }

        [Fact]
        public async Task Offline_ShowsTheOfflineMessage()
        {
            var vm = await LoadAsync(online: false);

            Assert.True(vm.HasError);
            Assert.Equal(AppResources.Weather_Offline, vm.ErrorMessage);
            Assert.True(vm.HasLoadedOnce);
            Assert.False(vm.IsInitialLoading);
        }

        [Fact]
        public async Task OnlineButNoData_ShowsTheGenericMessage()
        {
            var vm = await LoadAsync(online: true);

            Assert.True(vm.HasError);
            Assert.Equal(AppResources.Weather_Error, vm.ErrorMessage);
        }

        [Fact]
        public async Task Success_ClearsTheError()
        {
            _weather.Setup(w => w.GetWeatherAsync(It.IsAny<Trip>())).ReturnsAsync(new WeatherInfo { Temperature = 21, Condition = "Açık gökyüzü" });

            var vm = await LoadAsync(online: true);

            Assert.False(vm.HasError);
            Assert.Equal(21, vm.Weather!.Temperature);
        }
    }
}
