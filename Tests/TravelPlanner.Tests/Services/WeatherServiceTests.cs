using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class WeatherServiceTests
    {
        private const string ForecastJson =
            """{"current":{"temperature_2m":21.5,"relative_humidity_2m":60,"wind_speed_10m":3.2,"weather_code":2}}""";

        private readonly FakeHttpMessageHandler _http = new FakeHttpMessageHandler().Respond("api.open-meteo.com/v1/forecast", ForecastJson);
        private readonly Mock<IGeocodingService> _geocoding = new();

        public WeatherServiceTests()
        {
            _geocoding.Setup(g => g.GeocodeAsync("Roma", "İtalya")).ReturnsAsync((41.89, 12.48));
        }

        private WeatherService CreateService(bool online = true, IResponseCache? cache = null) =>
            new(_http.CreateClient(), _geocoding.Object, Fakes.Connectivity(online), cache ?? new ResponseCache());

        [Fact]
        public async Task MapsCurrentConditions()
        {
            var weather = await CreateService().GetWeatherAsync("Roma", "İtalya");

            Assert.NotNull(weather);
            Assert.Equal(21.5, weather!.Temperature);
            Assert.Equal(60, weather.Humidity);
            Assert.Equal(3.2, weather.WindSpeed);
            Assert.Equal("Parçalı bulutlu", weather.Condition);
            Assert.Contains("latitude=41.89", _http.Requests.Single().Query);
        }

        [Fact]
        public async Task SecondLookup_IsServedFromCache()
        {
            var service = CreateService();

            await service.GetWeatherAsync("Roma", "İtalya");
            await service.GetWeatherAsync("roma ", "İTALYA");

            Assert.Single(_http.Requests);
        }

        [Fact]
        public async Task ReturnsNull_WhenOffline_WithoutCallingTheApi()
        {
            var weather = await CreateService(online: false).GetWeatherAsync("Roma", "İtalya");

            Assert.Null(weather);
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task ReturnsNull_WhenCityCannotBeGeocoded()
        {
            var weather = await CreateService().GetWeatherAsync("Atlantis", "Okyanus");

            Assert.Null(weather);
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task ReturnsNull_OnServerError()
        {
            var failing = new FakeHttpMessageHandler().Respond("forecast", "{}", System.Net.HttpStatusCode.InternalServerError);
            var service = new WeatherService(failing.CreateClient(), _geocoding.Object, Fakes.Connectivity(), new ResponseCache());

            Assert.Null(await service.GetWeatherAsync("Roma", "İtalya"));
        }

        [Fact]
        public async Task TripOverload_UsesCityAndCountry()
        {
            var weather = await CreateService().GetWeatherAsync(new Trip { City = "Roma", Country = "İtalya" });

            Assert.NotNull(weather);
        }

        [Theory]
        [InlineData(0, "Açık gökyüzü")]
        [InlineData(3, "Bulutlu")]
        [InlineData(63, "Yağmurlu")]
        [InlineData(75, "Kar yağışlı")]
        [InlineData(95, "Gök gürültülü fırtına")]
        [InlineData(1234, "Bilinmiyor")]
        public void DescribesWmoWeatherCodes(int code, string expected)
        {
            Assert.Equal(expected, WeatherService.DescribeWeatherCode(code).Description);
        }
    }
}
