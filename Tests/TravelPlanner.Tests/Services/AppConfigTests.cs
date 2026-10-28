using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>Settings from the admin panel (migration 29: public.app_config).</summary>
    [Collection("Currency")] // the defaults change the static MoneyFormatter.CurrencyCode
    public class AppConfigTests : IDisposable
    {
        private const string Answer = """
            {"ai_enabled":false,"ai_paused_message":"Bakımdayız","announcement":" Yeni Keşfet planları! ",
             "min_app_version":"1.2","default_currency":"EUR","default_reminder_hour":20}
            """;

        private readonly FakeSupabaseClient _cloud = new();
        private readonly InMemoryPreferences _preferences = new();
        private readonly SettingsService _settings;
        private DateTime _now = new(2026, 10, 20, 10, 0, 0, DateTimeKind.Utc);

        public AppConfigTests() => _settings = new SettingsService(_preferences);

        public void Dispose() => MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;

        private AppConfigService NewService() => new(_cloud, _preferences, _settings, () => _now);

        [Theory]
        [InlineData("1.0.0", "1.2", true)]
        [InlineData("1.2", "1.2.0", false)]
        [InlineData("1.10.0", "1.9", false)]
        [InlineData("1.9.9", "1.10", true)]
        [InlineData("2.0", null, false)]
        [InlineData("1.0", "bozuk", false)]
        public void Versions_AreComparedNumerically(string current, string? minimum, bool older) =>
            Assert.Equal(older, AppConfig.IsOlder(current, minimum));

        [Fact]
        public async Task Refresh_ReadsTheSettings_AndKeepsThemForTheNextStart()
        {
            _cloud.Handler = (fn, _) => fn == "app_config" ? Answer : "{}";

            var config = await NewService().RefreshAsync();

            Assert.Equal(new AppConfig(false, "Bakımdayız", "Yeni Keşfet planları!", "1.2", "EUR", 20), config);
            Assert.Equal(1, _cloud.PublicCallCount); // guests read it too
            Assert.Equal(config, NewService().Current);
        }

        [Fact]
        public async Task Defaults_ApplyUntilTheUserPicksTheirOwn()
        {
            _cloud.Handler = (_, _) => Answer;
            await NewService().RefreshAsync();

            Assert.Equal("EUR", _settings.CurrencyCode);
            Assert.Equal("EUR", MoneyFormatter.CurrencyCode);
            Assert.Equal(20, _settings.ReminderHour);

            _settings.CurrencyCode = "USD";
            _settings.ReminderHour = 8;
            await NewService().RefreshAsync();
            Assert.Equal("USD", _settings.CurrencyCode);
            Assert.Equal(8, _settings.ReminderHour);
        }

        [Fact]
        public async Task Refresh_WaitsAFewMinutes_AndAFailureKeepsTheLastSettings()
        {
            _cloud.Handler = (_, _) => Answer;
            var service = NewService();
            await service.RefreshAsync();
            await service.RefreshAsync();
            Assert.Equal(1, _cloud.PublicCallCount);

            _now += AppConfigService.MinInterval;
            _cloud.Handler = (_, _) => throw new SyncException(SyncErrorKind.Network, "offline");
            var config = await service.RefreshAsync();
            Assert.Equal(2, _cloud.PublicCallCount);
            Assert.Equal("Bakımdayız", config.AiPausedMessage);
        }

        [Fact]
        public void BeforeTheFirstFetch_EverythingIsOn()
        {
            var config = NewService().Current;
            Assert.True(config.AiEnabled);
            Assert.Null(config.Announcement);
            Assert.False(config.NeedsUpdate("1.0.0"));
        }

        private DashboardViewModel NewDashboard(string version = "1.0.0")
        {
            var appInfo = new Mock<IAppInfo>();
            appInfo.SetupGet(a => a.VersionString).Returns(version);
            return new DashboardViewModel(Mock.Of<ITripService>(), _settings,
                Mock.Of<IDialogService>(), Mock.Of<IAppConfigService>(), appInfo.Object, _preferences);
        }

        [Theory]
        [InlineData(SyncStatus.UpToDate, 0, false, "")]
        [InlineData(SyncStatus.Pending, 3, true, "3")]
        [InlineData(SyncStatus.Offline, 0, true, "")]
        [InlineData(SyncStatus.Offline, 2, true, "2")]
        [InlineData(SyncStatus.Error, 0, true, "")]
        public void TP78_Dashboard_CloudBadge_WhenOfflineOrChangesWait(SyncStatus status, int pending, bool shown, string text)
        {
            var sync = new Mock<ISyncService>();
            sync.SetupGet(s => s.Status).Returns(status);
            sync.SetupGet(s => s.PendingCount).Returns(pending);
            var vm = new DashboardViewModel(Mock.Of<ITripService>(), _settings,
                Mock.Of<IDialogService>(), sync: sync.Object);

            Assert.Equal(shown, vm.ShowSyncBadge);
            Assert.Equal(text, vm.SyncBadgeText);
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void TP91_Dashboard_SignInHint_OnlyForGuests(bool signedIn, bool shown)
        {
            var account = new Mock<IAccountService>();
            account.SetupGet(a => a.IsSignedIn).Returns(signedIn);
            var vm = new DashboardViewModel(Mock.Of<ITripService>(), _settings,
                Mock.Of<IDialogService>(), preferences: _preferences, account: account.Object);

            Assert.Equal(shown, vm.ShowSignInHint);
        }

        [Fact]
        public void TP91_Dashboard_SignInHint_StaysClosed()
        {
            var account = new Mock<IAccountService>();
            account.SetupGet(a => a.IsSignedIn).Returns(false);
            var vm = new DashboardViewModel(Mock.Of<ITripService>(), _settings,
                Mock.Of<IDialogService>(), preferences: _preferences, account: account.Object);

            vm.CloseSignInHintCommand.Execute(null);

            Assert.False(vm.ShowSignInHint);
            var again = new DashboardViewModel(Mock.Of<ITripService>(), _settings,
                Mock.Of<IDialogService>(), preferences: _preferences, account: account.Object);
            Assert.False(again.ShowSignInHint);
        }

        [Fact]
        public void Dashboard_ShowsTheNotice_UntilItIsClosed()
        {
            var vm = NewDashboard();
            vm.ShowNotice(new AppConfig(Announcement: "Bakım bu gece"));
            Assert.True(vm.HasNotice);
            Assert.True(vm.CanCloseNotice);
            Assert.Equal("Bakım bu gece", vm.NoticeText);

            vm.CloseNoticeCommand.Execute(null);
            Assert.False(vm.HasNotice);

            vm.ShowNotice(new AppConfig(Announcement: "Bakım bu gece"));
            Assert.False(vm.HasNotice);
            vm.ShowNotice(new AppConfig(Announcement: "Yeni duyuru"));
            Assert.True(vm.HasNotice);
        }

        [Fact]
        public void Dashboard_AnOldVersionIsAskedToUpdate()
        {
            var vm = NewDashboard("1.0.0");
            vm.ShowNotice(new AppConfig(Announcement: "Duyuru", MinAppVersion: "1.1"));
            Assert.True(vm.HasNotice);
            Assert.False(vm.CanCloseNotice);
            Assert.Equal(AppResources.Dashboard_UpdateNotice, vm.NoticeText);

            var current = NewDashboard("1.1.0");
            current.ShowNotice(new AppConfig(MinAppVersion: "1.1"));
            Assert.False(current.HasNotice);
        }
    }
}
