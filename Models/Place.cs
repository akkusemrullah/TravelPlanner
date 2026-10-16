using SQLite;
using System.ComponentModel.DataAnnotations;

namespace TravelPlanner.Models
{
    public class Place : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        [Required, System.ComponentModel.DataAnnotations.MaxLength(150)]
        public string Name { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(500)]
        public string? Description { get; set; }

        [Range(-90, 90)]
        public double Latitude { get; set; }

        [Range(-180, 180)]
        public double Longitude { get; set; }

        public bool IsVisited { get; set; }

        // Convenience accessor for .NET MAUI Maps (not stored)
        [Ignore]
        public Microsoft.Maui.Devices.Sensors.Location Coordinates => new(Latitude, Longitude);
    }
}