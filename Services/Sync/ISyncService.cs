namespace TravelPlanner.Services.Sync
{
    public enum SyncStatus
    {
        /// <summary>Guest - nothing to sync with.</summary>
        Disabled,
        /// <summary>No connection; changes wait in the outbox.</summary>
        Offline,
        Syncing,
        /// <summary>Everything is uploaded and the latest changes are in.</summary>
        UpToDate,
        /// <summary>Some changes are still waiting (they failed and will be retried).</summary>
        Pending,
        /// <summary>The last attempt failed for another reason (see the debug log).</summary>
        Error
    }

    /// <summary>What one sync round did.</summary>
    public sealed record SyncResult(int Pushed, int Rejected, int Conflicts, int FilesUploaded, int Pulled, int StillPending);

    /// <summary>
    /// Keeps the signed-in account's local database and the cloud in step (G4): sends the outbox, uploads attachment
    /// files (G5) and applies everyone's changes. Runs a few seconds after a local change, when the connection comes
    /// back, when the app returns to the foreground and every couple of minutes while it is open.
    /// </summary>
    public interface ISyncService
    {
        SyncStatus Status { get; }

        /// <summary>Changes not uploaded yet.</summary>
        int PendingCount { get; }

        DateTime? LastSyncedAtUtc { get; }

        /// <summary>Status / pending count / last sync changed (any thread).</summary>
        event EventHandler? StatusChanged;

        /// <summary>Changes from the cloud were written to the local database (any thread) - pages should reload.</summary>
        event EventHandler? RemoteChangesApplied;

        /// <summary>
        /// G6: the server refused some of this device's changes for good (the row was deleted meanwhile, or the
        /// user's role changed); the argument is how many. The server's version has been restored locally.
        /// </summary>
        event EventHandler<int>? ChangesRejected;

        /// <summary>
        /// TP-3: the server refused some changes or files because a usage limit is reached (e.g. expenses per trip,
        /// storage per trip); the argument is how many were refused for the first time. They stay on this device
        /// and are retried later.
        /// </summary>
        event EventHandler<int>? QuotaExceeded;

        /// <summary>Starts the triggers (timer, connectivity, local changes). Call once the app shell is up.</summary>
        void Start();

        /// <summary>Stops the periodic timer (app in the background).</summary>
        void Stop();

        /// <summary>Schedules a sync after <paramref name="delay"/> (default: a few seconds), merging repeated requests.</summary>
        void RequestSync(TimeSpan? delay = null);

        /// <summary>
        /// A page was opened or pulled to refresh: sync right away, unless a round is running or one finished
        /// moments ago (<see cref="SyncService.RefreshGap"/>), so quick page changes don't flood the server.
        /// </summary>
        void RequestRefresh();

        /// <summary>Runs a sync round now (waits for a running one first).</summary>
        Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default);
    }
}
