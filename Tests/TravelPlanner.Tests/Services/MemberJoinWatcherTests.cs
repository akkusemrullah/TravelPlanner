using Moq;
using Xunit;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Tests
{
    /// <summary>TP-4: the owner hears when someone joins one of their trips.</summary>
    [Collection("SignedInUser")]
    public class MemberJoinWatcherTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 10, 17, 15, 0, 0, DateTimeKind.Utc);

        private readonly Mock<ISharingService> _sharing = new();
        private readonly InMemoryPreferences _preferences = new();
        private DateTime _clock = Now;

        public MemberJoinWatcherTests()
        {
            ChangeTracker.CurrentUserId = SharingFakes.Me;
        }

        public void Dispose() => ChangeTracker.CurrentUserId = null;

        private MemberJoinWatcher Create() => new(_sharing.Object, _preferences) { UtcNow = () => _clock };

        private static MemberJoin Join(string trip, string? tripName, string? name, DateTime at) =>
            new(trip, tripName, Guid.NewGuid().ToString(), name, at);

        private void Answer(params MemberJoin[] joins) =>
            _sharing.Setup(s => s.GetRecentJoinsAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>())).ReturnsAsync(joins);

        [Fact]
        public async Task TheFirstCheck_OnlySetsTheStartingPoint()
        {
            Answer(Join("t1", "Roma", "Ayşe", Now.AddHours(-3)));
            var watcher = Create();

            Assert.Empty(await watcher.CheckAsync());

            Assert.Equal(Now.AddHours(-3).Ticks, _preferences.Get(MemberJoinWatcher.SeenKeyPrefix + SharingFakes.Me, 0L));
            _sharing.Verify(s => s.GetRecentJoinsAsync(null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task NewJoins_AreReported_Once()
        {
            var seen = Now.AddHours(-1);
            _preferences.Set(MemberJoinWatcher.SeenKeyPrefix + SharingFakes.Me, seen.Ticks);
            var ayse = Join("t1", "Roma", "Ayşe", Now.AddMinutes(-5));
            Answer(ayse);
            var watcher = Create();

            var joins = await watcher.CheckAsync();

            Assert.Equal(ayse, Assert.Single(joins));
            _sharing.Verify(s => s.GetRecentJoinsAsync(seen, It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(ayse.JoinedAtUtc.Ticks, _preferences.Get(MemberJoinWatcher.SeenKeyPrefix + SharingFakes.Me, 0L));

            // right after another sync: the server isn't asked again
            Assert.Empty(await watcher.CheckAsync());
            _sharing.Verify(s => s.GetRecentJoinsAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);

            // two minutes later it is - from the newest join on
            _clock = Now.Add(MemberJoinWatcher.MinInterval);
            Answer();
            Assert.Empty(await watcher.CheckAsync());
            _sharing.Verify(s => s.GetRecentJoinsAsync(ayse.JoinedAtUtc, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task AGuest_OrAFailure_ReportsNothing()
        {
            ChangeTracker.CurrentUserId = null;
            Assert.Empty(await Create().CheckAsync());
            _sharing.Verify(s => s.GetRecentJoinsAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);

            ChangeTracker.CurrentUserId = SharingFakes.Me;
            _preferences.Set(MemberJoinWatcher.SeenKeyPrefix + SharingFakes.Me, Now.AddHours(-1).Ticks);
            _sharing.Setup(s => s.GetRecentJoinsAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new SharingException(SharingErrorKind.Offline));
            var watcher = Create();
            Assert.Empty(await watcher.CheckAsync());
            Assert.Empty(await watcher.CheckAsync()); // not asked again right away
            _sharing.Verify(s => s.GetRecentJoinsAsync(It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
            Assert.Equal(Now.AddHours(-1).Ticks, _preferences.Get(MemberJoinWatcher.SeenKeyPrefix + SharingFakes.Me, 0L));
        }

        [Fact]
        public void TheMessage_NamesThePersonAndTheTrip()
        {
            var one = MemberJoinWatcher.BuildMessage(new[] { Join("t1", "Roma", "Ayşe", Now) });
            Assert.Equal(AppResources.Members_JoinedTitle, one.Title);
            Assert.StartsWith("Ayşe, \"Roma\" seyahatine katıldı.", one.Message);
            Assert.EndsWith(AppResources.Members_JoinedHint, one.Message);

            var noName = MemberJoinWatcher.BuildMessage(new[] { Join("t1", "Roma", null, Now) });
            Assert.StartsWith(AppResources.Members_Someone + ",", noName.Message);

            var sameTrip = MemberJoinWatcher.BuildMessage(new[] { Join("t1", "Roma", "Ayşe", Now), Join("t1", "Roma", "Can", Now) });
            Assert.StartsWith("\"Roma\" seyahatine 2 kişi katıldı.", sameTrip.Message);

            var trips = MemberJoinWatcher.BuildMessage(new[] { Join("t1", "Roma", "Ayşe", Now), Join("t2", "Paris", "Can", Now), Join("t2", "Paris", "Deniz", Now) });
            Assert.StartsWith("Seyahatlerine 3 yeni üye katıldı.", trips.Message);
        }
    }
}
