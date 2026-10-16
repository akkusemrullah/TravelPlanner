using SQLite;
using System.ComponentModel.DataAnnotations;

namespace TravelPlanner.Models
{
    /// <summary>Someone travelling on the trip who shares costs (TP-504).</summary>
    public class Participant : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(60)]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// The trip member's account when this participant is one (G6 links them); null for someone without an
        /// account, who can still share costs.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? UserId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// "This participant takes part in splitting this expense" (TP-504). Shares are always equal and computed
    /// from the expense amount when needed, so they can never go stale when an amount is edited or the trip's
    /// currency is converted.
    /// </summary>
    public class ExpenseShare : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int ExpenseId { get; set; }

        [Indexed]
        public int ParticipantId { get; set; }
    }
}
