using SQLite;

namespace TravelPlanner.Models
{
    public enum ChangeOperation
    {
        /// <summary>Create or update. <see cref="PendingChange.ChangedFields"/> lists what changed (null = all fields).</summary>
        Upsert = 0,

        /// <summary>The row was deleted locally.</summary>
        Delete = 1
    }

    /// <summary>
    /// The offline outbox (G1): one row per local entity that has changes the server hasn't seen yet. Written in
    /// the same SQLite transaction as the change itself, so a change can never be saved without being queued.
    /// Several edits of the same row before the next sync are merged into one entry (field lists are unioned).
    /// The sync engine (G4) sends entries in <see cref="Sequence"/> order - parents are always queued before
    /// their children - and removes each entry once the server has accepted it.
    /// </summary>
    public class PendingChange
    {
        /// <summary>Queue order.</summary>
        [PrimaryKey, AutoIncrement]
        public int Sequence { get; set; }

        /// <summary>Table name, e.g. "Trip", "Expense" (see <see cref="Services.Sync.SyncSchema"/>).</summary>
        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(40)]
        public string EntityType { get; set; } = string.Empty;

        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string EntitySyncId { get; set; } = string.Empty;

        /// <summary>SyncId of the trip the row belongs to (null for personal data such as packing templates).</summary>
        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? TripSyncId { get; set; }

        public ChangeOperation Operation { get; set; }

        /// <summary>Comma-separated property names that changed; null = every field (new row / restored row).</summary>
        public string? ChangedFields { get; set; }

        /// <summary>The row's <see cref="ISyncEntity.Version"/> when it was first changed offline (conflict detection).</summary>
        public long BaseVersion { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        /// <summary>Failed push attempts (G4 backs off on these).</summary>
        public int Attempts { get; set; }

        public string? LastError { get; set; }

        /// <summary>When the last failed push happened (G4 waits before retrying a change that keeps failing).</summary>
        public DateTime? LastAttemptAtUtc { get; set; }

        [Ignore]
        public IReadOnlyList<string>? ChangedFieldList =>
            ChangedFields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
