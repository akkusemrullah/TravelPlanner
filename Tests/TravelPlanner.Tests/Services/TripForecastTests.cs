using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class TripForecastTests
    {
        private static readonly DateTime Today = new(2026, 10, 8);

        private const string ForecastJson = """
            {"daily":{"time":["2026-10-20","2026-10-21","2026-10-22","2026-10-23"],
                      "weather_code":[0,61,3,null],
                      "temperature_2m_max":[24.1,19.0,21.5,null],
                      "temperature_2m_min":[15.2,13.0,14.0,null],
                      "precipitation_sum":[0.0,6.4,0.2,null],
                      "precipitation_probability_max":[5,80,20,null]}}
            """;

        private static string ArchiveJson(int year, int max, int rain, int code) =>
            "{\"daily\":{\"time\":[\"" + year + "-10-24\",\"" + year + "-10-25\",\"" + year + "-10-26\"]," +
            "\"weather_code\":[" + code + "," + code + "," + code + "]," +
            "\"temperature_2m_max\":[" + max + "," + max + "," + max + "]," +
            "\"temperature_2m_min\":[10,10,10]," +
            "\"precipitation_sum\":[" + rain + "," + rain + "," + rain + "]}}";

        private readonly Mock<IGeocodingService> _geocoding = new();

        public TripForecastTests()
        {
            _geocoding.Setup(g => g.GeocodeAsync("Roma", "İtalya")).ReturnsAsync((41.89, 12.48));
        }

        private static Trip RomeTrip() => new()
        {
            Id = 1, Name = "Roma", City = "Roma", Country = "İtalya",
            StartDate = new DateTime(2026, 10, 20), EndDate = new DateTime(2026, 10, 26)
        };

        private static FakeHttpMessageHandler Http() => new FakeHttpMessageHandler()
            .Respond("api.open-meteo.com/v1/forecast", ForecastJson)
            .Respond("start_date=2025-10-24", ArchiveJson(2025, 20, 0, 1))
            .Respond("start_date=2024-10-24", ArchiveJson(2024, 22, 3, 61))
            .Respond("start_date=2023-10-24", ArchiveJson(2023, 18, 2, 61));

        private WeatherService CreateService(FakeHttpMessageHandler http, bool online = true) =>
            new(http.CreateClient(), _geocoding.Object, Fakes.Connectivity(online), new ResponseCache()) { Today = () => Today };

        [Fact]
        public async Task CombinesForecastDays_WithPastYearsAverages()
        {
            var http = Http();

            var days = await CreateService(http).GetTripForecastAsync(RomeTrip());

            Assert.NotNull(days);
            Assert.Equal(7, days!.Count);
            Assert.Equal(new DateTime(2026, 10, 20), days[0].Date);
            Assert.Equal(new DateTime(2026, 10, 26), days[^1].Date);

            // Inside the 16-day range: the real forecast.
            Assert.All(days.Take(4), d => Assert.Equal(ForecastKind.Forecast, d.Kind));
            Assert.Equal(24.1, days[0].TemperatureMax);
            Assert.Equal(80, days[1].PrecipitationProbability);
            Assert.Equal("Yağmurlu", days[1].Condition);

            // Beyond it: average of 2025/2024/2023.
            Assert.All(days.Skip(4), d => Assert.Equal(ForecastKind.ClimateAverage, d.Kind));
            Assert.Equal(20.0, days[4].TemperatureMax);           // (20 + 22 + 18) / 3
            Assert.Equal(1.7, days[4].PrecipitationSum);           // (0 + 3 + 2) / 3
            Assert.Equal(67, days[4].PrecipitationProbability);    // 2 of 3 years had >= 1 mm
            Assert.Equal(61, days[4].WeatherCode);                 // most frequent code

            Assert.Equal(4, http.Requests.Count); // 1 forecast + 3 archive years
            Assert.Contains(http.Requests, r => r.Query.Contains("start_date=2026-10-20") && r.Query.Contains("end_date=2026-10-23"));
        }

        [Fact]
        public async Task Offline_ReturnsNull_WithoutRequests()
        {
            var http = Http();

            Assert.Null(await CreateService(http, online: false).GetTripForecastAsync(RomeTrip()));
            Assert.Empty(http.Requests);
        }

        [Fact]
        public async Task SecondCall_IsServedFromCache()
        {
            var http = Http();
            var service = CreateService(http);

            await service.GetTripForecastAsync(RomeTrip());
            await service.GetTripForecastAsync(RomeTrip());

            Assert.Equal(4, http.Requests.Count);
        }

        [Fact]
        public void Plan_SplitsTheTripIntoObservedForecastAndClimate()
        {
            // 60 days ago .. 40 days ahead
            var plan = WeatherService.PlanTripForecast(Today.AddDays(-60), Today.AddDays(40), Today);

            Assert.Equal(new WeatherService.DateRange(Today.AddDays(-60), Today.AddDays(-31)), plan.Observed);
            Assert.Equal(new WeatherService.DateRange(Today.AddDays(-30), Today.AddDays(15)), plan.Forecast);
            Assert.Equal(new WeatherService.DateRange(Today.AddDays(16), Today.AddDays(40)), plan.Climate);
        }

        [Fact]
        public void Plan_ForATripNextWeek_IsAllForecast()
        {
            var plan = WeatherService.PlanTripForecast(Today.AddDays(5), Today.AddDays(9), Today);

            Assert.Null(plan.Observed);
            Assert.Null(plan.Climate);
            Assert.Equal(new WeatherService.DateRange(Today.AddDays(5), Today.AddDays(9)), plan.Forecast);
        }

        [Fact]
        public void ClimateAverages_MatchByCalendarDay_AcrossLeapYears()
        {
            // Trip 28 Feb - 2 Mar 2027; a 2024 sample has 29 Feb, a 2025 sample doesn't.
            static WeatherService.DailyValues V(int y, int m, int d, double max) => new(new DateTime(y, m, d), 1, max, 0, 0, null);
            var leap = new List<WeatherService.DailyValues> { V(2024, 2, 28, 10), V(2024, 2, 29, 99), V(2024, 3, 1, 12), V(2024, 3, 2, 14) };
            var normal = new List<WeatherService.DailyValues> { V(2025, 2, 28, 20), V(2025, 3, 1, 22), V(2025, 3, 2, 24) };

            var days = WeatherService.AverageYears(new DateTime(2027, 2, 28), 3, new[] { leap, normal });

            Assert.Equal(new[] { 15.0, 17.0, 19.0 }, days.Select(d => d.TemperatureMax!.Value));
        }

        [Fact]
        public void DailyForecast_DisplayHelpers()
        {
            var day = new DailyForecast { TemperatureMax = 23.6, TemperatureMin = 14.4, PrecipitationProbability = 40, Kind = ForecastKind.ClimateAverage };

            Assert.Equal("24° / 14°", day.TemperatureRange);
            Assert.Equal("%40", day.PrecipitationDisplay);
            Assert.Equal("Ortalama", day.KindLabel);
            Assert.True(day.IsClimateAverage);
        }
    }
}
