using SQLite;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace TravelPlanner.Models
{
    public enum AttachmentKind
    {
        Photo = 0,
        Document = 1
    }

    /// <summary>
    /// A ticket, booking, visa PDF or photo attached to a trip (TP-501). The file itself is copied into the
    /// app's own storage (AppDataDirectory/attachments/{tripId}/...), so it stays available offline and even if
    /// the original is deleted from the gallery / downloads folder.
    /// </summary>
    [Table("Attachment")]
    public class TripAttachment : SyncEntityBase, ISyncEntity
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }

        [Indexed]
        public int TripId { get; set; }

        /// <summary>Name shown in the list (the original file name, editable).</summary>
        [Required, System.ComponentModel.DataAnnotations.MaxLength(150)]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Path relative to the app data directory, e.g. "attachments/3/5f0c….pdf".</summary>
        [Required, System.ComponentModel.DataAnnotations.MaxLength(260)]
        public string RelativePath { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(100)]
        public string? ContentType { get; set; }

        public AttachmentKind Kind { get; set; }

        public long SizeBytes { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.Now;

        /// <summary>
        /// Object key of the uploaded file in the cloud ("trips/&lt;trip SyncId&gt;/&lt;SyncId&gt;.pdf"), null until it has been
        /// uploaded (G5). Synced as the server's storage_path, so other devices know where to download it from.
        /// </summary>
        [System.ComponentModel.DataAnnotations.MaxLength(300)]
        public string? RemotePath { get; set; }

        // ---- UI helpers (not stored) ----

        /// <summary>Absolute path on this device - filled in by AttachmentService.</summary>
        [Ignore]
        public string FullPath { get; set; } = string.Empty;

        [Ignore]
        public bool IsPhoto => Kind == AttachmentKind.Photo;

        [Ignore]
        public bool IsDocument => Kind == AttachmentKind.Document;

        /// <summary>"PDF", "JPG" … - shown on the document tile.</summary>
        [Ignore]
        public string Extension => Path.GetExtension(RelativePath).TrimStart('.').ToUpperInvariant();

        /// <summary>"1,4 MB" / "820 KB".</summary>
        [Ignore]
        public string SizeDisplay => FormatSize(SizeBytes);

        public static string FormatSize(long bytes)
        {
            var culture = CultureInfo.CurrentCulture;
            if (bytes >= 1024 * 1024)
                return (bytes / 1024d / 1024d).ToString("0.#", culture) + " MB";
            if (bytes >= 1024)
                return (bytes / 1024d).ToString("0", culture) + " KB";
            return bytes.ToString(culture) + " B";
        }
    }
}
