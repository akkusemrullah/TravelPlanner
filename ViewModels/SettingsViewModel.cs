using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.ViewModels
{
    /// <summary>One entry in a Settings Picker. The Picker shows ToString(), so no ItemDisplayBinding is needed.</summary>
    public sealed record SettingsOption<T>(T Value, string Display)
    {
        public override string ToString() => Display;
    }

    /// <summary>Backs the Ayarlar (Settings) page: name, currency, reminder time, theme and language (TP-305).</summary>
    public partial class SettingsViewModel : ViewModelBase
    {
        private readonly ISettingsService _settings;
        private readonly IThemeService _themeService;
        private readonly ITripService _tripService;
        private readonly INotificationService _notificationService;
        private readonly IBackupService _backupService;
        private readonly IShare _share;
        private readonly IFilePicker _filePicker;
        private readonly IAccountService _account;
        private readonly ISyncService _sync;
        private readonly ISharingService? _sharing;
        private readonly IConsentService? _consent;
        private readonly ITrashService? _trash;
        private readonly IJoinTripFlow? _joinFlow;

        /// <summary>"Sürüm 1.0.0 (12)" - TP-507.</summary>
        public string VersionText { get; }

        /// <summary>"© 2026 EAKSOFT. Tüm hakları saklıdır." - About section.</summary>
        public string CopyrightText { get; } = string.Format(AppResources.About_CopyrightFormat, DateTime.Today.Year);

        [ObservableProperty]
        private string userName = string.Empty;

        [ObservableProperty]
        private SettingsOption<string>? selectedCurrency;

        [ObservableProperty]
        private SettingsOption<int>? selectedReminderHour;

        [ObservableProperty]
        private SettingsOption<AppThemeOption>? selectedTheme;

        [ObservableProperty]
        private SettingsOption<string>? selectedLanguage;

        /// <summary>Shown after saving when the language changed - XAML strings refresh on the next start.</summary>
        [ObservableProperty]
        private bool showLanguageRestartHint;

        /// <summary>Shown after a successful save - Settings is a tab root now, so there is no page to go back to.</summary>
        [ObservableProperty]
        private bool showSavedHint;

        /// <summary>Shown as the account name while no "Ad Soyad" is set (brand name - the same in every language).</summary>
        public const string DefaultDisplayName = "Travel Planner";

        /// <summary>Profile header: the saved "Ad Soyad", or "Travel Planner" while it's empty.</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(UserName) ? DefaultDisplayName : UserName.Trim();

        /// <summary>Profile avatar: initials of the first and last name ("Emrullah Akkuş" -> "EA").</summary>
        public string Initials => PersonName.Initials(UserName);

        /// <summary>False while no name is set - the avatar then shows a person icon instead of letters.</summary>
        public bool HasInitials => Initials.Length > 0;

        partial void OnUserNameChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Initials));
            OnPropertyChanged(nameof(HasInitials));
            ShowSavedHint = false;
        }

        public IReadOnlyList<SettingsOption<string>> Currencies { get; } = MoneyFormatter.SupportedCurrencies
            .Select(code => new SettingsOption<string>(code, MoneyFormatter.DisplayName(code)))
            .ToList();

        public IReadOnlyList<SettingsOption<int>> ReminderHours { get; } = Enumerable.Range(0, 24)
            .Select(hour => new SettingsOption<int>(hour, $"{hour:00}:00"))
            .ToList();

        public IReadOnlyList<SettingsOption<AppThemeOption>> Themes { get; } = new[]
        {
            new SettingsOption<AppThemeOption>(AppThemeOption.System, AppResources.Theme_System),
            new SettingsOption<AppThemeOption>(AppThemeOption.Light, AppResources.Theme_Light),
            new SettingsOption<AppThemeOption>(AppThemeOption.Dark, AppResources.Theme_Dark),
        };

        public IReadOnlyList<SettingsOption<string>> Languages { get; } = new[]
        {
            new SettingsOption<string>(AppLanguages.System, AppResources.Language_System),
            new SettingsOption<string>(AppLanguages.Turkish, AppResources.Language_Turkish),
            new SettingsOption<string>(AppLanguages.English, AppResources.Language_English),
        };

        public SettingsViewModel(
            ISettingsService settings,
            IThemeService themeService,
            ITripService tripService,
            INotificationService notificationService,
            IBackupService backupService,
            IShare share,
            IFilePicker filePicker,
            IAppInfo appInfo,
            IAccountService account,
            ISyncService sync,
            IDialogService dialogs,
            ISharingService? sharing = null,
            IConsentService? consent = null,
            ITrashService? trash = null,
            IJoinTripFlow? joinFlow = null)
            : base(dialogs)
        {
            _joinFlow = joinFlow ?? (sharing is null ? null : new JoinTripFlow(sharing, account, dialogs));
            _trash = trash;
            _consent = consent;
            _account = account;
            _sync = sync;
            _sharing = sharing;
            _settings = settings;
            _themeService = themeService;
            _tripService = tripService;
            _notificationService = notificationService;
            _backupService = backupService;
            _share = share;
            _filePicker = filePicker;
            VersionText = string.Format(AppResources.Settings_VersionFormat, appInfo.VersionString, appInfo.BuildString);

            LoadFromSettings();
        }

        private bool _loadingSettings;

        // ================= Profile hub =================

        /// <summary>Items in the trash - shown as a count on the Profile's "Çöp kutusu" row so it gets noticed.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTrash))]
        [NotifyPropertyChangedFor(nameof(TrashSubtitle))]
        private int trashCount;

        public bool HasTrash => TrashCount > 0;

        /// <summary>"3 öğe geri alınabilir", or the general hint while the trash is empty.</summary>
        public string TrashSubtitle => TrashCount > 0
            ? string.Format(AppResources.Profile_TrashCountFormat, TrashCount)
            : AppResources.Trash_Hint;

        /// <summary>One line under the Profile's "Tercihler" row: "TRY · Sistem · Türkçe".</summary>
        public string PreferencesSummary => string.Join(" · ", new[]
        {
            SelectedCurrency?.Value, SelectedTheme?.Display, SelectedLanguage?.Display,
        }.Where(part => !string.IsNullOrEmpty(part)));

        partial void OnSelectedCurrencyChanged(SettingsOption<string>? value) => OnPropertyChanged(nameof(PreferencesSummary));

        partial void OnSelectedLanguageChanged(SettingsOption<string>? value) => OnPropertyChanged(nameof(PreferencesSummary));

        /// <summary>
        /// Profile tab: re-reads the saved settings (the Tercihler page may have changed the name or currency) and
        /// counts the trash. Called whenever the tab appears.
        /// </summary>
        public async Task RefreshAsync()
        {
            LoadFromSettings();
            if (_trash is null)
                return;
            try
            {
                TrashCount = (await _trash.GetEntriesAsync()).Count;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Settings] Trash count failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private Task OpenPreferencesAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Preferences));

        /// <summary>"Yedekleme" row: a short sheet with "Yedek al" / "Yedekten geri yükle".</summary>
        [RelayCommand]
        private async Task OpenBackupAsync()
        {
            var choice = await Dialogs.ChooseAsync(AppResources.Backup_Title,
                new[] { AppResources.Backup_ExportAction, AppResources.Backup_ImportTitle },
                AppResources.Common_Cancel);
            if (choice == AppResources.Backup_ExportAction)
                await ExportBackupAsync();
            else if (choice == AppResources.Backup_ImportTitle)
                await ImportBackupAsync();
        }

        /// <summary>What the one "Yasal ve gizlilik" row offers: label and document key (null = withdraw consent).</summary>
        internal static IReadOnlyList<(string Label, string? Key)> LegalChoices() => new (string, string?)[]
        {
            (AppResources.Privacy_Title, LegalDocuments.PrivacyKey),
            (AppResources.Kvkk_Title, LegalDocuments.KvkkKey),
            (AppResources.Terms_Title, LegalDocuments.TermsKey),
            (AppResources.Consent_DocTitle, LegalDocuments.ConsentKey),
            (AppResources.Licenses_Title, LegalDocuments.LicensesKey),
            (AppResources.Consent_Revoke, null),
        };

        [RelayCommand]
        private async Task OpenLegalAsync()
        {
            var choices = LegalChoices();
            var labels = choices.Select(c => c.Label).ToList();
            var picked = await Dialogs.ChooseAsync(AppResources.Profile_Legal, labels, AppResources.Common_Cancel);
            var index = picked is null ? -1 : labels.IndexOf(picked);
            if (index < 0)
                return;

            if (choices[index].Key is { } key)
                await OpenDocumentAsync(key);
            else
                await RevokeConsentAsync();
        }

        private void LoadFromSettings()
        {
            // Filling the pickers must not re-apply the theme: the saved theme is already active, and changing
            // it while the Settings tab is being built is exactly the kind of mid-layout switch that crashed.
            _loadingSettings = true;
            try
            {
                UserName = _settings.UserName;
                SelectedCurrency = Currencies.FirstOrDefault(c => c.Value == _settings.CurrencyCode) ?? Currencies[0];
                SelectedReminderHour = ReminderHours.FirstOrDefault(h => h.Value == _settings.ReminderHour) ?? ReminderHours[SettingsService.DefaultReminderHour];
                SelectedTheme = Themes.FirstOrDefault(t => t.Value == _settings.Theme) ?? Themes[0];
                SelectedLanguage = Languages.FirstOrDefault(l => l.Value == _settings.Language) ?? Languages[0];
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        // The theme applies and is saved as soon as it is picked. It used to be only a preview that was undone
        // when the page disappeared without Save - but Settings is a tab now, so simply switching tabs threw
        // the chosen dark theme away (and re-applying it during the tab switch could crash on Android).
        partial void OnSelectedThemeChanged(SettingsOption<AppThemeOption>? value)
        {
            OnPropertyChanged(nameof(PreferencesSummary));
            if (_loadingSettings || value is null)
                return;

            _settings.Theme = value.Value;
            _themeService.Apply(value.Value);
        }

        [RelayCommand]
        private Task SaveAsync() => RunSafeAsync(async () =>
        {
            var reminderChanged = SelectedReminderHour is not null && SelectedReminderHour.Value != _settings.ReminderHour;
            var languageChanged = SelectedLanguage is not null && SelectedLanguage.Value != _settings.Language;

            _settings.UserName = UserName;
            if (SelectedCurrency is not null) _settings.CurrencyCode = SelectedCurrency.Value;
            if (SelectedReminderHour is not null) _settings.ReminderHour = SelectedReminderHour.Value;
            if (SelectedTheme is not null) _settings.Theme = SelectedTheme.Value;
            if (SelectedLanguage is not null) _settings.Language = SelectedLanguage.Value;

            // Already-scheduled reminders used the old hour - move them to the new one.
            if (reminderChanged)
            {
                foreach (var trip in await _tripService.GetAllAsync())
                {
                    _notificationService.CancelTripNotifications(trip.Id);
                    await _notificationService.ScheduleTripNotificationsAsync(trip);
                }
            }

            if (languageChanged)
            {
                LocalizationHelper.Apply(_settings.Language);
                ShowLanguageRestartHint = true;
                return; // stay on the page so the restart hint is seen
            }

            // Settings is a tab root (Profile) - confirm in place instead of navigating back.
            ShowSavedHint = true;
        }, AppResources.Settings_SaveError);

        // ================= G3: account =================

        public bool IsSignedIn => _account.IsSignedIn;

        public bool IsGuest => !_account.IsSignedIn;

        /// <summary>The account's e-mail, or "Misafir" while not signed in.</summary>
        public string AccountTitle => _account.IsSignedIn
            ? _account.Email ?? _account.DisplayName ?? string.Empty
            : AppResources.Account_GuestTitle;

        public string AccountSubtitle => _account.IsSignedIn ? AppResources.Account_SignedInHint : AppResources.Auth_GuestHint;

        /// <summary>"Eşitlendi · 14:32", "3 değişiklik bekliyor", "Çevrimdışı" … (G4).</summary>
        public string SyncStatusText => DescribeSync(_sync);

        /// <summary>The sync status line, also shown when the home screen's cloud badge is tapped (TP-78).</summary>
        internal static string DescribeSync(ISyncService sync) => sync.Status switch
        {
            SyncStatus.Syncing => AppResources.Sync_StatusSyncing,
            SyncStatus.Offline => sync.PendingCount > 0
                ? string.Format(AppResources.Sync_StatusOfflinePendingFormat, sync.PendingCount)
                : AppResources.Sync_StatusOffline,
            SyncStatus.Pending => string.Format(AppResources.Sync_StatusPendingFormat, sync.PendingCount),
            SyncStatus.Error => AppResources.Sync_StatusError,
            SyncStatus.UpToDate when sync.LastSyncedAtUtc is { } last =>
                string.Format(AppResources.Sync_StatusUpToDateFormat, last.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture)),
            SyncStatus.UpToDate => AppResources.Sync_StatusUpToDate,
            _ => string.Empty,
        };

        public bool IsSyncing => _sync.Status == SyncStatus.Syncing;

        /// <summary>SettingsPage calls this in OnAppearing / OnDisappearing so the status line stays live.</summary>
        public void AttachSyncStatus(bool attach)
        {
            _sync.StatusChanged -= OnSyncStatusChanged;
            if (attach)
            {
                _sync.StatusChanged += OnSyncStatusChanged;
                OnSyncStatusChanged(this, EventArgs.Empty);
            }
        }

        private void OnSyncStatusChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(() =>
        {
            OnPropertyChanged(nameof(SyncStatusText));
            OnPropertyChanged(nameof(IsSyncing));
        });

        [RelayCommand]
        private Task SyncNowAsync() => RunSafeAsync(() => _sync.SyncNowAsync());

        [RelayCommand]
        private Task OpenSignInAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Auth));

        /// <summary>
        /// Signs out; the app switches back to the guest data. TP-16: the user chooses whether the account's data stays
        /// on the device (quick next sign-in) or is deleted from it (a borrowed or shared phone). Changes not uploaded
        /// yet are synced first when possible, and the user is told how many would stay behind or be lost.
        /// </summary>
        [RelayCommand]
        private Task SignOutAsync() => RunSafeAsync(async () =>
        {
            var pending = _sync.PendingCount;
            if (pending > 0)
            {
                // One last try to get them up (does nothing offline).
                try
                {
                    await _sync.SyncNowAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Settings] Sync before sign-out failed: {ex.Message}");
                }
                pending = _sync.PendingCount;
            }

            // A short title: an action sheet can cut a long one off.
            var title = pending > 0
                ? string.Format(AppResources.Account_SignOutPendingFormat, pending)
                : AppResources.Account_SignOutTitle;

            var choice = await Dialogs.ChooseAsync(title,
                new[] { AppResources.Account_SignOut, AppResources.Account_SignOutAndDelete },
                AppResources.Common_Cancel);
            if (choice != AppResources.Account_SignOut && choice != AppResources.Account_SignOutAndDelete)
                return;

            var deleteLocalData = choice == AppResources.Account_SignOutAndDelete;
            if (deleteLocalData && pending > 0 &&
                !await Dialogs.ConfirmAsync(AppResources.Account_SignOutTitle,
                                            string.Format(AppResources.Account_SignOutLoseFormat, pending),
                                            AppResources.Account_SignOutAndDelete, AppResources.Common_Cancel))
                return;

            await _account.SignOutAsync(deleteLocalData);
        });

        // ================= G7: delete the account =================

        /// <summary>Shown while the account is being deleted (the card's buttons are hidden meanwhile).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanDeleteAccount))]
        private bool isDeletingAccount;

        public bool CanDeleteAccount => IsSignedIn && !IsDeletingAccount;

        /// <summary>
        /// Deletes the account after two confirmations (a warning, then typing "SİL"). Online only. Afterwards the app
        /// continues as a guest - the shell is rebuilt, so the result is shown on the new page a moment later.
        /// </summary>
        [RelayCommand]
        private Task DeleteAccountAsync() => RunSafeAsync(async () =>
        {
            if (!_account.IsSignedIn || IsDeletingAccount)
                return;

            if (!await Dialogs.ConfirmAsync(AppResources.Account_DeleteTitle, AppResources.Account_DeleteMessage,
                                            AppResources.Account_DeleteContinue, AppResources.Common_Cancel))
                return;

            // TP-12: trips other people share would be deleted for all of them - hand over or delete, per trip.
            IReadOnlyList<(OwnedSharedTrip Trip, TripMember NewOwner)> handovers;
            try
            {
                if (await ChooseSharedTripHandoversAsync() is not { } chosen)
                    return;
                handovers = chosen;
            }
            catch (SharingException ex) when (ex.Kind is SharingErrorKind.Offline or SharingErrorKind.NotSignedIn)
            {
                await Dialogs.ShowMessageAsync(AppResources.Account_DeleteTitle, AppResources.Account_DeleteOffline);
                return;
            }

            var typed = await Dialogs.PromptAsync(AppResources.Account_DeleteTitle,
                string.Format(AppResources.Account_DeleteTypeFormat, AppResources.Account_DeleteKeyword),
                accept: AppResources.Account_DeleteConfirm, cancel: AppResources.Common_Cancel);
            if (typed is null)
                return;
            // Case and Turkish letters don't matter: "sil", "SIL" and "SİL" all confirm.
            if (PackingPlanMatcher.Fold(typed).Trim() != PackingPlanMatcher.Fold(AppResources.Account_DeleteKeyword).Trim())
            {
                await Dialogs.ShowMessageAsync(AppResources.Account_DeleteTitle, AppResources.Account_DeleteKeywordMismatch);
                return;
            }

            IsDeletingAccount = true;
            try
            {
                // Hand-overs first: a trip handed over survives delete_account. If one fails, nothing is deleted.
                foreach (var (trip, newOwner) in handovers)
                    await _sharing!.TransferOwnershipAsync(trip.TripSyncId, newOwner.UserId);

                await _account.DeleteAccountAsync();
            }
            catch (SharingException ex) when (ex.Kind is SharingErrorKind.Offline or SharingErrorKind.NotSignedIn)
            {
                await Dialogs.ShowMessageAsync(AppResources.Account_DeleteTitle, AppResources.Account_DeleteOffline);
                return;
            }
            catch (SyncException ex) when (ex.Kind is SyncErrorKind.Network or SyncErrorKind.NotSignedIn)
            {
                await Dialogs.ShowMessageAsync(AppResources.Account_DeleteTitle, AppResources.Account_DeleteOffline);
                return;
            }
            finally
            {
                IsDeletingAccount = false;
            }

            // App rebuilds the shell for the guest data right after the switch; say it on the new page.
            await Task.Delay(AccountDeletedNoticeDelay);
            await Dialogs.ShowMessageAsync(AppResources.Account_DeleteTitle, AppResources.Account_Deleted);
        }, AppResources.Account_DeleteError);

        /// <summary>
        /// TP-12: for each of the user's trips that others take part in, asks (showing how many people it affects)
        /// whether to hand it over - and to whom - or to delete it for everyone. Null = the user backed out.
        /// Nothing is changed here; the hand-overs run only after the final confirmation.
        /// </summary>
        private async Task<IReadOnlyList<(OwnedSharedTrip Trip, TripMember NewOwner)>?> ChooseSharedTripHandoversAsync()
        {
            var handovers = new List<(OwnedSharedTrip, TripMember)>();
            if (_sharing is null)
                return handovers;

            foreach (var trip in await _sharing.GetOwnedSharedTripsAsync())
            {
                var choice = await Dialogs.ChooseAsync(
                    string.Format(AppResources.Account_DeleteSharedTripFormat, trip.Name, trip.OtherMembers),
                    new[] { AppResources.Account_TransferOwnership, AppResources.Account_DeleteForEveryone },
                    AppResources.Common_Cancel);
                if (choice == AppResources.Account_DeleteForEveryone)
                    continue;
                if (choice != AppResources.Account_TransferOwnership)
                    return null;

                // Editors first: they already look after the trip.
                var candidates = (await _sharing.GetMembersAsync(trip.TripSyncId))
                    .Where(m => !m.IsMe && m.Role != TripRole.Owner)
                    .OrderBy(m => m.Role == TripRole.Editor ? 0 : 1)
                    .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                if (candidates.Count == 0)
                    continue; // everyone else left meanwhile: nobody to hand it to, nobody to lose it

                var labels = candidates.Select((m, i) => candidates.Take(i).Any(o => o.Name == m.Name) ? $"{m.Name} ({i + 1})" : m.Name).ToList();
                var picked = await Dialogs.ChooseAsync(string.Format(AppResources.Account_ChooseNewOwnerFormat, trip.Name), labels, AppResources.Common_Cancel);
                var index = picked is null ? -1 : labels.IndexOf(picked);
                if (index < 0)
                    return null;

                handovers.Add((trip, candidates[index]));
            }

            return handovers;
        }

        /// <summary>Wait before the "account deleted" message, so it lands on the rebuilt shell (0 in tests).</summary>
        internal TimeSpan AccountDeletedNoticeDelay { get; set; } = TimeSpan.FromMilliseconds(900);

        // ================= TP-502: backup & restore =================

        /// <summary>Exports everything to a .zip and opens the share sheet so it can be saved (Files, Drive, e-mail…).</summary>
        [RelayCommand]
        private Task ExportBackupAsync() => RunSafeAsync(async () =>
        {
            var path = await _backupService.ExportAsync();
            await _share.RequestAsync(new ShareFileRequest
            {
                Title = AppResources.Backup_ShareTitle,
                File = new ShareFile(path, "application/zip")
            });
        }, AppResources.Backup_ExportError);

        [RelayCommand]
        private Task ImportBackupAsync() => RunSafeAsync(async () =>
        {
            if (!await Dialogs.ConfirmAsync(AppResources.Backup_ImportTitle, AppResources.Backup_ImportWarning, AppResources.Backup_ImportAccept))
                return;

            var file = await _filePicker.PickAsync(new PickOptions { PickerTitle = AppResources.Backup_ImportTitle });
            if (file is null)
                return;

            BackupImportResult result;
            try
            {
                await using var stream = await file.OpenReadAsync();
                result = await _backupService.ImportAsync(stream, file.FileName, AskAboutSharedTripsAsync);
            }
            catch (InvalidBackupException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Backup_Invalid);
                return;
            }

            // Restored trips need their reminders scheduled on this device.
            foreach (var trip in await _tripService.GetAllAsync())
            {
                _notificationService.CancelTripNotifications(trip.Id);
                await _notificationService.ScheduleTripNotificationsAsync(trip);
            }

            var message = string.Format(AppResources.Backup_ImportedFormat, result.Trips, result.Expenses, result.Attachments, result.ExportedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            if (result.SkippedSharedTrips > 0)
                message += "\n\n" + string.Format(AppResources.Backup_SharedSkippedFormat, result.SkippedSharedTrips);
            if (result.CopiedSharedTrips > 0)
                message += "\n\n" + string.Format(AppResources.Backup_SharedCopiedFormat, result.CopiedSharedTrips);
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, message);
        }, AppResources.Backup_ImportError);

        /// <summary>
        /// TP-10: a backup with shared trips would overwrite the other members' changes - ask before restoring.
        /// Closing the sheet cancels the restore (nothing has been replaced yet).
        /// </summary>
        private async Task<SharedTripRestoreChoice> AskAboutSharedTripsAsync(SharedTripsInBackup shared)
        {
            // A short title: an action sheet can cut a long one off.
            var choice = await Dialogs.ChooseAsync(
                string.Format(AppResources.Backup_SharedTripsFormat, shared.TripNames.Count),
                new[] { AppResources.Backup_SharedSkip, AppResources.Backup_SharedCopy },
                AppResources.Common_Cancel);

            if (choice == AppResources.Backup_SharedSkip)
                return SharedTripRestoreChoice.Skip;
            if (choice == AppResources.Backup_SharedCopy)
                return SharedTripRestoreChoice.Copy;
            return SharedTripRestoreChoice.Cancel;
        }

        [RelayCommand]
        private Task OpenPrivacyAsync() => OpenDocumentAsync(LegalDocuments.PrivacyKey);

        // ================= Support and legal (Profil → Destek / Yasal ve gizlilik) =================

        /// <summary>Where the "Bize ulaşın" row writes to.</summary>
        public string SupportEmail => AppConstants.SupportEmail;

        /// <summary>Opens a legal document: "privacy", "kvkk", "terms" or "licenses".</summary>
        [RelayCommand]
        private Task OpenDocumentAsync(string? key) => RunSafeAsync(() =>
            Shell.Current.GoToAsync($"{Routes.Privacy}?doc={LegalDocuments.Key(LegalDocuments.FromKey(key))}"));

        /// <summary>
        /// TP-25: withdraws the explicit consents (AI planning, Google sign-in). The features ask again before their
        /// next use; what was transferred earlier stays lawful (and is answered by e-mail on request).
        /// </summary>
        [RelayCommand]
        private Task RevokeConsentAsync() => RunSafeAsync(async () =>
        {
            if (_consent is null || !_consent.HasAnyConsent)
            {
                await Dialogs.ShowMessageAsync(AppResources.Consent_Revoke, AppResources.Consent_NoneGiven);
                return;
            }

            if (!await Dialogs.ConfirmAsync(AppResources.Consent_Revoke, AppResources.Consent_RevokeMessage,
                                            AppResources.Consent_RevokeAccept, AppResources.Common_Cancel))
                return;

            _consent.RevokeAll();
            await Dialogs.ShowMessageAsync(AppResources.Consent_Revoke, AppResources.Consent_Revoked);
        });

        [RelayCommand]
        private Task OpenHelpCenterAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.HelpCenter));

        [RelayCommand]
        private Task ContactSupportAsync() =>
            RunSafeAsync(() => SupportContact.ComposeOrShowAddressAsync(Dialogs, AppResources.Help_MailSubject));

        // ================= G6: trash and joining a shared trip =================

        [RelayCommand]
        private Task OpenTrashAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Trash));

        /// <summary>"Davet koduyla katıl": asks for the code, joins the trip and opens it.</summary>
        [RelayCommand]
        private Task JoinTripAsync() => RunSafeAsync(async () =>
        {
            if (_joinFlow is null || !_account.IsSignedIn) return;
            if (await _joinFlow.RunAsync() is int tripId)
                await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={tripId}");
        }, AppResources.Members_ActionError);

        [RelayCommand]
        private async Task GoBackAsync()
        {
            if (Shell.Current?.Navigation.NavigationStack.Count > 1)
                await Shell.Current.GoToAsync("..");
        }
    }
}
