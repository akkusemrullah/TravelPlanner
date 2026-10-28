using TravelPlanner.Helpers;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner
{
    public partial class App : Application
    {
        private readonly INotificationService _notificationService;
        private readonly ITrashService _trashService;
        private readonly IAccountService _account;
        private readonly IPreferences _preferences;
        private readonly ISyncService _sync;
        private readonly IDialogService _dialogs;
        private readonly ITripService _trips;
        private readonly IMemberJoinWatcher _memberJoins;
        private readonly IAiPlanJobs _aiJobs;
        private readonly AppLifecycle _lifecycle;
        private bool _offeringAiPlan;
        private bool _shellShown;

        // Reminders mention the day's activities and the open to-dos, so they are re-planned when those change
        // (debounced per trip: deleting ten to-dos re-plans once), and all upcoming trips now and then.
        private readonly Dictionary<int, CancellationTokenSource> _pendingReminderRefresh = new();
        private CancellationTokenSource? _pendingRefreshAll;
        private DateTime _lastRemindersRefresh = DateTime.MinValue;
        private static readonly TimeSpan RemindersRefreshInterval = TimeSpan.FromHours(6);

        // Guards against asking twice - we only want the permission prompt once per process.
        private bool _notificationPermissionRequested;

        // G3: restores the session and opens that account's (or the guest's) database while the splash plays.
        private Task? _accountStartup;

        public App(INotificationService notificationService, ISettingsService settings, IThemeService themeService,
                   ITrashService trashService, IAccountService account, IPreferences preferences, ISyncService sync,
                   ITripAccessService tripAccess, IDialogService dialogs, ITripService trips, IMemberJoinWatcher memberJoins,
                   IAiPlanJobs aiJobs, AppLifecycle lifecycle)
        {
            _lifecycle = lifecycle;
            // Language first: every page reads its {x:Static} strings when it's built (TP-301).
            LocalizationHelper.Apply(settings.Language);

            InitializeComponent();

            // TP-79: the keyboard shrinks the page instead of covering it, so a field low on a form (the trip's budget)
            // can be scrolled into view above it.
            Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.Application.SetWindowSoftInputModeAdjust(
                this, Microsoft.Maui.Controls.PlatformConfiguration.AndroidSpecific.WindowSoftInputModeAdjust.Resize);

            _notificationService = notificationService;
            _trashService = trashService;
            _account = account;
            _preferences = preferences;
            _sync = sync;
            _dialogs = dialogs;
            _trips = trips;
            _memberJoins = memberJoins;
            _aiJobs = aiJobs;

            // TP-109: after a language change every page is built again, in the new language
            LocalizationHelper.ReopenPages = ReopenAfterLanguageChangeAsync;

            // TP-57: an AI plan got ready while its page was closed and no notification could be shown
            _aiJobs.ReadyUnnoticed += (_, _) => MainThread.BeginInvokeOnMainThread(() => OfferReadyAiPlanAsync().FireAndForget());

            // G4: a sync that brought other people's changes reloads the visible page.
            _sync.RemoteChangesApplied += (_, _) =>
            {
                RemoteChanges.Publish();
                // a round often ends right after a page was opened: let its transition finish before reloading it
                MainThread.BeginInvokeOnMainThread(() => Dispatcher.DispatchDelayed(PageLoading.TransitionDelay, ReloadVisiblePage));
                // trip mates may have changed a plan or the to-dos the reminders talk about
                MainThread.BeginInvokeOnMainThread(QueueAllRemindersRefresh);
            };

            TripPlanChanges.Changed += tripId => MainThread.BeginInvokeOnMainThread(() => QueueReminderRefresh(tripId));

            // Opening a page or pulling to refresh checks the cloud too (throttled), so trip mates' changes
            // show up without pressing "Eşitle".
            RemoteChanges.FreshDataRequested = () =>
            {
                if (_shellShown)
                    _sync.RequestRefresh();
            };

            OnboardingTour.Presenter = ShowTourAsync;

            // G6: every page can ask whether the open trip is read-only (ViewModelBase.RefreshAccessAsync).
            TripAccess.Current = tripAccess;

            // G6: tell the user when the server refused some of their offline changes (deleted meanwhile / role changed);
            // TP-6: which records were rolled back, and why.
            _sync.ChangesRejected += (_, rejected) => MainThread.BeginInvokeOnMainThread(() =>
                dialogs.ShowMessageAsync(TravelPlanner.Resources.Strings.AppResources.Common_Info,
                    SyncRejectionMessage.Format(rejected)).FireAndForget());

            // TP-67: ... or merged them with someone else's edit of the same record.
            _sync.ChangesMerged += (_, merged) => MainThread.BeginInvokeOnMainThread(() =>
                dialogs.ShowMessageAsync(TravelPlanner.Resources.Strings.AppResources.Common_Info,
                    SyncRejectionMessage.Format(merged)).FireAndForget());

            // TP-3: ... or kept them back because a usage limit is reached (told once per change).
            _sync.QuotaExceeded += (_, count) => MainThread.BeginInvokeOnMainThread(() =>
                dialogs.ShowMessageAsync(TravelPlanner.Resources.Strings.AppResources.Common_Info,
                    string.Format(TravelPlanner.Resources.Strings.AppResources.Sync_QuotaExceededFormat, count)).FireAndForget());

            // 28: a shared trip vanished from this device (deleted by its owner, or the user was taken off it): say so.
            _sync.SharedTripsRemoved += (_, names) => MainThread.BeginInvokeOnMainThread(() =>
                dialogs.ShowMessageAsync(TravelPlanner.Resources.Strings.AppResources.Common_Info,
                    SyncRejectionMessage.FormatLostTrips(names)).FireAndForget());

            // TP-4: tell the owner when someone joins one of their trips (asked after a sync, at most every 2 minutes).
            _sync.StatusChanged += (_, _) =>
            {
                if (_shellShown && _sync.Status == SyncStatus.UpToDate)
                    CheckMemberJoinsAsync().FireAndForget();
            };

            // Signing in / out switches the local data: rebuild the shell so every page reloads from the new data.
            _account.AccountChanged += (_, _) => MainThread.BeginInvokeOnMainThread(() => RebuildShellAsync().FireAndForget());

            // Light/dark colour tokens, following the saved choice or the OS (TP-303).
            themeService.Initialize();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            // The animated splash (Views/SplashPage) runs first; when it's done it calls ShowMainShell, which
            // swaps in the AppShell and only then asks for the notification permission.
            _accountStartup ??= InitializeAccountAsync();
            return new Window(new Views.SplashPage());
        }

        // Whatever page is on screen (tab, pushed page or modal) reloads its data after a sync pulled changes.
        private static void ReloadVisiblePage()
        {
            try
            {
                var shell = Shell.Current;
                var page = shell?.Navigation.ModalStack.LastOrDefault() ?? shell?.CurrentPage;
                if (page?.BindingContext is ViewModels.ViewModelBase viewModel)
                    viewModel.ReloadAfterRemoteChangeAsync().FireAndForget();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Reloading the visible page failed: {ex.Message}");
            }
        }

        private async Task RebuildShellAsync()
        {
            // Let the sign-in page finish closing itself first.
            await Task.Delay(400);

            var window = Windows.FirstOrDefault();
            if (window?.Page is not AppShell shell)
                return;

            while (shell.Navigation.ModalStack.Count > 0)
                await shell.Navigation.PopModalAsync(false);

            window.Page = new AppShell();
        }

        /// <summary>
        /// TP-109: open pages keep the texts, dates and the weather they were built with, so after a language change
        /// the shell is built again (like after signing in) and Profile is shown, now in the new language.
        /// </summary>
        private async Task<bool> ReopenAfterLanguageChangeAsync()
        {
            var window = Windows.FirstOrDefault();
            if (window?.Page is not AppShell old)
                return false;

            while (old.Navigation.ModalStack.Count > 0)
                await old.Navigation.PopModalAsync(false);

            var shell = new AppShell();
            window.Page = shell;
            try
            {
                await shell.GoToAsync("//Settings");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Language] Opening Profile failed: {ex.Message}");
            }
            return true;
        }

        private async Task InitializeAccountAsync()
        {
            try
            {
                await _account.InitializeAsync();
            }
            catch (Exception ex)
            {
                // Never stuck on the splash: worst case the app opens on the guest data.
                System.Diagnostics.Debug.WriteLine($"Account start-up failed: {ex}");
            }
        }

        /// <summary>Called by the splash screen once its animation has finished.</summary>
        internal void ShowMainShell(Window? window) => ShowMainShellAsync(window).FireAndForget();

        private async Task ShowMainShellAsync(Window? window)
        {
            if (_accountStartup is not null)
                await _accountStartup; // the right database must be open before the first page loads

            window ??= Windows.FirstOrDefault();
            if (window is null || window.Page is AppShell)
                return;

            window.Page = new AppShell();

#if ANDROID
            // Back from the splash's royal blue to the app's surface colour for the system bars.
            SystemBars.Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, RequestedTheme == AppTheme.Dark);
#endif

            // TP-93: the tour first (once), then the notification question - not both on screen at the same time
            ShowTourOnceThenAskNotificationsAsync().FireAndForget();
            PurgeTrashAsync().FireAndForget();

            // G7: an account deleted or banned in Supabase is signed out here, before syncing starts.
            _shellShown = true;
            await CheckAccountAsync();

            // G4: start syncing (does nothing for a guest; starts by itself after sign-in).
            _sync.Start();

            // Day reminders are only scheduled 30 days ahead, and older versions scheduled different ones.
            RefreshAllRemindersAsync().FireAndForget();

            // TP-57: an AI plan made while the app was closed
            _aiJobs.ResumeAsync().FireAndForget();
            OfferReadyAiPlanAsync().FireAndForget();
        }

        protected override void OnResume()
        {
            base.OnResume();
            _lifecycle.RaiseResumed();
            _aiJobs.InForeground = true;
            if (_shellShown)
                ResumeAsync().FireAndForget();
        }

        private async Task ResumeAsync()
        {
            await CheckAccountAsync(); // banned / deleted while the app was in the background?
            _sync.Start();             // fetch what changed while the app was in the background

            // TP-57: the AI plan went on while the app was away (also after a tap on "Planın hazır")
            _aiJobs.ResumeAsync().FireAndForget();
            OfferReadyAiPlanAsync().FireAndForget();

            if (DateTime.Now - _lastRemindersRefresh > RemindersRefreshInterval)
                await RefreshAllRemindersAsync();
        }

        /// <summary>
        /// G7: asks the server whether the signed-in account still exists and isn't banned. If it was deleted or
        /// banned (or its session revoked), the account service has already signed out and switched to the guest
        /// data; here the user is told why - on the rebuilt shell, hence the short wait. Never blocks the start for
        /// long: offline the check is skipped, and it gives up after a few seconds.
        /// </summary>
        private async Task CheckAccountAsync()
        {
            if (!_account.IsSignedIn)
                return;

            AccountEndReason reason;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                reason = await _account.CheckAccountAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Account] Status check failed: {ex.Message}");
                return;
            }

            var message = reason switch
            {
                AccountEndReason.Deleted => TravelPlanner.Resources.Strings.AppResources.Account_EndedDeleted,
                AccountEndReason.Banned => TravelPlanner.Resources.Strings.AppResources.Account_EndedBanned,
                AccountEndReason.SessionExpired => TravelPlanner.Resources.Strings.AppResources.Account_EndedExpired,
                _ => null,
            };
            if (message is null)
                return;

            ShowAccountEndedAsync(message).FireAndForget();
        }

        private async Task ShowAccountEndedAsync(string message)
        {
            await Task.Delay(900); // the shell is rebuilt for the guest data first
            await MainThread.InvokeOnMainThreadAsync(() =>
                _dialogs.ShowMessageAsync(TravelPlanner.Resources.Strings.AppResources.Account_EndedTitle, message));
        }

        /// <summary>TP-4: "Ayşe joined Roma" - as a notification, or in the app when notifications are off.</summary>
        private async Task CheckMemberJoinsAsync()
        {
            try
            {
                var joins = await _memberJoins.CheckAsync();
                if (joins.Count == 0)
                    return;

                var (title, message) = MemberJoinWatcher.BuildMessage(joins);
                if (await _notificationService.ShowMemberJoinedAsync(title, message))
                    return;

                await MainThread.InvokeOnMainThreadAsync(() => _dialogs.ShowMessageAsync(title, message));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sharing] New-member check failed: {ex.Message}");
            }
        }

        protected override void OnSleep()
        {
            base.OnSleep();
            _lifecycle.RaiseSleeping();
            _sync.Stop();
            _aiJobs.InForeground = false; // a plan getting ready now is told by a notification
        }

        /// <summary>
        /// TP-57: an AI plan is ready and the user hasn't seen it (made while the app was closed, or its page was
        /// left): "Roma için AI planı hazır. Görmek ister misin?" - once per plan.
        /// </summary>
        private async Task OfferReadyAiPlanAsync()
        {
            var job = _aiJobs.Current;
            if (_offeringAiPlan || job is not { Phase: AiPlanJobPhase.Ready, Offered: false } || _aiJobs.WatchedTripId == job.TripId)
                return;

            _offeringAiPlan = true;
            try
            {
                await Task.Delay(800); // after the first page
                if (await _trips.GetByIdAsync(job.TripId) is null)
                {
                    _aiJobs.Dismiss(job.TripId); // the trip is gone
                    return;
                }
                _aiJobs.MarkOffered();
                var show = await MainThread.InvokeOnMainThreadAsync(() => _dialogs.ConfirmAsync(
                    TravelPlanner.Resources.Strings.AppResources.AiPlan_ReadyTitle,
                    string.Format(TravelPlanner.Resources.Strings.AppResources.AiPlan_ReadyAsk, job.TripName),
                    TravelPlanner.Resources.Strings.AppResources.AiPlan_ReadyShow,
                    TravelPlanner.Resources.Strings.AppResources.AiPlan_ReadyLater));
                if (show)
                    await MainThread.InvokeOnMainThreadAsync(() =>
                        Shell.Current?.GoToAsync($"{Routes.AiPlanner}?TripId={job.TripId}") ?? Task.CompletedTask);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Offering the ready plan failed: {ex.Message}");
            }
            finally
            {
                _offeringAiPlan = false;
            }
        }

        // G1: deleted items older than 30 days (and their attachment files) are removed for good.
        private async Task PurgeTrashAsync()
        {
            try
            {
                await _trashService.PurgeExpiredAsync();
                // TP-72: backup zips shared earlier shouldn't stay in the cache
                Services.BackupService.DeleteOldExports(FileSystem.CacheDirectory, TimeSpan.FromHours(1));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Trash purge failed: {ex}");
            }
        }

        // ---- Reminders (Services/TripReminderPlanner) ----

        private void QueueReminderRefresh(int tripId)
        {
            if (_pendingReminderRefresh.Remove(tripId, out var previous))
                previous.Cancel();

            var pending = new CancellationTokenSource();
            _pendingReminderRefresh[tripId] = pending;
            RefreshTripRemindersAsync(tripId, pending).FireAndForget();
        }

        private async Task RefreshTripRemindersAsync(int tripId, CancellationTokenSource pending)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), pending.Token);
            }
            catch (OperationCanceledException)
            {
                return; // a newer change of the same trip is queued
            }

            if (_pendingReminderRefresh.TryGetValue(tripId, out var current) && ReferenceEquals(current, pending))
                _pendingReminderRefresh.Remove(tripId);

            try
            {
                var trip = await _trips.GetByIdAsync(tripId);
                if (trip is null)
                    return; // deleted - TripService already cancelled its reminders

                _notificationService.CancelTripNotifications(trip.Id);
                await _notificationService.ScheduleTripNotificationsAsync(trip);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Reminders] Re-planning trip {tripId} failed: {ex.Message}");
            }
        }

        private void QueueAllRemindersRefresh()
        {
            _pendingRefreshAll?.Cancel();
            var pending = _pendingRefreshAll = new CancellationTokenSource();
            DelayedRefreshAllAsync(pending.Token).FireAndForget();
        }

        private async Task DelayedRefreshAllAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshAllRemindersAsync();
        }

        /// <summary>Re-plans the reminders of every trip that hasn't finished yet (finished trips have none left).</summary>
        private async Task RefreshAllRemindersAsync()
        {
            _lastRemindersRefresh = DateTime.Now;
            try
            {
                foreach (var trip in await _trips.GetAllAsync())
                {
                    if (trip.EndDate.Date.AddDays(1) < DateTime.Today)
                        continue;

                    _notificationService.CancelTripNotifications(trip.Id);
                    await _notificationService.ScheduleTripNotificationsAsync(trip);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Reminders] Re-planning failed: {ex.Message}");
            }
        }

        // Asked once, after the splash: by then the Activity and its native window are long up. (Asking from
        // MauiProgram/OnStart was too early - on some devices the prompt never showed or the first frame froze -
        // so the old code waited for Window.Created; the end of the splash is later still.)
        private async Task ShowTourOnceThenAskNotificationsAsync()
        {
            if (OnboardingTour.ShouldShow(_preferences))
            {
                await Task.Delay(300); // let the first tab render under it
                await ShowTourAsync();
            }
            await RequestNotificationPermissionAsync();
        }

        /// <summary>TP-93: the tour over whatever is on screen (also from Profil → "Tanıtımı tekrar göster").</summary>
        private async Task ShowTourAsync()
        {
            try
            {
                var shell = Shell.Current;
                var page = shell?.Navigation.ModalStack.LastOrDefault() ?? shell?.CurrentPage;
                if (page is not null)
                    await MainThread.InvokeOnMainThreadAsync(() => Views.OnboardingPage.ShowAsync(page.Navigation, _preferences));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Onboarding] Showing the tour failed: {ex.Message}");
            }
        }

        private async Task RequestNotificationPermissionAsync()
        {
            if (_notificationPermissionRequested)
                return;
            _notificationPermissionRequested = true;

            try
            {
                // Let the first Shell page render before the OS dialog takes focus.
                await Task.Delay(400);
                await _notificationService.RequestPermissionAsync();
            }
            catch (Exception ex)
            {
                // Never let a platform hiccup here take the app down - notifications simply stay unavailable
                // until the user grants permission some other way (e.g. from OS settings).
                System.Diagnostics.Debug.WriteLine($"Notification permission request failed: {ex}");
            }
        }
    }
}
