namespace TravelPlanner.Services
{
    /// <summary>What a restore brought back.</summary>
    public sealed record BackupImportResult(int Trips, int Expenses, int Attachments, DateTime ExportedAt,
        int SkippedSharedTrips = 0, int CopiedSharedTrips = 0);

    /// <summary>
    /// TP-10: what to do with the shared trips in a backup restored by a signed-in user. Restoring them as they are
    /// would overwrite the other members' later edits and bring back rows they deleted.
    /// </summary>
    public enum SharedTripRestoreChoice
    {
        /// <summary>Leave them out; the current cloud copy comes back with the next sync.</summary>
        Skip,
        /// <summary>Restore them as new trips of the user's own (new SyncIds), next to the cloud copy.</summary>
        Copy,
        /// <summary>Restore nothing.</summary>
        Cancel,
    }

    /// <summary>The shared trips found in a backup (their names, for the question shown to the user).</summary>
    public sealed record SharedTripsInBackup(IReadOnlyList<string> TripNames);

    /// <summary>Full-data backup and restore (TP-502). Runs entirely on the device - no cloud service.</summary>
    public interface IBackupService
    {
        /// <summary>
        /// Writes every table as JSON plus the attachment files into a .zip in the cache folder and returns its
        /// path (the UI then hands it to the share sheet, so the user can save it to Drive, Files, e-mail…).
        /// </summary>
        Task<string> ExportAsync();

        /// <summary>
        /// Replaces ALL app data with the backup in <paramref name="content"/> (.zip from ExportAsync, or its bare
        /// .json). Throws <see cref="InvalidBackupException"/> when the file isn't a TravelPlanner backup.
        /// TP-10: for a signed-in user whose backup contains shared trips, <paramref name="decideSharedTrips"/> is
        /// asked what to do with them before anything is replaced (no callback = skip them);
        /// <see cref="SharedTripRestoreChoice.Cancel"/> throws <see cref="OperationCanceledException"/>.
        /// </summary>
        Task<BackupImportResult> ImportAsync(Stream content, string fileName,
            Func<SharedTripsInBackup, Task<SharedTripRestoreChoice>>? decideSharedTrips = null);
    }

    public sealed class InvalidBackupException : Exception
    {
        public InvalidBackupException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
