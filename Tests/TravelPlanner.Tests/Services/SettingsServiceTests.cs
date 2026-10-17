using Moq;
using Xunit;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    [Collection("Currency")] // SettingsService pushes the currency into the static MoneyFormatter
    public class SettingsServiceTests : IDisposable
    {
        private readonly InMemoryPreferences _preferences = new();

        public void Dispose() => MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;

        [Fact]
        public void Defaults_WhenNothingIsSaved()
        {
            var settings = new SettingsService(_preferences);

            Assert.Equal(string.Empty, settings.UserName);
            Assert.Equal("TRY", settings.CurrencyCode);
            Assert.Equal(9, settings.ReminderHour);
            Assert.Equal(AppThemeOption.System, settings.Theme);
            Assert.Equal(AppLanguages.System, settings.Language);
        }

        [Fact]
        public void EachAccount_HasItsOwnName_AndTheGuestStartsWithout()
        {
            const string ali = "11111111-1111-4111-8111-111111111111";
            const string ayse = "22222222-2222-4222-8222-222222222222";
            _preferences.Set(SettingsService.LegacyUserNameKey, "Emrullah Akkuş"); // name from an older version
            var settings = new SettingsService(_preferences);

            Assert.Equal(string.Empty, settings.UserName);          // guest: profile shows "Travel Planner"

            settings.UseAccount(ali);
            Assert.Equal("Emrullah Akkuş", settings.UserName);      // the old name went to the account
            settings.UseAccount(ayse);
            Assert.Equal(string.Empty, settings.UserName);
            settings.UserName = "Ayşe";

            settings.UseAccount(null);
            Assert.Equal(string.Empty, settings.UserName);          // signing out doesn't leave a name behind
            settings.UseAccount(ali);
            Assert.Equal("Emrullah Akkuş", settings.UserName);
            settings.UseAccount(ayse);
            Assert.Equal("Ayşe", settings.UserName);
        }

        [Fact]
        public void Values_RoundTrip_AndCurrencyReachesTheFormatter()
        {
            var settings = new SettingsService(_preferences)
            {
                UserName = "  Emrullah  ",
                CurrencyCode = "EUR",
                ReminderHour = 20,
                Theme = AppThemeOption.Dark,
                Language = AppLanguages.English,
            };

            var reloaded = new SettingsService(_preferences);
            Assert.Equal("Emrullah", reloaded.UserName);
            Assert.Equal("EUR", reloaded.CurrencyCode);
            Assert.Equal(20, reloaded.ReminderHour);
            Assert.Equal(AppThemeOption.Dark, reloaded.Theme);
            Assert.Equal(AppLanguages.English, reloaded.Language);
            Assert.Equal("EUR", MoneyFormatter.CurrencyCode);
        }

        [Fact]
        public void InvalidValues_AreNormalised()
        {
            var settings = new SettingsService(_preferences) { CurrencyCode = "ABC", ReminderHour = 99, Language = "fr" };

            Assert.Equal("TRY", settings.CurrencyCode);
            Assert.Equal(23, settings.ReminderHour);
            Assert.Equal(AppLanguages.System, settings.Language);
        }
    }

    [Collection("Currency")]
    public class SettingsViewModelTests : IDisposable
    {
        private readonly InMemoryPreferences _preferences = new();
        private readonly Mock<IThemeService> _theme = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<INotificationService> _notifications = new();
        private readonly Mock<IBackupService> _backup = new();
        private readonly Mock<Microsoft.Maui.ApplicationModel.DataTransfer.IShare> _share = new();
        private readonly Mock<IFilePicker> _filePicker = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        public void Dispose() => MoneyFormatter.CurrencyCode = MoneyFormatter.DefaultCurrencyCode;

        private readonly Mock<IAccountService> _account = new();

        private SettingsViewModel CreateViewModel(SettingsService settings) =>
            new(settings, _theme.Object, _trips.Object, _notifications.Object, _backup.Object, _share.Object, _filePicker.Object, AppInfo(),
                _account.Object, new Mock<TravelPlanner.Services.Sync.ISyncService>().Object, _dialogs.Object)
            {
                AccountDeletedNoticeDelay = TimeSpan.Zero
            };

        private void ConfirmDeletion(string? typed)
        {
            _account.SetupGet(a => a.IsSignedIn).Returns(true);
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            _dialogs.Setup(d => d.PromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                    .ReturnsAsync(typed);
        }

        [Theory]
        [InlineData("SİL")]
        [InlineData("sil")]
        [InlineData(" SIL ")]
        public async Task DeleteAccount_RunsAfterTypingTheKeyword(string typed)
        {
            ConfirmDeletion(typed);

            await CreateViewModel(new SettingsService(_preferences)).DeleteAccountCommand.ExecuteAsync(null);

            _account.Verify(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()), Times.Once);
            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), TravelPlanner.Resources.Strings.AppResources.Account_Deleted));
        }

        [Theory]
        [InlineData("silme")]
        [InlineData(null)]
        public async Task DeleteAccount_DoesNothing_WithoutTheKeyword(string? typed)
        {
            ConfirmDeletion(typed);

            await CreateViewModel(new SettingsService(_preferences)).DeleteAccountCommand.ExecuteAsync(null);

            _account.Verify(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DeleteAccount_Offline_SaysNothingWasDeleted()
        {
            ConfirmDeletion("SİL");
            _account.Setup(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new TravelPlanner.Services.Sync.SyncException(TravelPlanner.Services.Sync.SyncErrorKind.Network, "offline"));
            var vm = CreateViewModel(new SettingsService(_preferences));

            await vm.DeleteAccountCommand.ExecuteAsync(null);

            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), TravelPlanner.Resources.Strings.AppResources.Account_DeleteOffline));
            Assert.False(vm.IsDeletingAccount);
        }

        private static IAppInfo AppInfo()
        {
            var info = new Mock<IAppInfo>();
            info.SetupGet(i => i.VersionString).Returns("1.0.0");
            info.SetupGet(i => i.BuildString).Returns("12");
            return info.Object;
        }

        [Fact]
        public void ShowsTheAppVersion()
        {
            Assert.Equal("Sürüm 1.0.0 (12)", CreateViewModel(new SettingsService(_preferences)).VersionText);
        }

        [Fact]
        public async Task Restore_IsCancelledWhenTheUserDoesNotConfirm()
        {
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

            await CreateViewModel(new SettingsService(_preferences)).ImportBackupCommand.ExecuteAsync(null);

            _filePicker.Verify(f => f.PickAsync(It.IsAny<PickOptions?>()), Times.Never);
            _backup.Verify(b => b.ImportAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<Func<SharedTripsInBackup, Task<SharedTripRestoreChoice>>>()), Times.Never);
        }

        [Fact]
        public async Task Export_HandsTheZipToTheShareSheet()
        {
            _backup.Setup(b => b.ExportAsync()).ReturnsAsync("/tmp/TravelPlanner-2026-10-09.zip");

            await CreateViewModel(new SettingsService(_preferences)).ExportBackupCommand.ExecuteAsync(null);

            _share.Verify(s => s.RequestAsync(It.IsAny<Microsoft.Maui.ApplicationModel.DataTransfer.ShareFileRequest>()), Times.Once);
        }

        [Fact]
        public void LoadsTheSavedValues()
        {
            var settings = new SettingsService(_preferences) { UserName = "Emrullah", CurrencyCode = "USD", ReminderHour = 8 };

            var vm = CreateViewModel(settings);

            Assert.Equal("Emrullah", vm.UserName);
            Assert.Equal("USD", vm.SelectedCurrency!.Value);
            Assert.Equal(8, vm.SelectedReminderHour!.Value);
            Assert.Equal("08:00", vm.SelectedReminderHour.ToString());
        }

        [Fact]
        public void PickingATheme_PreviewsItImmediately()
        {
            var vm = CreateViewModel(new SettingsService(_preferences));

            vm.SelectedTheme = vm.Themes.Single(t => t.Value == AppThemeOption.Dark);

            _theme.Verify(t => t.Apply(AppThemeOption.Dark), Times.Once);
        }

        [Fact]
        public void PickingATheme_SavesItRightAway_SoLeavingTheTabKeepsIt()
        {
            var settings = new SettingsService(_preferences);
            var vm = CreateViewModel(settings);

            vm.SelectedTheme = vm.Themes.Single(t => t.Value == AppThemeOption.Dark);

            Assert.Equal(AppThemeOption.Dark, settings.Theme);
        }

        [Fact]
        public void OpeningSettings_DoesNotReapplyTheTheme()
        {
            var settings = new SettingsService(_preferences) { Theme = AppThemeOption.Dark };

            var vm = CreateViewModel(settings);

            Assert.Equal(AppThemeOption.Dark, vm.SelectedTheme!.Value);
            _theme.Verify(t => t.Apply(It.IsAny<AppThemeOption>()), Times.Never);
        }

        [Fact]
        public async Task Save_PersistsEverything_AndReschedulesRemindersWhenTheHourChanges()
        {
            var settings = new SettingsService(_preferences);
            var trip = new Trip { Id = 4, Name = "Roma", City = "Roma", StartDate = DateTime.Today.AddDays(10), EndDate = DateTime.Today.AddDays(12) };
            _trips.Setup(t => t.GetAllAsync()).ReturnsAsync(new List<Trip> { trip });
            var vm = CreateViewModel(settings);

            vm.UserName = "Emrullah";
            vm.SelectedCurrency = vm.Currencies.Single(c => c.Value == "GBP");
            vm.SelectedReminderHour = vm.ReminderHours.Single(h => h.Value == 18);
            await vm.SaveCommand.ExecuteAsync(null);

            Assert.Equal("Emrullah", settings.UserName);
            Assert.Equal("GBP", settings.CurrencyCode);
            Assert.Equal(18, settings.ReminderHour);
            _notifications.Verify(n => n.CancelTripNotifications(4), Times.Once);
            _notifications.Verify(n => n.ScheduleTripNotificationsAsync(trip), Times.Once);
        }

        [Fact]
        public async Task Save_WithoutHourChange_DoesNotTouchReminders()
        {
            var vm = CreateViewModel(new SettingsService(_preferences));
            vm.UserName = "Emrullah";

            await vm.SaveCommand.ExecuteAsync(null);

            _trips.Verify(t => t.GetAllAsync(), Times.Never);
        }
    }
}
