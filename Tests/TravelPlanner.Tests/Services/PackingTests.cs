using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class PackingSuggestionEngineTests
    {
        private static Trip TripTo(string country, int days) => new()
        {
            Name = "Gezi", City = "X", Country = country,
            StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 1).AddDays(days - 1)
        };

        private static DailyForecast Day(double max, double min, int rainChance = 0, int code = 1) =>
            new() { TemperatureMax = max, TemperatureMin = min, PrecipitationProbability = rainChance, WeatherCode = code };

        private static List<string> Titles(IEnumerable<PackingSuggestion> s) => s.Select(x => x.Title).ToList();

        [Fact]
        public void RainyColdForecast_SuggestsUmbrellaAndCoat()
        {
            var forecast = new[] { Day(9, 3, rainChance: 70), Day(11, 5) };

            var titles = Titles(PackingSuggestionEngine.Suggest(TripTo("Almanya", 2), forecast, Array.Empty<string>()));

            Assert.Contains(AppResources.Packing_Umbrella, titles);
            Assert.Contains(AppResources.Packing_Coat, titles);
            Assert.DoesNotContain(AppResources.Packing_Sunscreen, titles);
            Assert.DoesNotContain(AppResources.Packing_GlovesHat, titles); // not below 2 °C
        }

        [Fact]
        public void HotForecast_SuggestsSunGearAndSwimwear()
        {
            var titles = Titles(PackingSuggestionEngine.Suggest(TripTo("Türkiye", 4), new[] { Day(31, 22) }, Array.Empty<string>()));

            Assert.Contains(AppResources.Packing_Sunscreen, titles);
            Assert.Contains(AppResources.Packing_Swimwear, titles);
            Assert.DoesNotContain(AppResources.Packing_Coat, titles);
        }

        [Fact]
        public void FreezingSnowyForecast_SuggestsWinterGear()
        {
            var titles = Titles(PackingSuggestionEngine.Suggest(TripTo("Avusturya", 5), new[] { Day(1, -6, code: 73) }, Array.Empty<string>()));

            Assert.Contains(AppResources.Packing_GlovesHat, titles);
            Assert.Contains(AppResources.Packing_ThermalWear, titles);
            Assert.Contains(AppResources.Packing_SnowBoots, titles);
        }

        [Fact]
        public void Abroad_AddsPassport_AndAdapterOnlyWherePlugsDiffer()
        {
            var uk = Titles(PackingSuggestionEngine.Suggest(TripTo("Birleşik Krallık", 3), null, Array.Empty<string>()));
            var italy = Titles(PackingSuggestionEngine.Suggest(TripTo("İtalya", 3), null, Array.Empty<string>()));
            var home = Titles(PackingSuggestionEngine.Suggest(TripTo("Türkiye", 3), null, Array.Empty<string>()));

            Assert.Contains(AppResources.Packing_Passport, uk);
            Assert.Contains(AppResources.Packing_Adapter, uk);
            Assert.Contains(AppResources.Packing_Passport, italy);
            Assert.DoesNotContain(AppResources.Packing_Adapter, italy);
            Assert.DoesNotContain(AppResources.Packing_Passport, home);
            Assert.Contains(AppResources.Packing_IdCard, home);
        }

        [Fact]
        public void TripLength_DrivesClothesAndLaundry()
        {
            var shortTrip = Titles(PackingSuggestionEngine.Suggest(TripTo("Türkiye", 2), null, Array.Empty<string>()));
            var longTrip = Titles(PackingSuggestionEngine.Suggest(TripTo("Türkiye", 10), null, Array.Empty<string>()));

            Assert.Contains("2 günlük kıyafet", shortTrip);
            Assert.DoesNotContain(AppResources.Packing_LaundryBag, shortTrip);
            Assert.Contains("7 günlük kıyafet", longTrip); // capped at a week - the laundry bag covers the rest
            Assert.Contains(AppResources.Packing_LaundryBag, longTrip);
        }

        [Fact]
        public void ItemsAlreadyOnTheList_AreNotSuggestedAgain_CaseInsensitive()
        {
            var existing = new[] { AppResources.Packing_Charger.ToUpper(new System.Globalization.CultureInfo("tr-TR")), "  şemsiye " };

            var titles = Titles(PackingSuggestionEngine.Suggest(TripTo("Türkiye", 3), new[] { Day(15, 9, rainChance: 90) }, existing));

            Assert.DoesNotContain(AppResources.Packing_Charger, titles);
            Assert.DoesNotContain(AppResources.Packing_Umbrella, titles);
            Assert.Contains(AppResources.Packing_RainJacket, titles);
        }

        [Fact]
        public void SuggestionsAreSelectedByDefault_AndCarryAReason()
        {
            var suggestions = PackingSuggestionEngine.Suggest(TripTo("Türkiye", 3), null, Array.Empty<string>());

            Assert.All(suggestions, s => Assert.True(s.IsSelected));
            Assert.All(suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s.Reason)));
        }
    }

    public class PackingContextRuleTests
    {
        private static Trip Trip(string country, int days = 4, int month = 11, string? currency = null) => new()
        {
            Name = "Gezi", City = "X", Country = country, CurrencyCode = currency,
            StartDate = new DateTime(2026, month, 3), EndDate = new DateTime(2026, month, 3).AddDays(days - 1)
        };

        private static List<PackingSuggestion> Suggest(Trip trip, IReadOnlyList<DailyForecast>? forecast = null,
                                                       IEnumerable<string>? existing = null, IEnumerable<PackingPlanEntry>? plan = null) =>
            PackingSuggestionEngine.Suggest(trip, forecast, existing ?? Array.Empty<string>(), plan).ToList();

        private static List<string> Titles(IEnumerable<PackingSuggestion> s) => s.Select(x => x.Title).ToList();

        [Fact]
        public void Domestic_NeedsOnlyTheIdCard_NoAbroadItems()
        {
            var titles = Titles(Suggest(Trip("Türkiye")));

            Assert.Contains(AppResources.Packing_IdCard, titles);
            foreach (var abroadOnly in new[] { AppResources.Packing_Passport, AppResources.Packing_SchengenVisa, AppResources.Packing_VisaCheck,
                                               AppResources.Packing_TravelInsurance, AppResources.Packing_ExitFee, AppResources.Packing_Roaming,
                                               AppResources.Packing_CardAbroad, AppResources.Packing_Adapter })
                Assert.DoesNotContain(abroadOnly, titles);
        }

        [Theory]
        [InlineData("Türkiye", "Ankara")]
        [InlineData("TÜRKİYE", "Ankara")]
        [InlineData("turkiye", "Ankara")]
        [InlineData("Türkiye Cumhuriyeti", "Ankara")]
        [InlineData("Republic of Türkiye", "Ankara")]
        [InlineData("Turkey (Türkiye)", "İzmir")]
        [InlineData("Türkei", "Antalya")]
        [InlineData("TR", "Bursa")]
        [InlineData("Anadolu", "Ankara")]   // unrecognised country text, but Ankara is a Turkish province
        public void AnyWayOfWritingTürkiye_IsDomestic(string country, string city)
        {
            var trip = Trip(country);
            trip.City = city;

            var titles = Titles(Suggest(trip));

            Assert.DoesNotContain(AppResources.Packing_Passport, titles);
            Assert.DoesNotContain(AppResources.Packing_ExitFee, titles);
            Assert.Contains(AppResources.Packing_IdCard, titles);
        }

        [Theory]
        [InlineData("Türkmenistan", "Aşkabat")]
        [InlineData("Almanya", "Batman")]   // a known foreign country wins over a look-alike city name
        public void LookAlikes_StayAbroad(string country, string city)
        {
            var trip = Trip(country);
            trip.City = city;

            Assert.Contains(AppResources.Packing_Passport, Titles(Suggest(trip)));
        }

        [Fact]
        public void NoCountry_IsTreatedAsDomestic()
        {
            Assert.DoesNotContain(AppResources.Packing_Passport, Titles(Suggest(Trip(""))));
        }

        [Theory]
        [InlineData("KKTC")]
        [InlineData("Kuzey Kıbrıs Türk Cumhuriyeti")]
        [InlineData("gürcistan")]
        public void IdCardCountries_NeedNoPassportOrVisa_ButStillCountAsAbroad(string country)
        {
            var suggestions = Suggest(Trip(country));
            var titles = Titles(suggestions);

            Assert.DoesNotContain(AppResources.Packing_Passport, titles);
            Assert.DoesNotContain(AppResources.Packing_SchengenVisa, titles);
            Assert.DoesNotContain(AppResources.Packing_VisaCheck, titles);
            Assert.Contains(suggestions, s => s.Title == AppResources.Packing_IdCard && s.Reason.Contains(country));
            Assert.Contains(AppResources.Packing_ExitFee, titles);
        }

        [Fact]
        public void Schengen_GetsTheVisa_OtherCountries_AVisaCheck()
        {
            var germany = Titles(Suggest(Trip("almanya")));   // any spelling/case
            var japan = Titles(Suggest(Trip("Japonya")));

            Assert.Contains(AppResources.Packing_Passport, germany);
            Assert.Contains(AppResources.Packing_SchengenVisa, germany);
            Assert.DoesNotContain(AppResources.Packing_VisaCheck, germany);
            Assert.Contains(AppResources.Packing_VisaCheck, japan);
            Assert.DoesNotContain(AppResources.Packing_SchengenVisa, japan);
            Assert.DoesNotContain(AppResources.Packing_IdCard, japan);
        }

        [Fact]
        public void Cash_IsSuggestedInTheTripsCurrency_OnlyAbroad_AndNotForLira()
        {
            Assert.Contains("Biraz nakit EUR", Titles(Suggest(Trip("İtalya", currency: "EUR"))));
            Assert.DoesNotContain(Titles(Suggest(Trip("İtalya", currency: "TRY"))), t => t.StartsWith("Biraz nakit"));
            Assert.DoesNotContain(Titles(Suggest(Trip("Türkiye", currency: "EUR"))), t => t.StartsWith("Biraz nakit"));
        }

        [Fact]
        public void DayTrip_HasNoClothesOrPyjamas_AndToiletriesOnlyAsAMaybe()
        {
            var suggestions = Suggest(Trip("Türkiye", days: 1));
            var titles = Titles(suggestions);

            Assert.DoesNotContain(titles, t => t.Contains("kıyafet"));
            Assert.DoesNotContain(AppResources.Packing_Pajamas, titles);
            Assert.DoesNotContain(AppResources.Packing_PowerBank, titles);
            Assert.False(suggestions.Single(s => s.Title == AppResources.Packing_Toiletries).IsSelected);
        }

        [Fact]
        public void OvernightTrip_GetsUnderwearForEachNight_PlusOne()
        {
            var titles = Titles(Suggest(Trip("Türkiye", days: 3)));

            Assert.Contains(string.Format(AppResources.Packing_UnderwearFormat, 4), titles);
            Assert.Contains(AppResources.Packing_Pajamas, titles);
        }

        [Fact]
        public void HeatAlone_OffersSwimwearUnticked_ABeachInThePlanTicksIt()
        {
            var hot = new[] { new DailyForecast { TemperatureMax = 34, TemperatureMin = 20 } };
            var beach = new[] { new PackingPlanEntry("Plaj", "Plaj") };

            var heatOnly = Suggest(Trip("Türkiye", month: 7), hot).Single(s => s.Title == AppResources.Packing_Swimwear);
            var withBeach = Suggest(Trip("Türkiye", month: 7), hot, plan: beach).Single(s => s.Title == AppResources.Packing_Swimwear);

            Assert.False(heatOnly.IsSelected);
            Assert.True(withBeach.IsSelected);
        }

        [Fact]
        public void ItemsAlreadyOnTheList_AreRecognised_EvenWordedDifferently()
        {
            var existing = new[] { "Pasaport ve vize", "5 günlük kıyafet", "mayo" };
            var hot = new[] { new DailyForecast { TemperatureMax = 34, TemperatureMin = 20 } };

            var titles = Titles(Suggest(Trip("İtalya", days: 4, month: 7), hot, existing));

            Assert.DoesNotContain(AppResources.Packing_Passport, titles);
            Assert.DoesNotContain(titles, t => t.EndsWith("günlük kıyafet"));
            Assert.DoesNotContain(AppResources.Packing_Swimwear, titles);
            Assert.Contains(AppResources.Packing_SchengenVisa, titles); // "vize" alone doesn't cover the Schengen visa
        }

        [Fact]
        public void WithoutAForecast_TheMonthDecides_UpsideDownInTheSouth()
        {
            var januaryHome = Titles(Suggest(Trip("Türkiye", month: 1)));
            var januaryAustralia = Titles(Suggest(Trip("Avustralya", month: 1)));

            Assert.Contains(AppResources.Packing_Coat, januaryHome);
            Assert.DoesNotContain(AppResources.Packing_Sunscreen, januaryHome);
            Assert.Contains(AppResources.Packing_Sunscreen, januaryAustralia);
            Assert.DoesNotContain(AppResources.Packing_Coat, januaryAustralia);
        }
    }

    public class PackingPlanTests
    {
        private static Trip TripTo(string city, int days = 3) => new()
        {
            Name = "Gezi", City = city, Country = "Türkiye",
            StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 1).AddDays(days - 1)
        };

        private static List<PackingSuggestion> Suggest(Trip trip, params PackingPlanEntry[] plan) =>
            PackingSuggestionEngine.Suggest(trip, null, Array.Empty<string>(), plan).ToList();

        private static PackingPlanEntry Entry(string text) => new(text, text);

        [Fact]
        public void BeachInThePlan_SuggestsSwimwearAndTowel_QuotingThePlan()
        {
            var suggestions = Suggest(TripTo("Kaş"), Entry("Kaputaş Plajı'nda yüzme"));

            var towel = Assert.Single(suggestions, s => s.Title == AppResources.Packing_BeachTowel);
            Assert.Contains(suggestions, s => s.Title == AppResources.Packing_Swimwear);
            Assert.Contains("Kaputaş", towel.Reason);
        }

        [Theory]
        [InlineData("DAG YURUYUSU", "Packing_HikingShoes")]
        [InlineData("Belgrad Ormanı'nda piknik", "Packing_HikingShoes")]
        [InlineData("Uludağ'da kayak", "Packing_SkiGoggles")]
        [InlineData("Müşteri toplantısı", "Packing_FormalWear")]
        [InlineData("İş gezisi", "Packing_Laptop")]
        [InlineData("Sultanahmet Camii", "Packing_Scarf")]
        [InlineData("Gulet ile mavi tur", "Packing_SeasicknessPills")]
        [InlineData("Kapadokya'da çadır kampı", "Packing_SleepingBag")]
        [InlineData("Çemberlitaş Hamamı", "Packing_FlipFlops")]
        [InlineData("Arkadaşın düğünü", "Packing_SmartOutfit")]
        [InlineData("Sabah 06:30 uçuş", "Packing_NeckPillow")]
        public void PlanWords_BringTheirItems(string planText, string expectedResource)
        {
            var expected = AppResources.ResourceManager.GetString(expectedResource, AppResources.Culture)!;

            Assert.Contains(Suggest(TripTo("İstanbul"), Entry(planText)), s => s.Title == expected);
        }

        [Theory]
        [InlineData("Denizli", "Packing_BeachTowel")]          // a city, not the sea
        [InlineData("Galata Kulesi", "Packing_SmartOutfit")]  // not a gala
        [InlineData("Kampüs gezisi", "Packing_SleepingBag")]  // not camping
        [InlineData("This is fine", "Packing_FormalWear")]    // English "is" is not "iş"
        [InlineData("Doğal taş atölyesi", "Packing_HikingShoes")]
        public void LookAlikeWords_DoNotMatch(string planText, string unexpectedResource)
        {
            var unexpected = AppResources.ResourceManager.GetString(unexpectedResource, AppResources.Culture)!;

            Assert.DoesNotContain(Suggest(TripTo("Ankara"), Entry(planText)), s => s.Title == unexpected);
        }

        [Fact]
        public void TripDetails_CountAsPlanToo()
        {
            var trip = TripTo("Erzurum");
            trip.Name = "Palandöken kayak tatili";
            var plan = new[] { new PackingPlanEntry(trip.Name, $"{trip.Name} {trip.City}") };

            Assert.Contains(PackingSuggestionEngine.Suggest(trip, null, Array.Empty<string>(), plan), s => s.Title == AppResources.Packing_SkiWear);
        }

        [Fact]
        public void PlanItemsAlreadyOnTheList_AreNotSuggestedAgain_AndNoPlanMeansNoPlanItems()
        {
            var existing = new[] { AppResources.Packing_Swimwear };

            var withList = PackingSuggestionEngine.Suggest(TripTo("Bodrum"), null, existing, new[] { Entry("Plaj günü") });
            var withoutPlan = PackingSuggestionEngine.Suggest(TripTo("Bodrum"), null, Array.Empty<string>());

            Assert.DoesNotContain(withList, s => s.Title == AppResources.Packing_Swimwear);
            Assert.Contains(withList, s => s.Title == AppResources.Packing_BeachTowel);
            Assert.DoesNotContain(withoutPlan, s => s.Title == AppResources.Packing_BeachTowel);
        }

        [Fact]
        public void LongPlanTexts_AreShortenedInTheReason()
        {
            var label = "Plaj " + new string('x', 80);

            var towel = Suggest(TripTo("Bodrum"), new PackingPlanEntry(label, label)).Single(s => s.Title == AppResources.Packing_BeachTowel);

            Assert.True(towel.Reason.Length < label.Length);
            Assert.Contains("…", towel.Reason);
        }

        [Fact]
        public void Fold_IgnoresCaseTurkishLettersAndPunctuation()
        {
            Assert.Equal("dag yuruyusu  istanbul", PackingPlanMatcher.Fold("Dağ Yürüyüşü, İSTANBUL"));
        }
    }

    public class PackingTemplateTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private PackingService _service = null!;

        public Task InitializeAsync()
        {
            _service = new PackingService(_db.Service);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task SaveTemplate_CleansItems_AndReplacesSameName()
        {
            await _service.SaveTemplateAsync("Kış", new[] { "Mont", " mont ", "", "Bere" });
            var saved = await _service.SaveTemplateAsync("kış", new[] { "Mont", "Eldiven" });

            var all = await _service.GetTemplatesAsync();
            var template = Assert.Single(all);
            Assert.Equal(saved.Id, template.Id);
            Assert.Equal(new[] { "Mont", "Eldiven" }, template.ItemList);
            Assert.Equal("Kış (2)", template.Display);
        }

        [Fact]
        public async Task DeleteTemplate_RemovesIt()
        {
            var template = await _service.SaveTemplateAsync("İş", new[] { "Laptop" });

            await _service.DeleteTemplateAsync(template);

            Assert.Empty(await _service.GetTemplatesAsync());
        }

        [Fact]
        public async Task GetPlan_ReadsTheTrip_ItsActivities_AndItsPlaces()
        {
            var db = await _db.Service.Connection;
            var trip = new Trip { Id = 7, Name = "Muğla", City = "Bodrum", Description = "Deniz ve kum" };
            await db.InsertAsync(new TripActivity { TripId = 7, Title = "Tekne turu", Note = "Öğle yemeği dahil", Date = new DateTime(2026, 8, 2) });
            await db.InsertAsync(new TripActivity { TripId = 8, Title = "Başka seyahat", Date = new DateTime(2026, 8, 2) });
            await db.InsertAsync(new Place { TripId = 7, Name = "Camel Plajı", Description = "Şezlong var" });

            var plan = await _service.GetPlanAsync(trip);

            Assert.Equal(new[] { "Muğla", "Tekne turu", "Camel Plajı" }, plan.Select(p => p.Label));
            Assert.Contains("Deniz", plan[0].Text);
            Assert.Contains("Öğle", plan[1].Text);
        }

        [Fact]
        public async Task EmptyName_IsRejected()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveTemplateAsync("  ", new[] { "x" }));
        }
    }
}
