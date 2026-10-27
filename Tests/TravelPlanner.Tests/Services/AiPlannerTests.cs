using System.Text.Json.Nodes;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    internal sealed class FixedDevice : IDeviceIdentity
    {
        public const string Id = "abababababababababababababababababababababababababababababababab";
        public string Hash => Id;
    }

    public class DeviceIdentityTests
    {
        [Fact]
        public void Hash_IsStable_AndHidesTheRawId()
        {
            var preferences = new InMemoryPreferences();
            var first = new DeviceIdentity(preferences).Hash;
            var again = new DeviceIdentity(preferences).Hash; // e.g. after signing in with another account

            Assert.Matches("^[0-9a-f]{64}$", first);
            Assert.Equal(first, again);
            Assert.DoesNotContain(preferences.Get(DeviceIdentity.PreferenceKey, string.Empty), first);
        }
    }

    public class AiPlannerServiceTests : IAsyncLifetime
    {
        private const double RomeLat = 41.9028, RomeLon = 12.4964;

        private readonly TestDatabase _db = new();
        private readonly FakeSupabaseClient _cloud = new();
        private readonly Mock<IGeocodingService> _geo = new();
        private PlaceService _places = null!;
        private ActivityService _activities = null!;
        private TodoService _todos = null!;
        private AiPlannerService _service = null!;

        private static readonly Trip Rome = new()
        {
            Id = 1, Name = "Roma", City = "Roma", Country = "İtalya",
            StartDate = new DateTime(2026, 10, 22), EndDate = new DateTime(2026, 10, 24)
        };

        public Task InitializeAsync()
        {
            _places = new PlaceService(_db.Service);
            _activities = new ActivityService(_db.Service);
            _todos = new TodoService(_db.Service);
            _service = new AiPlannerService(_cloud, _geo.Object, _places, _activities, _todos, new FixedDevice());

            _geo.Setup(g => g.GeocodeAsync("Roma", "İtalya")).ReturnsAsync((RomeLat, RomeLon));
            _geo.Setup(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<PlaceSuggestion>());
            _geo.Setup(g => g.SearchPlacesAsync(It.Is<string>(q => q.StartsWith("Colosseo")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PlaceSuggestion>
                {
                    new() { Name = "Colosseum", Latitude = 37.7, Longitude = -97.3 },     // Colosseum in Kansas - too far
                    new() { Name = "Colosseo", Latitude = 41.8902, Longitude = 12.4922 },
                });
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static AiPlanRequest Request() => new("tr", "Roma, İtalya", new DateTime(2026, 10, 22), 3,
            new[] { "history", "food" }, "balanced", "mid", "couple", "Vejetaryeniz", new[] { "Pantheon" });

        [Fact]
        public async Task CreatePlan_SendsTheAnswers_AndReadsThePlan()
        {
            _cloud.Handler = (fn, _) => fn == "fn:plan-trip"
                ? """
                  {"plan":{"summary":"Antik Roma","days":[{"day":1,"title":"Tarih","activities":[{"start":"09:00","end":"11:00","title":"Kolezyum turu","note":null,"placeKey":"p1"}]}],
                           "places":[{"key":"p1","name":"Kolezyum","localName":"Colosseo","area":"Monti","category":"landmark","description":"Antik arena","lat":41.8902,"lon":12.4922,"visitMinutes":120}],
                           "todos":["Biletleri al"],"tips":["Su al"]},
                   "remaining":4,"truncatedDays":false}
                  """
                : "{}";

            var result = (await _service.StartPlanAsync(Request(), null)).Result!;

            var sent = _cloud.Calls.Single(c => c.Function == "fn:plan-trip").Arguments;
            Assert.Equal("2026-10-22", sent["startDate"]!.GetValue<string>());
            Assert.Equal(3, sent["days"]!.GetValue<int>());
            Assert.Equal("history", sent["interests"]![0]!.GetValue<string>());
            Assert.Equal("Pantheon", sent["existingPlaces"]![0]!.GetValue<string>());
            Assert.Equal(FixedDevice.Id, sent["deviceId"]!.GetValue<string>());

            Assert.Equal(4, result.Remaining);
            var activity = result.Plan.Days[0].Activities[0];
            Assert.Equal(("Kolezyum turu", "09:00", "11:00", "p1"), (activity.Title, activity.Start, activity.End, activity.PlaceKey));
            var place = result.Plan.Places[0];
            Assert.Equal(("p1", "Kolezyum", "Colosseo", "Monti", "landmark"), (place.Key, place.Name, place.LocalName, place.Area, place.Category));
            Assert.Equal((41.8902, 12.4922, 120), (place.Lat!.Value, place.Lon!.Value, place.VisitMinutes!.Value));
            Assert.Equal("Biletleri al", Assert.Single(result.Plan.Todos));
        }

        [Fact]
        public async Task CreatePlan_ReadsTheRequestId_AndTheSaveTimeIsReported()
        {
            _cloud.Handler = (fn, _) => fn == "fn:plan-trip"
                ? """{"plan":{"summary":"x","days":[],"places":[],"todos":[],"tips":[]},"remaining":1,"truncatedDays":false,"requestId":"req-9"}"""
                : "true";

            var result = (await _service.StartPlanAsync(Request(), null)).Result!;
            await _service.ReportSavedAsync(result.RequestId!, TimeSpan.FromSeconds(42.4));

            Assert.Equal("req-9", result.RequestId);
            var sent = _cloud.Calls.Single(c => c.Function == "ai_plan_saved").Arguments;
            Assert.Equal("req-9", sent["p_request_id"]!.GetValue<string>());
            Assert.Equal(42, sent["p_seconds"]!.GetValue<int>());
        }

        [Fact]
        public async Task ReportSaved_NeverThrows()
        {
            _cloud.Handler = (_, _) => throw new SyncException(SyncErrorKind.Network, "offline");
            await _service.ReportSavedAsync("req-1", TimeSpan.FromSeconds(5));
            await _service.ReportSavedAsync("", TimeSpan.FromSeconds(5));
            Assert.DoesNotContain(_cloud.Calls, c => c.Function == "ai_plan_saved" && c.Arguments["p_request_id"]!.GetValue<string>() == "");
        }

        [Fact]
        public async Task StartPlan_InTheBackground_SendsTheJobId_AndReadsTheJob()
        {
            _cloud.Handler = (fn, _) => fn == "fn:plan-trip" ? """{"jobId":"j-1","status":"running","remaining":3}""" : "{}";

            var start = await _service.StartPlanAsync(Request(), "j-1");

            var sent = _cloud.Calls.Single(c => c.Function == "fn:plan-trip").Arguments;
            Assert.True(sent["background"]!.GetValue<bool>());
            Assert.Equal("j-1", sent["jobId"]!.GetValue<string>());
            Assert.Equal(new AiPlanStart(null, "j-1", 3), start);
        }

        [Fact]
        public async Task StartPlan_AnOlderServerSendsThePlanItself()
        {
            _cloud.Handler = (_, _) => """{"plan":{"summary":"x","days":[],"places":[],"todos":[],"tips":[]},"remaining":2}""";

            var start = await _service.StartPlanAsync(Request(), "j-1");

            Assert.Null(start.JobId);
            Assert.Equal(2, start.Result!.Remaining);
        }

        [Fact]
        public async Task Job_RunningDoneFailedOrMissing()
        {
            var answer = "null";
            _cloud.Handler = (fn, args) => fn == "ai_plan_job" && args["p_id"]!.GetValue<string>() == "j-1" ? answer : "null";

            answer = """{"status":"running"}""";
            Assert.Equal(AiPlanJobStatus.Running, (await _service.GetJobAsync("j-1")).Status);

            answer = """{"status":"done","remaining":4,"result":{"plan":{"summary":"Roma","days":[],"places":[],"todos":[],"tips":[]},"remaining":4,"truncatedDays":true,"requestId":"r-1"}}""";
            var done = await _service.GetJobAsync("j-1");
            Assert.Equal((AiPlanJobStatus.Done, "Roma", true, "r-1", 4), (done.Status, done.Result!.Plan.Summary, done.Result.TruncatedDays, done.Result.RequestId, done.Remaining));

            answer = """{"status":"failed","error":"quota_exceeded","remaining":0}""";
            Assert.Equal(new AiPlanJobState(AiPlanJobStatus.Failed, Error: AiPlanErrorKind.QuotaExceeded, Remaining: 0), await _service.GetJobAsync("j-1"));

            answer = """{"status":"failed","error":"timeout"}""";
            Assert.Equal(AiPlanErrorKind.Failed, (await _service.GetJobAsync("j-1")).Error);

            answer = "null";
            Assert.Equal(AiPlanJobStatus.Missing, (await _service.GetJobAsync("j-1")).Status);
        }

        [Fact]
        public async Task Job_WithoutMigration39_IsMissing_AndOfflineThrows()
        {
            _cloud.Handler = (_, _) => throw new SyncException(SyncErrorKind.Rejected, "no function", status: 404);
            Assert.Equal(AiPlanJobStatus.Missing, (await _service.GetJobAsync("j-1")).Status);
            await _service.ClearJobAsync("j-1"); // never throws

            _cloud.Handler = (_, _) => throw new SyncException(SyncErrorKind.Network, "offline");
            var ex = await Assert.ThrowsAsync<AiPlanException>(() => _service.GetJobAsync("j-1"));
            Assert.Equal(AiPlanErrorKind.Offline, ex.Kind);
        }

        [Fact]
        public async Task CreatePlan_SendsTheCityCentre_SoTheServerSearchesTheRightSpot()
        {
            _cloud.Handler = (_, _) => """{"plan":{"summary":"","days":[],"places":[],"todos":[],"tips":[]},"remaining":4}""";

            await _service.StartPlanAsync(Request() with { City = "Roma", Country = "İtalya" }, null);

            var center = _cloud.Calls.Single(c => c.Function == "fn:plan-trip").Arguments["center"]!;
            Assert.Equal(RomeLat, center["lat"]!.GetValue<double>(), 4);
            Assert.Equal(RomeLon, center["lon"]!.GetValue<double>(), 4);
        }

        [Fact]
        public async Task Apply_UsesTheServersMapPosition_WithoutSearchingAgain()
        {
            var verified = new AiPlanPlace { Key = "p1", Name = "Van Kalesi", Category = "historic", Lat = 38.503, Lon = 43.342, Verified = true };

            var result = await _service.ApplyAsync(Rome, Selection(new[] { verified }, new[] { "p1" },
                (1, new AiPlanActivity { Start = "10:00", Title = "Van Kalesi'ne çıkış", PlaceKey = "p1" })));

            Assert.Equal(new AiApplyResult(1, 1, 0, 0), result);
            var place = Assert.Single(await _places.GetByTripIdAsync(1));
            Assert.Equal((38.503, 43.342), (place.Latitude, place.Longitude));
            Assert.Equal(place.Id, Assert.Single(await _activities.GetByTripIdAsync(1)).PlaceId);
            _geo.Verify(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
            _geo.Verify(g => g.GeocodeAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        }

        [Theory]
        [InlineData(SyncErrorKind.Network, null, null, AiPlanErrorKind.Offline)]
        [InlineData(SyncErrorKind.NotSignedIn, 401, null, AiPlanErrorKind.NotSignedIn)]
        [InlineData(SyncErrorKind.Server, 429, "quota_exceeded", AiPlanErrorKind.QuotaExceeded)]
        [InlineData(SyncErrorKind.Rejected, 403, "account_disabled", AiPlanErrorKind.AccountDisabled)]
        [InlineData(SyncErrorKind.Server, 502, "ai_not_configured", AiPlanErrorKind.NotConfigured)]
        [InlineData(SyncErrorKind.Rejected, 404, null, AiPlanErrorKind.NotConfigured)]
        [InlineData(SyncErrorKind.Server, 502, "ai_failed", AiPlanErrorKind.Failed)]
        [InlineData(SyncErrorKind.Server, 503, "ai_paused", AiPlanErrorKind.Paused)]
        [InlineData(SyncErrorKind.Server, 503, null, AiPlanErrorKind.Paused)]
        [InlineData(SyncErrorKind.Rejected, 409, "plan_running", AiPlanErrorKind.Busy)]
        public async Task CreatePlan_ErrorsAreExplained(SyncErrorKind kind, int? status, string? code, AiPlanErrorKind expected)
        {
            _cloud.Handler = (_, _) => throw new SyncException(kind, "x", status: status, serverCode: code);

            var ex = await Assert.ThrowsAsync<AiPlanException>(() => _service.StartPlanAsync(Request(), null));

            Assert.Equal(expected, ex.Kind);
        }

        [Fact]
        public async Task Quota_IsRead_AndOfflineGivesNull()
        {
            _cloud.Handler = (fn, _) => fn == "ai_plan_quota_status" ? """{"limit":5,"remaining":3}""" : "{}";
            Assert.Equal(new AiQuota(3, 5), await _service.GetQuotaAsync());
            Assert.Equal(FixedDevice.Id, _cloud.Calls.Last().Arguments["p_device"]!.GetValue<string>());

            _cloud.Handler = (_, _) => throw new SyncException(SyncErrorKind.Network, "offline");
            Assert.Null(await _service.GetQuotaAsync());
        }

        [Fact]
        public async Task Quota_ReadsThePeriod()
        {
            _cloud.Handler = (_, _) => """{"limit":5,"remaining":2,"paused":false,"period":"weekly","resets_at":"2026-11-02T00:00:00+03:00","max_days":7}""";
            Assert.Equal(new AiQuota(2, 5, false, null, "weekly", new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(3)), 7),
                         await _service.GetQuotaAsync());

            _cloud.Handler = (_, _) => """{"limit":5,"remaining":2,"period":"never","resets_at":null,"max_days":40}""";
            var quota = await _service.GetQuotaAsync();
            Assert.Equal(new AiQuota(2, 5, false, null, "never"), quota);
            Assert.True(quota!.NeverResets);
        }

        [Fact]
        public async Task Quota_SaysWhenAiIsPaused()
        {
            _cloud.Handler = (_, _) => """{"limit":5,"remaining":0,"paused":true,"message":" Bakımdayız "}""";
            Assert.Equal(new AiQuota(0, 5, true, "Bakımdayız"), await _service.GetQuotaAsync());

            _cloud.Handler = (_, _) => """{"limit":5,"remaining":0,"paused":true,"message":null}""";
            Assert.Equal(new AiQuota(0, 5, true), await _service.GetQuotaAsync());
        }

        private static AiPlanPlace Colosseum() => new()
        {
            Key = "p1", Name = "Kolezyum", LocalName = "Colosseo", Area = "Monti", Category = "landmark",
            Description = "Antik arena", Lat = 41.8901, Lon = 12.4921, VisitMinutes = 120
        };

        private static AiPlanSelection Selection(IReadOnlyList<AiPlanPlace> places, IEnumerable<string> keys,
                                              params (int, AiPlanActivity)[] activities) =>
            new(places, activities, keys.ToList(), Array.Empty<string>());

        [Fact]
        public async Task Apply_AddsPlacesNearTheCity_LinksActivitiesByKey_AndSkipsDuplicates()
        {
            await _todos.SaveAsync(new TodoItem { TripId = 1, Title = "Biletleri al" });
            var places = new List<AiPlanPlace>
            {
                Colosseum(),
                new() { Key = "p2", Name = "Gizli Bahçe", Description = "Sakin bir bahçe", Category = "park" },
                new() { Key = "p3", Name = "Seçilmeyen Lokanta", Category = "restaurant" },
            };
            var activities = new List<(int, AiPlanActivity)>
            {
                (1, new AiPlanActivity { Start = "9:30", Title = "Kolezyum turu", Note = "Erken git", PlaceKey = "p1" }),
                (1, new AiPlanActivity { Start = "12:00", Title = "kolezyum  turu", PlaceKey = "p1" }),           // same title, same day
                (2, new AiPlanActivity { Start = "10:00", End = "11:15", Title = "Gizli bahçe", PlaceKey = "p2" }),
                (3, new AiPlanActivity { Start = "25:00", Title = "Akşam yemeği", PlaceKey = "p3" }),
                (9, new AiPlanActivity { Title = "Gezinin dışında" }),
            };

            var result = await _service.ApplyAsync(Rome,
                new AiPlanSelection(places, activities, new[] { "p1", "p2" }, new[] { "biletleri al", "Müze kartı al" }));

            Assert.Equal(new AiApplyResult(Activities: 3, Places: 1, Todos: 1, UnlocatedPlaces: 1), result);

            var saved = await _places.GetByTripIdAsync(1);
            var colosseum = saved.Single(p => p.Name == "Kolezyum");
            Assert.Equal(41.8902, colosseum.Latitude, 4);                      // the geocoder's Colosseo, not the one in Kansas
            Assert.Contains(AppResources.AiPlan_Cat_Landmark, colosseum.Description);
            Assert.Contains("Monti", colosseum.Description);
            Assert.Contains("Colosseo", colosseum.Description);
            Assert.Contains("Antik arena", colosseum.Description);
            Assert.DoesNotContain(saved, p => p.Name == "Gizli Bahçe");        // not on the map: no pin rather than a wrong one
            Assert.DoesNotContain(saved, p => p.Name == "Seçilmeyen Lokanta"); // unticked

            var plan = await _activities.GetByTripIdAsync(1);
            var tour = Assert.Single(plan, a => a.Date == new DateTime(2026, 10, 22));
            Assert.Equal(colosseum.Id, tour.PlaceId);
            Assert.Equal(new TimeSpan(9, 30, 0), tour.StartTime);
            Assert.Equal(new TimeSpan(11, 30, 0), tour.EndTime);               // start + the visit's 120 minutes
            var walk = plan.Single(a => a.Title == "Gizli bahçe");
            Assert.Null(walk.PlaceId);                                         // the activity stays, its title names the place
            Assert.Equal(new TimeSpan(11, 15, 0), walk.EndTime);
            var dinner = plan.Single(a => a.Title == "Akşam yemeği");
            Assert.Null(dinner.StartTime);                                     // 25:00 isn't a time
            Assert.Null(dinner.PlaceId);
            Assert.Contains("Seçilmeyen Lokanta", dinner.Note);                // unpinned place kept by name
            Assert.DoesNotContain(plan, a => a.Title == "Gezinin dışında");    // day 9 of a 3-day trip

            Assert.Equal(new[] { "Biletleri al", "Müze kartı al" }, (await _todos.GetByTripIdAsync(1)).Select(t => t.Title).OrderBy(t => t, StringComparer.Ordinal));

            // the same plan again adds nothing twice
            var again = await _service.ApplyAsync(Rome, new AiPlanSelection(places, activities, new[] { "p1", "p2" }, new[] { "Müze kartı al" }));
            Assert.Equal(new AiApplyResult(0, 0, 0, UnlocatedPlaces: 1), again);
        }

        [Fact]
        public async Task Apply_PinsOnlyNamedMatches_NeverTheAiGuessOrTheCityCentre()
        {
            var hidden = new AiPlanPlace { Key = "a", Name = "Küçük Kilise", Lat = 41.95, Lon = 12.45 };        // the map knows nothing
            var town = new AiPlanPlace { Key = "b", Name = "Tivoli Şehir Merkezi", Lat = 41.7, Lon = 12.3 };   // the guess is wrong
            var other = new AiPlanPlace { Key = "c", Name = "Tiyatro", LocalName = "Teatro X", Lat = 41.9, Lon = 12.47 };
            _geo.Setup(g => g.SearchPlacesAsync(It.Is<string>(q => q.StartsWith("Tivoli")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PlaceSuggestion>
                {
                    new() { Name = "Villa d'Este Tivoli", Latitude = 41.9627, Longitude = 12.7960 },
                    new() { Name = "Tivoli", Latitude = 41.9634, Longitude = 12.7981 },
                });
            _geo.Setup(g => g.SearchPlacesAsync(It.Is<string>(q => q.StartsWith("Teatro X")), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PlaceSuggestion> { new() { Name = "Sala Grande", Latitude = 41.905, Longitude = 12.475 } }); // right there, another name

            var result = await _service.ApplyAsync(Rome, Selection(new[] { hidden, town, other }, new[] { "a", "b", "c" }));

            Assert.Equal((1, 2), (result.Places, result.UnlocatedPlaces));
            var tivoli = Assert.Single(await _places.GetByTripIdAsync(1));
            Assert.Equal((41.9634, 12.7981), (tivoli.Latitude, tivoli.Longitude)); // the town itself, by its exact name
        }

        [Theory]
        [InlineData("Erciş Şehri Merkezi", "Erciş")]
        [InlineData("Van Gölü Kıyı Şeridi", "Van Gölü")]
        [InlineData("Kaleiçi", "Kaleiçi")]
        public void DescriptiveWords_AreDropped(string name, string expected)
        {
            Assert.Equal(expected, AiPlannerService.WithoutGenericWords(name));
        }

        [Fact]
        public async Task Apply_LinksToPlacesTheTripAlreadyHas_EvenWhenNotTicked()
        {
            await _places.SaveAsync(new Place { TripId = 1, Name = "Colosseo", Latitude = 41.89, Longitude = 12.49 });

            var result = await _service.ApplyAsync(Rome,
                Selection(new[] { Colosseum() }, Array.Empty<string>(), (1, new AiPlanActivity { Title = "Tur", PlaceKey = "p1" })));

            Assert.Equal(0, result.Places);
            var place = Assert.Single(await _places.GetByTripIdAsync(1));
            var activity = Assert.Single(await _activities.GetByTripIdAsync(1));
            Assert.Equal(place.Id, activity.PlaceId);
            Assert.Null(activity.Note);
            _geo.Verify(g => g.SearchPlacesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Theory]
        [InlineData("09:00", "10:30", null, "09:00", "10:30")]
        [InlineData("09:00", null, 45, "09:00", "09:45")]
        [InlineData("09:00", "08:00", null, "09:00", null)]   // end before start
        [InlineData("23:30", null, 90, "23:30", null)]        // would pass midnight
        [InlineData(null, "10:00", 60, null, null)]           // no start, no end
        public void Times_GetAnEnd(string? start, string? end, int? visitMinutes, string? expectedStart, string? expectedEnd)
        {
            var (s, e) = AiPlannerService.TimesOf(new AiPlanActivity { Start = start, End = end },
                                                  new AiPlanPlace { VisitMinutes = visitMinutes });
            Assert.Equal(expectedStart, s?.ToString(@"hh\:mm"));
            Assert.Equal(expectedEnd, e?.ToString(@"hh\:mm"));
        }

        [Theory]
        [InlineData("Colosseo", "colosseo", true)]
        [InlineData("Galata Kulesi", "Galata Kulesi Müzesi", true)]
        [InlineData("Café de Flore", "CAFE DE FLORE", true)]
        [InlineData("Roma", "Romantik Otel", false)]           // a word, not a piece of one
        [InlineData("Bar", "Bar Centrale", false)]             // too short to trust
        public void Names_Match(string a, string b, bool same)
        {
            Assert.Equal(same, AiPlannerService.SameName(a, b));
        }

        [Theory]
        [InlineData("09:00", 9, 0)]
        [InlineData("7:05", 7, 5)]
        public void Times_AreParsed(string text, int hours, int minutes)
        {
            Assert.Equal(new TimeSpan(hours, minutes, 0), AiPlannerService.ParseTime(text));
        }

        [Fact]
        public void Distance_IsRoughlyRight()
        {
            // Rome → Florence is about 230 km
            Assert.InRange(AiPlannerService.DistanceKm(41.9028, 12.4964, 43.7696, 11.2558), 220, 240);
        }
    }

    public class AiPlannerViewModelTests
    {
        private readonly Mock<IAiPlannerService> _planner = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IPlaceService> _places = new();
        private readonly Mock<IAccountService> _account = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();
        private readonly Mock<INotificationService> _notifications = new();
        private readonly InMemoryPreferences _preferences = new();
        private readonly AiPlanJobs _jobs;

        private static readonly Trip Rome = new()
        {
            Id = 1, Name = "Roma", City = "Roma", Country = "İtalya",
            StartDate = new DateTime(2026, 10, 22), EndDate = new DateTime(2026, 10, 24)
        };

        static AiPlannerViewModelTests() => AiPlanJobs.PollInterval = TimeSpan.FromMilliseconds(5);

        public AiPlannerViewModelTests()
        {
            _jobs = NewJobs();
            _notifications.Setup(n => n.ShowAiPlanAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            _trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(Rome);
            _places.Setup(p => p.GetByTripIdAsync(1)).ReturnsAsync(new List<Place> { new() { Name = "Pantheon" } });
            _account.SetupGet(a => a.IsSignedIn).Returns(true);
            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(5, 5));
        }

        private async Task<AiPlannerViewModel> LoadAsync(int tripId = 1)
        {
            var vm = new AiPlannerViewModel(_planner.Object, _jobs, _trips.Object, _places.Object, _account.Object,
                                            Fakes.Connectivity(online: true), _dialogs.Object)
            {
                TripId = tripId,
                AfterApplied = _ => Task.CompletedTask,
                Dispatch = a => a(),
            };
            await vm.LoadCommand.ExecuteAsync(null);
            return vm;
        }

        /// <summary>The background plans of one account, saved in <see cref="_preferences"/> (a restart is a new one).</summary>
        private AiPlanJobs NewJobs() => new(_planner.Object, _preferences, _notifications.Object) { CurrentUser = () => "u1" };

        private static AiPlanStart Started(AiPlanResult result) => new(result, null, result.Remaining);

        private static async Task Until(Func<bool> condition)
        {
            for (var i = 0; i < 400 && !condition(); i++)
                await Task.Delay(5);
            Assert.True(condition());
        }

        [Fact]
        public async Task PausedAi_ShowsTheMessage_AndCannotGenerate()
        {
            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(0, 5, true, "Bakımdayız"));
            var vm = await LoadAsync();
            Assert.False(vm.CanGenerate);
            Assert.Equal("Bakımdayız", vm.QuotaText);
            Assert.True(vm.IsBlocked);
            Assert.False(vm.IsAsking);
            Assert.Equal((AppResources.AiPlan_PausedTitle, "Bakımdayız"), (vm.BlockedTitle, vm.BlockedText));

            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(0, 5, true));
            vm = await LoadAsync();
            Assert.Equal(AppResources.AiPlan_Paused, vm.QuotaText);
            Assert.Equal(AppResources.AiPlan_PausedText, vm.BlockedText);
        }

        [Fact]
        public async Task QuotaUsedUp_ShowsTheBlockedCard_InsteadOfTheQuestions()
        {
            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(0, 5));
            var vm = await LoadAsync();

            Assert.True(vm.IsBlocked);
            Assert.False(vm.IsAsking);
            Assert.False(vm.IsPaused);
            Assert.Equal((AppResources.AiPlan_QuotaNoneTitle, AppResources.AiPlan_QuotaNoneText), (vm.BlockedTitle, vm.BlockedText));

            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(2, 5));
            vm = await LoadAsync();
            Assert.False(vm.IsBlocked);
            Assert.True(vm.IsAsking);
        }

        [Fact]
        public async Task Quota_SaysWhenThePlansComeBack()
        {
            var next = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.FromHours(3));
            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(0, 5, false, null, "weekly", next, 2));
            var vm = await LoadAsync();
            var when = AiPlannerViewModel.ResetText(next);
            Assert.Equal(string.Format(AppResources.AiPlan_QuotaRuleFormat, when), vm.QuotaRuleText);
            Assert.Equal(string.Format(AppResources.AiPlan_QuotaNoneFormat, when), vm.BlockedText);
            Assert.Equal(string.Format(AppResources.AiPlan_IntroFormat, Rome.City, 2), vm.IntroText);

            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(0, 5, false, null, "never"));
            vm = await LoadAsync();
            Assert.Equal(AppResources.AiPlan_QuotaRuleNever, vm.QuotaRuleText);
            Assert.Equal(AppResources.AiPlan_QuotaNoneNever, vm.BlockedText);

            _planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(3, 5)); // an older server
            vm = await LoadAsync();
            Assert.Equal(AppResources.AiPlan_QuotaRule, vm.QuotaRuleText);
        }

        [Fact]
        public async Task PausedWhileAsking_SaysSoOnce()
        {
            var vm = await LoadAsync();
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new AiPlanException(AiPlanErrorKind.Paused));

            await vm.GenerateCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorText);
            Assert.Equal(AppResources.AiPlan_Paused, vm.QuotaText);
            Assert.False(vm.CanGenerate);
        }

        private static AiTripPlan SamplePlan() => new()
        {
            Summary = "Antik Roma",
            Days = new()
            {
                new AiPlanDay { Day = 1, Title = "Tarih", Activities = new()
                {
                    new() { Start = "09:00", End = "11:00", Title = "Kolezyum turu", PlaceKey = "p1" },
                    new() { Title = "Gelato", PlaceKey = "p2" },
                } },
                new AiPlanDay { Day = 2, Title = "Boş gün", Activities = new() },
            },
            Places = new()
            {
                new AiPlanPlace { Key = "p1", Name = "Kolezyum", Area = "Monti", Description = "Antik arena", Category = "landmark" },
                new AiPlanPlace { Key = "p2", Name = "Pantheon", Description = "Tapınak", Category = "historic" }, // the trip has it
            },
            Todos = new() { "Biletleri al" },
            Tips = new() { "Su al" },
        };

        [Fact]
        public async Task Answers_SingleChoiceGroups_AndAtMostFourInterests()
        {
            var vm = await LoadAsync();

            vm.ToggleChoiceCommand.Execute(vm.Paces.Single(p => p.Key == "packed"));
            Assert.Equal(new[] { "packed" }, vm.Paces.Where(p => p.IsSelected).Select(p => p.Key));

            foreach (var key in new[] { "nature", "beach", "art" })
                vm.ToggleChoiceCommand.Execute(vm.Interests.Single(i => i.Key == key));
            Assert.Equal(AiPlannerViewModel.MaxInterests, vm.Interests.Count(i => i.IsSelected)); // history, food, nature, beach

            vm.Notes = "  Vejetaryeniz ";
            var request = vm.BuildRequest(Rome, new[] { "Pantheon" });
            Assert.Equal("Roma, İtalya", request.Destination);
            Assert.Equal(3, request.Days);
            Assert.Equal("packed", request.Pace);
            Assert.Equal(new[] { "history", "food", "nature", "beach" }, request.Interests);
            Assert.Equal("Vejetaryeniz", request.Notes);
            Assert.Equal("tr", request.Language);
        }

        [Fact]
        public async Task Generate_ShowsThePreview_WithEverythingTicked()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Started(new AiPlanResult(SamplePlan(), 4, TruncatedDays: false)));
            var vm = await LoadAsync();

            await vm.GenerateCommand.ExecuteAsync(null);

            Assert.True(vm.IsPreview);
            Assert.False(vm.IsAsking);
            var day = Assert.Single(vm.Days); // the empty day is left out
            Assert.Equal(2, day.Activities.Count);
            Assert.All(day.Activities, a => Assert.True(a.IsSelected));
            Assert.Equal(("09:00", "11:00"), (day.Activities[0].TimeText, day.Activities[0].EndText));
            Assert.Equal("Pantheon", day.Activities[1].Detail);              // the place's name, found by its key
            var place = Assert.Single(vm.Places);                            // Pantheon is already in the trip
            Assert.Equal($"{AppResources.AiPlan_Cat_Landmark} · Monti — Antik arena", place.Description);
            Assert.True(vm.HasPlaces && vm.HasTodos && vm.HasTips);
            _planner.Verify(p => p.StartPlanAsync(It.Is<AiPlanRequest>(r => r.ExistingPlaces.Contains("Pantheon")), It.IsAny<string?>(), It.IsAny<CancellationToken>()));

            // one flat, virtualized list: section, day, its activities, then places, to-dos and tips
            Assert.Equal(new[]
            {
                typeof(AiPreviewSection), typeof(AiPreviewDay), typeof(AiPreviewActivity), typeof(AiPreviewActivity),
                typeof(AiPreviewSection), typeof(AiPreviewPlace),
                typeof(AiPreviewSection), typeof(AiPreviewTodo),
                typeof(AiPreviewSection), typeof(AiPreviewTip),
            }, vm.PreviewRows.Select(r => r.GetType()));
            Assert.Same(day.Activities[0], vm.PreviewRows[2]); // the same rows the add button reads
            Assert.True(vm.IsPreview && !vm.IsFormVisible);
        }

        [Fact]
        public async Task Generate_WaitingScreen_SaysWhatIsGoingOn()
        {
            var answer = new TaskCompletionSource<AiPlanStart>();
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).Returns(answer.Task);
            var vm = await LoadAsync();
            var shown = new List<string>();
            var steps = new List<string>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AiPlannerViewModel.GeneratingStatus)) shown.Add(vm.GeneratingStatus);
                if (e.PropertyName == nameof(AiPlannerViewModel.GeneratingStepText)) steps.Add(vm.GeneratingStepText);
            };
            var interval = AiPlannerViewModel.StepInterval;
            AiPlannerViewModel.StepInterval = TimeSpan.FromMilliseconds(10);
            try
            {
                var generating = vm.GenerateCommand.ExecuteAsync(null);
                for (var i = 0; i < 100 && shown.Count < 3; i++)
                    await Task.Delay(10);
                Assert.True(vm.IsGenerating);
                await generating; // the page doesn't wait for the plan (TP-57)
                Assert.True(vm.IsGenerating);
                answer.SetResult(Started(new AiPlanResult(SamplePlan(), 4, false)));
                await Until(() => vm.IsPreview);
            }
            finally
            {
                AiPlannerViewModel.StepInterval = interval;
            }

            Assert.True(shown.Count >= 3);
            Assert.Equal(AiPlannerViewModel.GeneratingSteps.Take(shown.Count), shown.Take(AiPlannerViewModel.GeneratingSteps.Count));
            Assert.Equal("Adım 1/6", steps[0]); // TP-77
            Assert.Equal("Adım 2/6", steps[1]);
            Assert.DoesNotMatch(@"\d", AppResources.AiPlan_Generating); // the rough duration is a line of its own (TP-45)
            Assert.False(vm.IsGenerating);
        }

        [Fact]
        public async Task LeavingWhileGenerating_ThePlanGoesOn_AndComingBackShowsIt()
        {
            string? jobId = null;
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .Callback<AiPlanRequest, string?, CancellationToken>((_, id, _) => jobId = id)
                    .ReturnsAsync(new AiPlanStart(null, "server-id-ignored", 4));
            var ready = false;
            _planner.Setup(p => p.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => ready
                        ? new AiPlanJobState(AiPlanJobStatus.Done, new AiPlanResult(SamplePlan(), 4, false, "req-1"))
                        : new AiPlanJobState(AiPlanJobStatus.Running));
            var vm = await LoadAsync();

            await vm.GenerateCommand.ExecuteAsync(null);
            Assert.True(vm.IsGenerating);
            Assert.NotNull(jobId);                                           // the phone's id goes to the server
            vm.Leave();                                                      // no "leave?" question any more
            _dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);

            ready = true;
            await Until(() => _jobs.Current?.Phase == AiPlanJobPhase.Ready);
            _notifications.Verify(n => n.ShowAiPlanAsync(AppResources.AiPlan_ReadyTitle, string.Format(AppResources.AiPlan_ReadyFormat, "Roma")));
            _planner.Verify(p => p.GetJobAsync(jobId!, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
            _planner.Verify(p => p.ClearJobAsync(jobId!, It.IsAny<CancellationToken>()));   // the phone keeps it now
            Assert.False(vm.IsPreview);                                      // the closed page isn't touched

            var back = await LoadAsync();
            Assert.True(back.IsPreview);
            Assert.Single(back.Days);
            Assert.True(_jobs.Current!.Offered);
        }

        [Fact]
        public async Task PlanReadyOnItsOwnPage_NoNotification()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanStart(null, "x", 4));
            _planner.Setup(p => p.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanJobState(AiPlanJobStatus.Done, new AiPlanResult(SamplePlan(), 3, false)));
            var vm = await LoadAsync();

            await vm.GenerateCommand.ExecuteAsync(null);
            await Until(() => vm.IsPreview);

            Assert.False(vm.IsGenerating);
            _notifications.Verify(n => n.ShowAiPlanAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task OnlyOnePlanAtATime_AlsoForAnotherTrip()
        {
            _trips.Setup(t => t.GetByIdAsync(2)).ReturnsAsync(new Trip
            {
                Id = 2, Name = "Paris", City = "Paris", StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 3)
            });
            _places.Setup(p => p.GetByTripIdAsync(2)).ReturnsAsync(new List<Place>());
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .Returns(new TaskCompletionSource<AiPlanStart>().Task);   // still being made
            var rome = await LoadAsync();
            await rome.GenerateCommand.ExecuteAsync(null);
            Assert.True(rome.IsGenerating);

            var paris = await LoadAsync(2);
            Assert.True(paris.IsBusyElsewhere);
            Assert.False(paris.CanGenerate);
            Assert.Equal(AppResources.AiPlan_Busy, paris.ErrorText);
            await paris.GenerateCommand.ExecuteAsync(null);
            _planner.Verify(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AppClosedWhileGenerating_NextStartPicksThePlanUp()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .Returns(new TaskCompletionSource<AiPlanStart>().Task);   // the app is closed before the answer
            var vm = await LoadAsync();
            await vm.GenerateCommand.ExecuteAsync(null);
            var id = _jobs.Current!.Id;

            // a new process: only what was saved on the phone is left
            _planner.Setup(p => p.GetJobAsync(id, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanJobState(AiPlanJobStatus.Done, new AiPlanResult(SamplePlan(), 2, false)));
            var restarted = NewJobs();
            Assert.True(restarted.IsRunning);
            await restarted.ResumeAsync();

            Assert.Equal(AiPlanJobPhase.Ready, restarted.Current!.Phase);
            Assert.False(restarted.Current.Offered);                         // the app asks "Göster?"
            var again = NewJobs();                                           // and it survives one more restart
            Assert.Equal("Kolezyum turu", again.For(1)!.Result!.Plan.Days[0].Activities[0].Title);
            Assert.Null(again.For(2));
        }

        [Fact]
        public async Task PlanThatFailedInTheBackground_SaysWhy_AndTheQuestionsAreBack()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanStart(null, "x", 4));
            _planner.Setup(p => p.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanJobState(AiPlanJobStatus.Failed, Error: AiPlanErrorKind.Failed, Remaining: 5));
            var vm = await LoadAsync();

            await vm.GenerateCommand.ExecuteAsync(null);
            await Until(() => !vm.IsGenerating);

            Assert.Equal(AppResources.AiPlan_ErrorFailed, vm.ErrorText);
            Assert.Equal(5, vm.Remaining);                                   // given back by the server
            Assert.Null(_jobs.Current);                                      // told once
            Assert.True(vm.CanGenerate);
        }

        [Fact]
        public async Task JobTheServerDoesNotKnow_Fails()
        {
            _planner.Setup(p => p.GetJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiPlanJobState(AiPlanJobStatus.Missing));
            _preferences.Set(AiPlanJobs.StoreKey, """{"id":"j1","userId":"u1","tripId":1,"tripName":"Roma","phase":0}""");
            var jobs = NewJobs();

            await jobs.ResumeAsync();

            Assert.Equal((AiPlanJobPhase.Failed, AiPlanErrorKind.Failed), (jobs.Current!.Phase, jobs.Current.Error));
        }

        [Fact]
        public void AnotherAccount_DoesNotSeeThePlan()
        {
            _preferences.Set(AiPlanJobs.StoreKey, """{"id":"j1","userId":"u2","tripId":1,"tripName":"Roma","phase":0}""");
            var jobs = NewJobs();
            Assert.Null(jobs.Current);
            Assert.False(jobs.IsRunning);
        }

        [Fact]
        public async Task Generate_WhenTheDailyLimitIsReached_SaysSo()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new AiPlanException(AiPlanErrorKind.QuotaExceeded));
            var vm = await LoadAsync();

            await vm.GenerateCommand.ExecuteAsync(null);

            Assert.Null(vm.ErrorText); // the blocked card says it
            Assert.False(vm.IsPreview);
            Assert.False(vm.CanGenerate);
            Assert.True(vm.IsBlocked);
            Assert.False(vm.IsAsking);
            Assert.Equal(AppResources.AiPlan_QuotaNoneTitle, vm.BlockedTitle);
        }

        [Fact]
        public async Task Guests_CannotGenerate()
        {
            _account.SetupGet(a => a.IsSignedIn).Returns(false);
            var vm = await LoadAsync();

            Assert.True(vm.IsGuest);
            Assert.False(vm.CanGenerate);
            await vm.GenerateCommand.ExecuteAsync(null);
            _planner.Verify(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task AddSelected_PassesOnlyTheTickedItems()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Started(new AiPlanResult(SamplePlan(), 4, false)));
            AiPlanSelection? sent = null;
            _planner.Setup(p => p.ApplyAsync(It.IsAny<Trip>(), It.IsAny<AiPlanSelection>(), It.IsAny<CancellationToken>()))
                    .Callback<Trip, AiPlanSelection, CancellationToken>((_, selection, _) => sent = selection)
                    .ReturnsAsync(new AiApplyResult(1, 0, 1, 0));
            var vm = await LoadAsync();
            await vm.GenerateCommand.ExecuteAsync(null);

            vm.Days[0].Activities[1].IsSelected = false; // no gelato
            vm.Places[0].IsSelected = false;
            await vm.AddSelectedCommand.ExecuteAsync(null);

            Assert.Equal("Kolezyum turu", Assert.Single(sent!.Activities).Activity.Title);
            Assert.Empty(sent.PlaceKeys);
            Assert.Equal(2, sent.PlanPlaces.Count);                          // to link activities to places the trip has
            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.Is<string>(m => m.Contains("1"))));
        }

        [Fact]
        public async Task AddSelected_ReportsHowLongAfterThePlanItWasSaved_Once()
        {
            _planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Started(new AiPlanResult(SamplePlan(), 4, false, "req-7")));
            _planner.Setup(p => p.ApplyAsync(It.IsAny<Trip>(), It.IsAny<AiPlanSelection>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new AiApplyResult(1, 0, 1, 0));
            var vm = await LoadAsync();
            var clock = new StepClock();
            vm.Clock = clock;
            await vm.GenerateCommand.ExecuteAsync(null);

            clock.Advance(TimeSpan.FromSeconds(30));
            await vm.AddSelectedCommand.ExecuteAsync(null);
            await vm.AddSelectedCommand.ExecuteAsync(null);

            _planner.Verify(p => p.ReportSavedAsync("req-7", TimeSpan.FromSeconds(30), It.IsAny<CancellationToken>()), Times.Once);
        }

        /// <summary>A clock that only moves when told to.</summary>
        private sealed class StepClock : TimeProvider
        {
            private long _ticks;
            public void Advance(TimeSpan by) => _ticks += by.Ticks;
            public override long GetTimestamp() => _ticks;
            public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        }
    }
}
