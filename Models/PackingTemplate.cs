using SQLite;
using System.ComponentModel.DataAnnotations;

namespace TravelPlanner.Models
{
    /// <summary>
    /// A reusable packing list saved by the user (TP-405), e.g. "Kış tatili" or "İş seyahati". Not tied to a
    /// trip: applying it to a trip adds its items to that trip's to-do list (skipping ones already there).
    /// </summary>
    public class PackingTemplate : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(80)]
        [Indexed]
        public string Name { get; set; } = string.Empty;

        /// <summary>Items, one per line.</summary>
        public string Items { get; set; } = string.Empty;

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        [Ignore]
        public IReadOnlyList<string> ItemList =>
            Items.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        [Ignore]
        public string Display => $"{Name} ({ItemList.Count})";
    }
}
