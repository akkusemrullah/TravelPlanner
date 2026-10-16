using SQLite;
using System.ComponentModel.DataAnnotations;

namespace TravelPlanner.Models
{
    [Table("Todo")]
    public class TodoItem : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(150)]
        public string Title { get; set; } = string.Empty;

        public bool IsCompleted { get; set; }

        /// <summary>Account (Supabase auth uid) this item is assigned to on a shared trip; null = nobody / everyone.</summary>
        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? AssignedToUserId { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }
}