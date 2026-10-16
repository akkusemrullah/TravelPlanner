using Moq;
using Xunit;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class GeocodingServiceTests
    {
        private const string OpenMeteoJson = """
            {"results":[
              {"name":"Paris","latitude":33.66,"longitude":-95.55,"country":"Amerika Birleşik Devletleri","country_code":"US","admin1":"Texas"},
              {"name":"Paris","latitude":48.85,"longitude":2.35,"country":"Fransa","country_code":"FR","admin1":"Île-de-France"}
            ]}
            """;

        private const string PhotonJson = """
            {"features":[
              {"geometry":{"coordinates":[32.8369,39.9251]},"properties":{"name":"Anıtkabir","city":"Ankara","country":"Türkiye","osm_key":"historic","osm_value":"memorial"}},
              {"geometry":{"coordinates":[32.85,39.93]},"properties":{"postcode":"06570"}},
              {"geometry":{"coordinates":[32.86,39.94]},"properties":{"name":"Bir Kafe","osm_key":"amenity","osm_value":"cafe"}}
            ]}
            """;

        private readonly FakeHttpMessageHandler _http = new FakeHttpMessageHandler()
            .Respond("geocoding-api.open-meteo.com", OpenMeteoJson)
            .Respond("photon.komoot.io", PhotonJson);

        private GeocodingService CreateService(bool online = true) =>
            new(_http.CreateClient(), Fakes.Connectivity(online), new ResponseCache());

        [Fact]
        public async Task Geocode_PrefersTheRequestedCountry()
        {
            var point = await CreateService().GeocodeAsync("Paris", "Fransa");

            Assert.Equal((48.85, 2.35), point);
        }

        [Fact]
        public async Task Geocode_MatchesCountryCodeToo_AndCachesTheResult()
        {
            var service = CreateService();

            var first = await service.GeocodeAsync("Paris", "FR");
            var second = await service.GeocodeAsync("Paris", "FR");

            Assert.Equal((48.85, 2.35), first);
            Assert.Equal(first, second);
            Assert.Single(_http.Requests);
        }

        [Fact]
        public async Task Geocode_FallsBackToFirstResult_WhenCountryDoesNotMatch()
        {
            var point = await CreateService().GeocodeAsync("Paris", "Japonya");

            Assert.Equal((33.66, -95.55), point);
        }

        [Fact]
        public async Task Geocode_ReturnsNull_WhenOffline()
        {
            Assert.Null(await CreateService(online: false).GeocodeAsync("Paris"));
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task SearchCities_FiltersToCountry_AndBuildsRegionLabel()
        {
            var cities = await CreateService().SearchCitiesAsync("Par", "Fransa");

            var city = Assert.Single(cities);
            Assert.Equal("Île-de-France, Fransa", city.DisplayRegion);
        }

        [Fact]
        public async Task SearchCities_IgnoresQueriesShorterThanTwoCharacters()
        {
            Assert.Empty(await CreateService().SearchCitiesAsync("P"));
            Assert.Empty(_http.Requests);
        }

        [Fact]
        public async Task SearchPlaces_ReadsGeoJsonLonLatOrder_AndSkipsNamelessFeatures()
        {
            var places = await CreateService().SearchPlacesAsync("Anıtkabir");

            Assert.Equal(2, places.Count);
            var anitkabir = places[0];
            Assert.Equal(39.9251, anitkabir.Latitude);
            Assert.Equal(32.8369, anitkabir.Longitude);
            Assert.Equal("Anıt · Ankara, Türkiye", anitkabir.DisplayRegion);
            Assert.Equal("Tesis", places[1].CategoryLabel); // falls back from osm_value to osm_key
        }

        [Fact]
        public async Task SearchPlaces_SecondIdenticalQuery_IsServedFromCache()
        {
            var service = CreateService();

            await service.SearchPlacesAsync("Anıtkabir");
            await service.SearchPlacesAsync("anıtkabir");

            Assert.Single(_http.Requests);
        }
    }
}
