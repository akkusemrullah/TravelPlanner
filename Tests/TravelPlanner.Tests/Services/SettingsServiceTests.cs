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
        private readonly Mock<TravelPlanner.Services.Sync.ISyncService> _sync = new();

        private SettingsViewModel CreateViewModel(SettingsService settings) =>
            new(settings, _theme.Object, _trips.Object, _notifications.Object, _backup.Object, _share.Object, _filePicker.Object, AppInfo(),
                _account.Object, _sync.Object, _dialogs.Object)
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

        // ---- Profile hub: trash count, preferences summary, backup and legal sheets ----

        private readonly Mock<ITrashService> _trash = new();
        private readonly Mock<IConsentService> _consent = new();

        private SettingsViewModel CreateHubViewModel(SettingsService settings) =>
            new(settings, _theme.Object, _trips.Object, _notifications.Object, _backup.Object, _share.Object, _filePicker.Object, AppInfo(),
                _account.Object, _sync.Object, _dialogs.Object, consent: _consent.Object, trash: _trash.Object);

        private static TrashEntry Deleted(string id) => new(id, "expense", "Taksi", null, DateTime.UtcNow, null, 1);

        [Fact]
        public async Task Profile_TrashRow_ShowsHowManyItemsCanBeRestored()
        {
            _trash.Setup(t => t.GetEntriesAsync(null)).ReturnsAsync(new List<TrashEntry> { Deleted("a"), Deleted("b"), Deleted("c") });
            var vm = CreateHubViewModel(new SettingsService(_preferences));

            await vm.RefreshAsync();

            Assert.Equal(3, vm.TrashCount);
            Assert.True(vm.HasTrash);
            Assert.Contains("3", vm.TrashSubtitle);
        }

        [Fact]
        public async Task Profile_TrashRow_EmptyTrash_ShowsTheHint()
        {
            _trash.Setup(t => t.GetEntriesAsync(null)).ReturnsAsync(new List<TrashEntry>());
            var vm = CreateHubViewModel(new SettingsService(_preferences));

            await vm.RefreshAsync();

            Assert.False(vm.HasTrash);
            Assert.Equal(TravelPlanner.Resources.Strings.AppResources.Trash_Hint, vm.TrashSubtitle);
        }

        [Fact]
        public async Task Profile_Refresh_PicksUpWhatTheTercihlerPageSaved()
        {
            var settings = new SettingsService(_preferences);
            var vm = CreateHubViewModel(settings);

            settings.UserName = "Emrullah Akkuş";
            settings.CurrencyCode = "EUR";
            await vm.RefreshAsync();

            Assert.Equal("EA", vm.Initials);
            Assert.StartsWith("EUR · ", vm.PreferencesSummary);
        }

        [Fact]
        public async Task Profile_BackupRow_CreatesABackup()
        {
            _dialogs.Setup(d => d.ChooseAsync(TravelPlanner.Resources.Strings.AppResources.Backup_Title, It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Backup_ExportAction);
            _backup.Setup(b => b.ExportAsync()).ReturnsAsync(Path.Combine(Path.GetTempPath(), "tp-backup.zip"));

            await CreateHubViewModel(new SettingsService(_preferences)).OpenBackupCommand.ExecuteAsync(null);

            _backup.Verify(b => b.ExportAsync(), Times.Once);
        }

        [Fact]
        public async Task Profile_BackupRow_Cancelled_DoesNothing()
        {
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>())).ReturnsAsync((string?)null);

            await CreateHubViewModel(new SettingsService(_preferences)).OpenBackupCommand.ExecuteAsync(null);

            _backup.Verify(b => b.ExportAsync(), Times.Never);
            _filePicker.Verify(f => f.PickAsync(It.IsAny<PickOptions>()), Times.Never);
        }

        [Fact]
        public async Task Profile_LegalRow_OffersEveryDocument_AndWithdrawingConsent()
        {
            IReadOnlyList<string>? offered = null;
            _dialogs.Setup(d => d.ChooseAsync(TravelPlanner.Resources.Strings.AppResources.Profile_Legal, It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .Callback<string, IReadOnlyList<string>, string?>((_, options, _) => offered = options)
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Consent_Revoke);
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            _consent.SetupGet(c => c.HasAnyConsent).Returns(true);

            await CreateHubViewModel(new SettingsService(_preferences)).OpenLegalCommand.ExecuteAsync(null);

            Assert.NotNull(offered);
            Assert.Contains(TravelPlanner.Resources.Strings.AppResources.Privacy_Title, offered!);
            Assert.Contains(TravelPlanner.Resources.Strings.AppResources.Kvkk_Title, offered!);
            Assert.Equal(6, offered!.Count);
            _consent.Verify(c => c.RevokeAll(), Times.Once);
        }

        // ---- TP-16: signing out can delete the account's data from the device ----

        private void ChooseOnSignOut(string? choice) =>
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>())).ReturnsAsync(choice);

        [Fact]
        public async Task TP16_SignOut_KeepsTheLocalData_ByDefault()
        {
            ChooseOnSignOut(TravelPlanner.Resources.Strings.AppResources.Account_SignOut);

            await CreateViewModel(new SettingsService(_preferences)).SignOutCommand.ExecuteAsync(null);

            _account.Verify(a => a.SignOutAsync(false), Times.Once);
        }

        [Fact]
        public async Task TP16_SignOut_CanDeleteTheLocalData()
        {
            ChooseOnSignOut(TravelPlanner.Resources.Strings.AppResources.Account_SignOutAndDelete);

            await CreateViewModel(new SettingsService(_preferences)).SignOutCommand.ExecuteAsync(null);

            _account.Verify(a => a.SignOutAsync(true), Times.Once);
            _dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task TP16_SignOut_Cancelled_DoesNothing()
        {
            ChooseOnSignOut(null);

            await CreateViewModel(new SettingsService(_preferences)).SignOutCommand.ExecuteAsync(null);

            _account.Verify(a => a.SignOutAsync(It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task TP16_PendingChanges_AreSyncedFirst_AndTheUserIsWarned()
        {
            _sync.SetupGet(s => s.PendingCount).Returns(3);
            string? shown = null;
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .Callback<string, IReadOnlyList<string>, string?>((t, _, _) => shown = t)
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Account_SignOutAndDelete);
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

            await CreateViewModel(new SettingsService(_preferences)).SignOutCommand.ExecuteAsync(null);

            _sync.Verify(s => s.SyncNowAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains(string.Format(TravelPlanner.Resources.Strings.AppResources.Account_SignOutPendingFormat, 3), shown);
            _dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), string.Format(TravelPlanner.Resources.Strings.AppResources.Account_SignOutLoseFormat, 3), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _account.Verify(a => a.SignOutAsync(It.IsAny<bool>()), Times.Never); // declined: nothing is lost
        }

        // ---- TP-12: shared trips are handed over or deleted for everyone, by choice ----

        private readonly Mock<ISharingService> _sharing = new();
        private static readonly OwnedSharedTrip Roma = new("11111111-1111-1111-1111-111111111111", "Roma", 2);

        private SettingsViewModel CreateViewModelWithSharing() =>
            new(new SettingsService(_preferences), _theme.Object, _trips.Object, _notifications.Object, _backup.Object, _share.Object, _filePicker.Object, AppInfo(),
                _account.Object, new Mock<TravelPlanner.Services.Sync.ISyncService>().Object, _dialogs.Object, _sharing.Object)
            {
                AccountDeletedNoticeDelay = TimeSpan.Zero
            };

        private void WithSharedTrip()
        {
            _sharing.Setup(s => s.GetOwnedSharedTripsAsync()).ReturnsAsync(new[] { Roma });
            _sharing.Setup(s => s.GetMembersAsync(Roma.TripSyncId)).ReturnsAsync(new[]
            {
                new TripMember("me", TripRole.Owner, "Ben", null, DateTime.UtcNow, true),
                new TripMember("can", TripRole.Viewer, "Can", null, DateTime.UtcNow, false),
                new TripMember("ayse", TripRole.Editor, "Ayşe", null, DateTime.UtcNow, false),
            });
        }

        [Fact]
        public async Task DeleteAccount_SharedTrip_CanBeHandedOver_BeforeTheAccountGoes()
        {
            ConfirmDeletion("SİL");
            WithSharedTrip();
            string? asked = null;
            IReadOnlyList<string>? offered = null;
            _dialogs.Setup(d => d.ChooseAsync(It.Is<string>(t => t.Contains("Roma") && t.Contains("2")), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .Callback<string, IReadOnlyList<string>, string?>((t, _, _) => asked = t)
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Account_TransferOwnership);
            _dialogs.Setup(d => d.ChooseAsync(It.Is<string>(t => t.StartsWith(string.Format(TravelPlanner.Resources.Strings.AppResources.Account_ChooseNewOwnerFormat, "Roma"))), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .Callback<string, IReadOnlyList<string>, string?>((_, options, _) => offered = options)
                    .ReturnsAsync("Can");
            var order = new List<string>();
            _sharing.Setup(s => s.TransferOwnershipAsync(Roma.TripSyncId, "can")).Callback(() => order.Add("transfer")).Returns(Task.CompletedTask);
            _account.Setup(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>())).Callback(() => order.Add("delete")).Returns(Task.CompletedTask);

            await CreateViewModelWithSharing().DeleteAccountCommand.ExecuteAsync(null);

            Assert.NotNull(asked);
            Assert.Equal(new[] { "Ayşe", "Can" }, offered); // editors first, never the user
            Assert.Equal(new[] { "transfer", "delete" }, order);
        }

        [Fact]
        public async Task DeleteAccount_SharedTrip_DeleteForEveryone_HandsNothingOver()
        {
            ConfirmDeletion("SİL");
            WithSharedTrip();
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Account_DeleteForEveryone);

            await CreateViewModelWithSharing().DeleteAccountCommand.ExecuteAsync(null);

            _sharing.Verify(s => s.TransferOwnershipAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _account.Verify(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteAccount_SharedTrip_Cancel_ChangesNothing()
        {
            ConfirmDeletion("SİL");
            WithSharedTrip();
            _dialogs.Setup(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync((string?)null);

            await CreateViewModelWithSharing().DeleteAccountCommand.ExecuteAsync(null);

            _sharing.Verify(s => s.TransferOwnershipAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _account.Verify(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task DeleteAccount_HandOverChosen_ButKeywordNotTyped_HandsNothingOver()
        {
            ConfirmDeletion(null);
            WithSharedTrip();
            _dialogs.Setup(d => d.ChooseAsync(It.Is<string>(t => t.Contains("2")), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync(TravelPlanner.Resources.Strings.AppResources.Account_TransferOwnership);
            _dialogs.Setup(d => d.ChooseAsync(It.Is<string>(t => !t.Contains("2")), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()))
                    .ReturnsAsync("Ayşe");

            await CreateViewModelWithSharing().DeleteAccountCommand.ExecuteAsync(null);

            _sharing.Verify(s => s.TransferOwnershipAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _account.Verify(a => a.DeleteAccountAsync(It.IsAny<CancellationToken>()), Times.Never);
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
