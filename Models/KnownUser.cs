using SQLite;

namespace TravelPlanner.Models
{
    /// <summary>
    /// Names of the people the user shares trips with (G6), remembered from the members list so "son düzenleyen"
    /// can show a name also offline. Per account database.
    /// </summary>
    [Table("KnownUser")]
    public class KnownUser
    {
        [PrimaryKey, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string UserId { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(80)]
        public string? DisplayName { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(254)]
        public string? Email { get; set; }
    }
}
