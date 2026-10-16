namespace TravelPlanner.Services
{
    /// <summary>What a restore brought back.</summary>
    public sealed record BackupImportResult(int Trips, int Expenses, int Attachments, DateTime ExportedAt);

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
        /// </summary>
        Task<BackupImportResult> ImportAsync(Stream content, string fileName);
    }

    public sealed class InvalidBackupException : Exception
    {
        public InvalidBackupException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
