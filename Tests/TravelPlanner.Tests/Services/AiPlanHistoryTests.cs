using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-108: AI plans kept on the phone and opened again from "Önceki planlar".</summary>
    public class AiPlanHistoryTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private AiPlanHistory _history = null!;

        public async Task InitializeAsync()
        {
            _history = new AiPlanHistory(_db.Service);
            var conn = await _db.Connection;
            await conn.InsertAsync(Trip(1, "trip-1"));
            await conn.InsertAsync(Trip(2, "trip-2"));
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static Trip Trip(int id, string syncId) => new()
        {
            Id = id, SyncId = syncId, Name = "Roma", City = "Roma", Country = "İtalya",
            StartDate = new DateTime(2026, 10, 22), EndDate = new DateTime(2026, 10, 24),
        };

        internal static AiTripPlan Plan(string summary) => new()
        {
            Summary = summary,
            Days = new()
            {
                new AiPlanDay { Day = 1, Title = "Tarih", Activities = new() { new() { Start = "09:00", Title = "Kolezyum turu", PlaceKey = "p1" } } },
            },
            Places = new() { new AiPlanPlace { Key = "p1", Name = "Kolezyum", Category = "landmark" } },
            Todos = new() { "Biletleri al" },
        };

        private static readonly AiPlanAnswers Answers = new(new[] { "history", "food" }, "balanced", "mid", "couple");

        [Fact]
        public async Task Plans_AreKeptPerTrip_NewestFirst_OncePerJob()
        {
            await _history.SaveAsync(1, "job-a", new AiPlanResult(Plan("A"), 3, false, "req-a"), Answers, new DateTime(2026, 10, 1, 9, 0, 0));
            await _history.SaveAsync(1, "job-b", new AiPlanResult(Plan("B"), 2, false), null, new DateTime(2026, 10, 2, 9, 0, 0));
            await _history.SaveAsync(1, "job-a", new AiPlanResult(Plan("A again"), 3, false), Answers, DateTime.UtcNow); // same job
            await _history.SaveAsync(2, "job-c", new AiPlanResult(Plan("C"), 1, false), null, DateTime.UtcNow);

            var plans = await _history.ForTripAsync(1);

            Assert.Equal(new[] { "B", "A" }, plans.Select(p => p.Result.Plan.Summary));
            Assert.Equal("req-a", plans[1].Result.RequestId);
            Assert.Equal("Kolezyum turu", plans[1].Result.Plan.Days[0].Activities[0].Title);
            Assert.Equal(Answers.Interests, plans[1].Answers!.Interests);
            Assert.Null(plans[0].Answers);
            Assert.Single(await _history.ForTripAsync(2));
        }

        [Fact]
        public async Task OnlyTheNewestFive_AreKept()
        {
            for (var i = 0; i < AiPlanHistory.MaxPerTrip + 2; i++)
                await _history.SaveAsync(1, $"job-{i}", new AiPlanResult(Plan($"P{i}"), 1, false), null, new DateTime(2026, 10, 1).AddHours(i));

            var plans = await _history.ForTripAsync(1);

            Assert.Equal(AiPlanHistory.MaxPerTrip, plans.Count);
            Assert.Equal("P6", plans[0].Result.Plan.Summary);
            Assert.Equal("P2", plans[^1].Result.Plan.Summary);
        }

        [Fact]
        public async Task APlanOfAMissingTrip_IsNotSaved()
        {
            await _history.SaveAsync(99, "job-x", new AiPlanResult(Plan("X"), 1, false), null, DateTime.UtcNow);

            var conn = await _db.Connection;
            Assert.Equal(0, await conn.Table<AiPlanRecord>().CountAsync());
        }

        [Fact]
        public async Task PlansOfADeletedTrip_AreHidden_AndPurgedOnceItLeavesTheTrash()
        {
            await _history.SaveAsync(2, "job-c", new AiPlanResult(Plan("C"), 1, false), null, DateTime.UtcNow);
            var conn = await _db.Connection;
            await conn.DeleteAsync<Trip>(2);
            await conn.InsertAsync(new TrashItem { BatchId = "b", IsRoot = true, EntityType = "Trip", EntitySyncId = "trip-2", TripSyncId = "trip-2", Json = "{}" });

            Assert.Empty(await _history.ForTripAsync(2));
            Assert.Equal(0, await _history.PurgeOrphansAsync());   // still in the trash: it may come back

            await conn.DeleteAllAsync<TrashItem>();
            Assert.Equal(1, await _history.PurgeOrphansAsync());
            Assert.Equal(0, await conn.Table<AiPlanRecord>().CountAsync());
        }

        [Fact]
        public async Task MarkApplied_SetsTheBadge()
        {
            await _history.SaveAsync(1, "job-a", new AiPlanResult(Plan("A"), 3, false), null, DateTime.UtcNow);

            await _history.MarkAppliedAsync("job-a");

            Assert.True(Assert.Single(await _history.ForTripAsync(1)).IsApplied);
        }

        [Fact]
        public async Task ChangingTheAnswers_KeepsThePlan_AndItCanBeOpenedAndAddedAgain()
        {
            var planner = new Mock<IAiPlannerService>();
            planner.Setup(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new AiPlanStart(new AiPlanResult(Plan("Antik Roma"), 4, false, "req-1"), null, 4));
            planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(5, 5));
            planner.Setup(p => p.ApplyAsync(It.IsAny<Trip>(), It.IsAny<AiPlanSelection>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new AiApplyResult(1, 1, 1, 0));
            var notifications = new Mock<INotificationService>();
            notifications.Setup(n => n.ShowAiPlanAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            var jobs = new AiPlanJobs(planner.Object, new InMemoryPreferences(), notifications.Object, _history)
            {
                CurrentUser = () => "u1",
                SendDelay = TimeSpan.Zero,
            };
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(Trip(1, "trip-1"));
            var places = new Mock<IPlaceService>();
            places.Setup(p => p.GetByTripIdAsync(1)).ReturnsAsync(new List<Place>());
            var account = new Mock<IAccountService>();
            account.SetupGet(a => a.IsSignedIn).Returns(true);
            var vm = new AiPlannerViewModel(planner.Object, jobs, trips.Object, places.Object, account.Object,
                                            Fakes.Connectivity(online: true), Fakes.Dialogs().Object, history: _history)
            {
                TripId = 1,
                AfterApplied = _ => Task.CompletedTask,
                Dispatch = a => a(),
            };
            await vm.LoadCommand.ExecuteAsync(null);
            Assert.False(vm.ShowHistory);

            await vm.GenerateCommand.ExecuteAsync(null);
            for (var i = 0; i < 400 && !vm.IsPreview; i++)
                await Task.Delay(5);
            Assert.True(vm.IsPreview);
            Assert.False(vm.ShowHistory);

            await vm.StartOverCommand.ExecuteAsync(null);

            Assert.False(vm.IsPreview);
            Assert.True(vm.ShowHistory);
            var item = Assert.Single(vm.History);
            Assert.Contains(AppResources.AiPlan_Pace_Balanced, item.Subtitle);
            Assert.False(item.IsApplied);

            await vm.OpenHistoryCommand.ExecuteAsync(item);

            Assert.True(vm.IsPreview);
            Assert.Equal("Antik Roma", vm.Summary);
            Assert.Equal("Kolezyum turu", Assert.Single(Assert.Single(vm.Days).Activities).Title);

            await vm.AddSelectedCommand.ExecuteAsync(null);

            Assert.True(Assert.Single(await _history.ForTripAsync(1)).IsApplied);
            planner.Verify(p => p.StartPlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
