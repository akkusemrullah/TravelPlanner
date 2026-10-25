using SQLite;

namespace TravelPlanner.Models
{
    /// <summary>
    /// A deleted row, kept for <see cref="Services.TrashService.RetentionDays"/> days so it can be restored (G1).
    /// Rows deleted by one user action share a <see cref="BatchId"/> (deleting a trip puts the trip and everything
    /// in it into one batch) and are restored together. The row itself is stored as JSON, exactly as it was.
    /// </summary>
    [Table("Trash")]
    public class TrashItem
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string BatchId { get; set; } = string.Empty;

        /// <summary>True for the row the user actually deleted (the trip, the expense …), false for rows deleted with it.</summary>
        public bool IsRoot { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(40)]
        public string EntityType { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string EntitySyncId { get; set; } = string.Empty;

        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? TripSyncId { get; set; }

        /// <summary>What the trash list shows (trip name, expense description …).</summary>
        [System.ComponentModel.DataAnnotations.MaxLength(150)]
        public string? Title { get; set; }

        /// <summary>The deleted row, serialized with the backup's JSON options.</summary>
        public string Json { get; set; } = string.Empty;

        [Indexed]
        public DateTime DeletedAtUtc { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? DeletedBy { get; set; }

        /// <summary>
        /// TP-68: for a deleted participant, the SyncIds of the expenses they had paid (comma-separated). Deleting
        /// the participant clears the payer of those expenses; restoring them sets it back.
        /// </summary>
        public string? PaidExpenseSyncIds { get; set; }
    }
}
