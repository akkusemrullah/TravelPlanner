namespace TravelPlanner.Services
{
    /// <summary>One thing the user deleted (a trip with everything in it, an expense …), as the trash list shows it.</summary>
    public sealed record TrashEntry(
        string BatchId,
        string EntityType,
        string? Title,
        string? TripSyncId,
        DateTime DeletedAtUtc,
        string? DeletedBy,
        int ItemCount)
    {
        /// <summary>When it is purged for good.</summary>
        public DateTime ExpiresAtUtc => DeletedAtUtc.AddDays(TrashService.RetentionDays);
    }

    /// <summary>
    /// The 30-day trash (G1): deleted rows are kept here and can be restored with their original identity.
    /// Implemented by TrashService. The trash screen itself comes with trip sharing (G6).
    /// </summary>
    public interface ITrashService
    {
        /// <summary>Deleted items, newest first; only those of one trip when <paramref name="tripSyncId"/> is given.</summary>
        Task<List<TrashEntry>> GetEntriesAsync(string? tripSyncId = null);

        /// <summary>
        /// Restores everything deleted together with <paramref name="batchId"/> (and queues it for sync).
        /// Rows whose parent no longer exists (e.g. an expense whose trip was deleted afterwards) are skipped.
        /// Returns how many rows came back.
        /// </summary>
        Task<int> RestoreAsync(string batchId);

        /// <summary>Removes items older than <see cref="TrashService.RetentionDays"/> days (and their files). Returns how many.</summary>
        Task<int> PurgeExpiredAsync();
    }
}
