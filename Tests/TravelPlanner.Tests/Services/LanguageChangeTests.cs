using System.Globalization;
using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    /// <summary>AppResources.Culture is shared: tests that switch it run alone.</summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class CultureSensitive
    {
        public const string Name = "CultureSensitive";
    }

    /// <summary>TP-109: switching the app's language with trips open.</summary>
    [Collection(CultureSensitive.Name)]
    public class LanguageChangeTests
    {
        // the trip was saved in Turkish ("Almanya"); in English the geocoder says "Germany"
        private const string English = """
            {"results":[
              {"name":"Munich","latitude":39.0,"longitude":-80.0,"country":"United States","country_code":"US"},
              {"name":"Munich","latitude":48.14,"longitude":11.58,"country":"Germany","country_code":"DE"}
            ]}
            """;
        private const string Turkish = """
            {"results":[
              {"name":"Münih","latitude":39.0,"longitude":-80.0,"country":"Amerika Birleşik Devletleri","country_code":"US"},
              {"name":"Münih","latitude":48.14,"longitude":11.58,"country":"Almanya","country_code":"DE"}
            ]}
            """;
        private const string Current = """
            {"current":{"temperature_2m":12.0,"relative_humidity_2m":70,"wind_speed_10m":2.0,"weather_code":2}}
            """;

        private static async Task<T> InEnglish<T>(Func<Task<T>> action)
        {
            var before = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, AppResources.Culture);
            var english = new CultureInfo("en-US");
            CultureInfo.CurrentCulture = english;
            CultureInfo.CurrentUICulture = english;
            AppResources.Culture = english;
            try
            {
                return await action();
            }
            finally
            {
                (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture, AppResources.Culture) = before;
            }
        }

        [Fact]
        public async Task ATurkishTrip_IsFoundInEnglish_ByAskingInTurkishToo()
        {
            var http = new FakeHttpMessageHandler().Respond("language=en", English).Respond("language=tr", Turkish);
            var service = new GeocodingService(http.CreateClient(), Fakes.Connectivity(), new ResponseCache());

            var point = await InEnglish(() => service.GeocodeAsync("Münih", "Almanya"));

            Assert.Equal((48.14, 11.58), point);
            Assert.Equal(2, http.Requests.Count);
        }

        [Fact]
        public async Task ThePoint_IsTheSameInEveryLanguage_SoItIsAskedOnce()
        {
            var http = new FakeHttpMessageHandler().Respond("language=en", English).Respond("language=tr", Turkish);
            var cache = new ResponseCache();
            var service = new GeocodingService(http.CreateClient(), Fakes.Connectivity(), cache);

            var turkish = await service.GeocodeAsync("Münih", "Almanya");
            var english = await InEnglish(() => service.GeocodeAsync("Münih", "Almanya"));

            Assert.Equal(turkish, english);
            Assert.Single(http.Requests);
        }

        [Fact]
        public async Task TheWeatherCondition_FollowsTheLanguage_NotTheCache()
        {
            var http = new FakeHttpMessageHandler().Respond("geocoding", Turkish).Respond("api.open-meteo.com/v1/forecast", Current);
            var cache = new ResponseCache();
            var geocoding = new GeocodingService(http.CreateClient(), Fakes.Connectivity(), cache);
            var weather = new WeatherService(http.CreateClient(), geocoding, Fakes.Connectivity(), cache);

            var turkish = await weather.GetWeatherAsync("Münih", "Almanya");
            var english = await InEnglish(() => weather.GetWeatherAsync("Münih", "Almanya"));

            // the condition is text in the app's language: English asks again instead of reusing the Turkish answer
            // (the tests have no English strings, so the text itself can't be compared)
            Assert.Equal(AppResources.Weather_PartlyCloudy, turkish!.Condition);
            Assert.NotNull(english);
            Assert.Equal(2, http.Requests.Count(r => r.Host == "api.open-meteo.com"));
            Assert.Equal(1, http.Requests.Count(r => r.Host.StartsWith("geocoding"))); // the point is the same
        }

        [Fact]
        public void MatchCountry_TakesAnySpellingOrTheCode()
        {
            Assert.Equal("Almanya", GeocodingService.MatchCountry(new[]
            {
                new GeocodingService.GeocodingResult { Country = "Amerika", CountryCode = "US" },
                new GeocodingService.GeocodingResult { Country = "Almanya", CountryCode = "DE" },
            }, "almanya")?.Country);
            Assert.NotNull(GeocodingService.MatchCountry(new[] { new GeocodingService.GeocodingResult { Country = "Turkey", CountryCode = "TR" } }, "Türkiye"));
            Assert.NotNull(GeocodingService.MatchCountry(new[] { new GeocodingService.GeocodingResult { Country = "Japan", CountryCode = "JP" } }, "jp"));
        }
    }
}
