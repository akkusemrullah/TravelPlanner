using System.ComponentModel;
using System.Text.Json.Serialization;
using SQLite;

namespace TravelPlanner.Models
{
    /// <summary>
    /// A row that is backed up to the cloud and synced between devices and trip members (G1).
    /// Locally every table keeps its own integer <c>Id</c> (all relations - TripId, ExpenseId, PlaceId … - stay
    /// integers); <see cref="SyncId"/> is the row's global identity that the server knows it by.
    /// Never write these rows with <c>Insert/Update/Delete</c> directly - go through
    /// <see cref="Services.Sync.ChangeTracker"/>, which keeps the metadata, the outbox and the trash in step.
    /// </summary>
    /// <summary>
    /// A row a list can tick in its "Seçerek sil" mode (<see cref="ViewModels.ListSelection{T}"/>): every synced model,
    /// and rows that aren't models themselves (the trash list).
    /// </summary>
    public interface ISelectableRow
    {
        /// <summary>Stable identity of the row while the list is shown (the selection is kept by it).</summary>
        int Id { get; }

        /// <summary>Ticked in selection mode; must raise PropertyChanged so the row's tick updates.</summary>
        bool IsSelected { get; set; }
    }

    public interface ISyncEntity : ISelectableRow
    {
        new int Id { get; set; }

        /// <summary>Global identity (Guid, "d" format). Assigned on first insert, never changes.</summary>
        string SyncId { get; set; }

        /// <summary>Account id (Supabase auth uid) of whoever created the row. Null = created as guest / before accounts.</summary>
        string? CreatedBy { get; set; }

        /// <summary>Account id of whoever changed the row last.</summary>
        string? UpdatedBy { get; set; }

        /// <summary>Last local change (UTC). Display only ("son düzenleme") - never used to order changes.</summary>
        DateTime UpdatedAtUtc { get; set; }

        /// <summary>Server row version this copy is based on (0 = the server has never seen it).</summary>
        long Version { get; set; }

        /// <summary>Server change sequence of this copy (0 = never synced). Set only by the sync engine.</summary>
        long ChangeSeq { get; set; }
    }

    /// <summary>Stored sync metadata shared by every synced model (see <see cref="ISyncEntity"/>).</summary>
    public abstract class SyncEntityBase : INotifyPropertyChanged
    {
        [Indexed, System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string SyncId { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? CreatedBy { get; set; }

        [System.ComponentModel.DataAnnotations.MaxLength(36)]
        public string? UpdatedBy { get; set; }

        public DateTime UpdatedAtUtc { get; set; }

        public long Version { get; set; }

        public long ChangeSeq { get; set; }

        // ---- UI only (not stored, not backed up, not synced) ----

        private bool _isSelected;

        /// <summary>
        /// Ticked in a list's "Seçerek sil" mode (see <see cref="ViewModels.ListSelection{T}"/>). The only property
        /// that raises <see cref="PropertyChanged"/>, so the row's check mark updates without rebuilding the row.
        /// </summary>
        [Ignore, JsonIgnore]
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>For the few UI-only properties that change while a row is shown (e.g. a trip's cover thumbnail).</summary>
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
