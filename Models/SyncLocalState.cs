using SQLite;

namespace TravelPlanner.Models
{
    /// <summary>Small key/value store for the sync engine, per account database (G4): the pull cursor, last sync time.</summary>
    [Table("SyncState")]
    public class SyncStateEntry
    {
        [PrimaryKey, System.ComponentModel.DataAnnotations.MaxLength(40)]
        public string Key { get; set; } = string.Empty;

        public string? Value { get; set; }
    }

    /// <summary>
    /// The signed-in user's role in each trip they can see, as the server last reported it (G4). Trips the user
    /// created offline and that were never uploaded have no row yet - they are the user's own.
    /// </summary>
    [Table("TripMembership")]
    public class TripMembership
    {
        [PrimaryKey, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string TripSyncId { get; set; } = string.Empty;

        /// <summary>"owner", "editor" or "viewer".</summary>
        [System.ComponentModel.DataAnnotations.MaxLength(10)]
        public string Role { get; set; } = string.Empty;
    }
}
