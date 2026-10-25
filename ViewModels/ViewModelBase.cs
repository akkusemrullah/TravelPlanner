using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Shared plumbing for every ViewModel:
    /// - RunSafeAsync: catches any exception from a command/load and shows a Turkish error dialog instead
    ///   of letting it crash the app or vanish silently (TP-106).
    /// - RunOnceAsync: if the same operation is already running, callers get the in-flight Task back
    ///   instead of starting a second copy. This is what stops the "loaded twice" race where
    ///   OnAppearing and a query-property change both kicked off a load and the list ended up with
    ///   duplicate rows (TP-104).
    /// - IsBusy / IsInitialLoading / RunLoadAsync: one consistent loading pattern for every page (TP-309).
    ///   IsBusy drives pull-to-refresh spinners; IsInitialLoading shows a centred spinner only until the
    ///   first load finishes, so empty-state messages don't flash before data arrives.
    /// </summary>
    public abstract class ViewModelBase : ObservableObject
    {
        private readonly Dictionary<string, Task> _inFlight = new();

        protected ViewModelBase(IDialogService dialogs)
        {
            Dialogs = dialogs;
        }

        protected IDialogService Dialogs { get; }

        private bool _isBusy;
        private bool _hasLoadedOnce;

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(IsInitialLoading));
                    if (!value)
                        IsRefreshing = false;
                }
            }
        }

        private bool _isRefreshing;

        /// <summary>
        /// The pull-to-refresh spinner (RefreshView.IsRefreshing, two-way): true only while a load the USER started
        /// by pulling is running. Loads the app starts itself (opening a page, coming back to it, a sync) don't show
        /// it - the spinner popping in and out on every page change made the app feel jumpy.
        /// </summary>
        public bool IsRefreshing
        {
            get => _isRefreshing;
            set => SetProperty(ref _isRefreshing, value);
        }

        /// <summary>True once the page's first load has completed (successfully or not).</summary>
        public bool HasLoadedOnce
        {
            get => _hasLoadedOnce;
            protected set
            {
                if (SetProperty(ref _hasLoadedOnce, value))
                    OnPropertyChanged(nameof(IsInitialLoading));
            }
        }

        public bool IsInitialLoading => IsBusy && !HasLoadedOnce;

        // ---- G6: shared trips ----

        private bool _canEdit = true;

        /// <summary>
        /// False while the open trip is shared with the user as a viewer: add/edit buttons bind to this. Writes are
        /// refused by ChangeTracker anyway; this only keeps the buttons out of the way.
        /// </summary>
        public bool CanEdit
        {
            get => _canEdit;
            protected set
            {
                if (SetProperty(ref _canEdit, value))
                    OnPropertyChanged(nameof(IsReadOnly));
            }
        }

        public bool IsReadOnly => !CanEdit;

        /// <summary>The last result of <see cref="RefreshAccessAsync"/> (null = unknown / unit tests).</summary>
        protected TripAccessInfo? Access { get; private set; }

        /// <summary>Looks up the user's role in <paramref name="tripId"/> and sets <see cref="CanEdit"/>.</summary>
        protected async Task RefreshAccessAsync(int tripId)
        {
            Access = null;
            if (TripAccess.Current is not { } access || tripId <= 0)
            {
                CanEdit = true;
                return;
            }

            try
            {
                Access = await access.GetAccessAsync(tripId);
                CanEdit = Access.CanEdit;
            }
            catch (Exception ex)
            {
                // Never lock the user out because a lookup failed - the write itself is still checked.
                System.Diagnostics.Debug.WriteLine($"[Access] {ex.Message}");
                CanEdit = true;
            }
        }

        /// <summary>RunOnceAsync + IsBusy/HasLoadedOnce bookkeeping, for a page's main data load.</summary>
        protected Task RunLoadAsync(Func<Task> load, string? errorMessage = null, [CallerMemberName] string key = "")
        {
            // Remembered so the page can be reloaded when a sync brings trip mates' changes (ReloadAfterRemoteChangeAsync).
            _lastLoad = () => RunLoadAsync(load, errorMessage, key);

            // Opening a page or pulling to refresh also asks the cloud for news (throttled in SyncService);
            // if anything arrives, App reloads the visible page.
            RemoteChanges.RequestFresh();

            return RunOnceAsync(async () =>
            {
                IsBusy = true;
                try
                {
                    // TP-81: a page that couldn't load offers "Tekrar dene" instead of only "Tamam"
                    while (true)
                    {
                        try
                        {
                            await load();
                            break;
                        }
                        catch (Exception ex) when (ex is not (OperationCanceledException or TripAccessException))
                        {
                            if (!await Dialogs.ShowRetryAsync(errorMessage ?? AppResources.Common_GenericError, ex))
                                break;
                        }
                    }
                }
                finally
                {
                    IsBusy = false;
                    IsRefreshing = false;
                    HasLoadedOnce = true;
                }
            }, errorMessage, key);
        }

        private Func<Task>? _lastLoad;

        /// <summary>
        /// Whether the page reloads by itself when a sync brings changes from the cloud. Forms say no, so a
        /// half-typed edit is never overwritten.
        /// </summary>
        protected virtual bool ReloadOnRemoteChanges => true;

        /// <summary>Re-runs the page's last data load (G6: called by App for the visible page after a sync pulled changes).</summary>
        public Task ReloadAfterRemoteChangeAsync() =>
            ReloadOnRemoteChanges && _lastLoad is { } load ? load() : Task.CompletedTask;

        // ---- Lists: tick rows (or "Tümünü seç") and delete them together ----

        /// <summary>One confirmation for several rows: "<paramref name="countText"/> silinecek… 30 gün çöp kutusunda".</summary>
        protected Task<bool> ConfirmBulkDeleteAsync(string title, string countText) =>
            Dialogs.ConfirmAsync(title, string.Format(AppResources.Selection_DeleteMessageFormat, countText));

        /// <summary>
        /// Deletes <paramref name="items"/> one by one (each goes to the trash on its own, so it can be restored on its
        /// own) and reports every row that is gone, so the list stays right even when a later row fails - the
        /// exception then reaches RunSafeAsync's error message.
        /// </summary>
        protected static async Task DeleteEachAsync<T>(IEnumerable<T> items, Func<T, Task> delete, Action<T> deleted)
        {
            foreach (var item in items.ToList())
            {
                await delete(item);
                deleted(item);
            }
        }

        protected async Task RunSafeAsync(Func<Task> action, string? errorMessage = null)
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                // Superseded/cancelled work is not an error worth showing.
            }
            catch (TripAccessException ex)
            {
                // G6: a viewer (or an editor deleting the trip) - explain instead of "something went wrong".
                await Dialogs.ShowMessageAsync(AppResources.Common_Info,
                    ex.Reason == TripAccessDenied.OwnerOnly ? AppResources.Access_OwnerOnly : AppResources.Access_ReadOnly);
            }
            catch (Exception ex)
            {
                await Dialogs.ShowErrorAsync(errorMessage ?? AppResources.Common_GenericError, ex);
            }
        }

        protected Task RunOnceAsync(Func<Task> action, string? errorMessage = null, [CallerMemberName] string key = "")
        {
            if (_inFlight.TryGetValue(key, out var running) && !running.IsCompleted)
                return running;

            // Register the run BEFORE starting it: the synchronous start of the work can re-enter here (e.g.
            // IsBusy = true makes a RefreshView execute its Command again), and that call must join this run
            // instead of starting a second one (which used to fill lists twice).
            var done = new TaskCompletionSource();
            _inFlight[key] = done.Task;
            CompleteWhenDone(RunSafeAsync(action, errorMessage), done);
            return done.Task;
        }

        private static async void CompleteWhenDone(Task work, TaskCompletionSource done)
        {
            try
            {
                await work;
                done.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }
    }
}
