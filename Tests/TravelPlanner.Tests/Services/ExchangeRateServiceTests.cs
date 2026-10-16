using Moq;
using Xunit;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class ExchangeRateServiceTests
    {
        private const string LatestJson = """{"amount":1.0,"base":"EUR","date":"2026-10-07","rates":{"TRY":37.5}}""";

        private readonly InMemoryPreferences _preferences = new();

        private ExchangeRateService CreateService(FakeHttpMessageHandler http, bool online = true) =>
            new(http.CreateClient(), Fakes.Connectivity(online), new ResponseCache(), _preferences)
            {
                Today = () => new DateTime(2026, 10, 8)
            };

        [Fact]
        public async Task SameCurrency_IsOneToOne_WithoutCallingTheApi()
        {
            var http = new FakeHttpMessageHandler();

            var quote = await CreateService(http).GetRateAsync("try", "TRY");

            Assert.Equal(1m, quote!.Rate);
            Assert.Empty(http.Requests);
        }

        [Fact]
        public async Task Latest_ParsesTheRate_AndUsesFrankfurter()
        {
            var http = new FakeHttpMessageHandler().Respond("api.frankfurter.dev/v1/latest", LatestJson);

            var quote = await CreateService(http).GetRateAsync("EUR", "TRY");

            Assert.NotNull(quote);
            Assert.Equal(37.5m, quote!.Rate);
            Assert.Equal(new DateTime(2026, 10, 7), quote.RateDate);
            Assert.Equal(1875.00m, quote.Convert(50m));
            var request = Assert.Single(http.Requests);
            Assert.Contains("base=EUR", request.Query);
            Assert.Contains("symbols=TRY", request.Query);
        }

        [Fact]
        public async Task PastDate_AsksForThatDaysRate()
        {
            var http = new FakeHttpMessageHandler().Respond("api.frankfurter.dev/v1/2026-09-01", LatestJson.Replace("2026-10-07", "2026-09-01"));

            var quote = await CreateService(http).GetRateAsync("EUR", "TRY", new DateTime(2026, 9, 1));

            Assert.Equal(new DateTime(2026, 9, 1), quote!.RateDate);
            Assert.Contains("/v1/2026-09-01", Assert.Single(http.Requests).AbsolutePath);
        }

        [Fact]
        public async Task FutureDate_UsesTheLatestRate()
        {
            var http = new FakeHttpMessageHandler().Respond("/latest", LatestJson);

            await CreateService(http).GetRateAsync("EUR", "TRY", new DateTime(2026, 12, 24));

            Assert.Contains("/latest", Assert.Single(http.Requests).AbsolutePath);
        }

        [Fact]
        public async Task SecondLookup_IsCached()
        {
            var http = new FakeHttpMessageHandler().Respond("/latest", LatestJson);
            var service = CreateService(http);

            await service.GetRateAsync("EUR", "TRY");
            await service.GetRateAsync("eur", "try");

            Assert.Single(http.Requests);
        }

        [Fact]
        public async Task Offline_FallsBackToTheLastStoredRate_AlsoInverted()
        {
            var online = CreateService(new FakeHttpMessageHandler().Respond("/latest", LatestJson));
            await online.GetRateAsync("EUR", "TRY"); // stores EUR->TRY on the device

            var http = new FakeHttpMessageHandler();
            var offline = CreateService(http, online: false);

            var direct = await offline.GetRateAsync("EUR", "TRY");
            var inverse = await offline.GetRateAsync("TRY", "EUR");

            Assert.Equal(37.5m, direct!.Rate);
            Assert.True(direct.IsOfflineFallback);
            Assert.Equal(Math.Round(1m / 37.5m, 6), inverse!.Rate);
            Assert.Empty(http.Requests);
        }

        [Fact]
        public async Task OfflineWithoutAnyStoredRate_ReturnsNull()
        {
            Assert.Null(await CreateService(new FakeHttpMessageHandler(), online: false).GetRateAsync("USD", "TRY"));
        }

        [Fact]
        public async Task ServerError_ReturnsNull()
        {
            var http = new FakeHttpMessageHandler().Respond("/latest", "{}", System.Net.HttpStatusCode.InternalServerError);

            Assert.Null(await CreateService(http).GetRateAsync("USD", "TRY"));
        }

        [Fact]
        public async Task TryGetCachedRate_NeverHitsTheNetwork()
        {
            var http = new FakeHttpMessageHandler().Respond("/latest", LatestJson);
            var service = CreateService(http);

            Assert.False(service.TryGetCachedRate("EUR", "TRY", out _));
            await service.GetRateAsync("EUR", "TRY");

            Assert.True(service.TryGetCachedRate("EUR", "TRY", out var rate));
            Assert.Equal(37.5m, rate);
            Assert.True(service.TryGetCachedRate("TRY", "TRY", out var same));
            Assert.Equal(1m, same);
            Assert.Single(http.Requests);
        }
    }
}
