using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>Trip attachments (TP-501): files are copied into app storage so they work offline.</summary>
    public interface IAttachmentService
    {
        /// <summary>Root folder attachments live under (the app data directory, or the account's folder - G3).</summary>
        string RootDirectory { get; }

        /// <summary>Switches to another root folder (guest ↔ account, see AccountService).</summary>
        void UseRootDirectory(string rootDirectory);

        Task<List<TripAttachment>> GetByTripIdAsync(int tripId);

        /// <summary>
        /// Copies <paramref name="content"/> into app storage and records it. The kind (photo/document) is derived
        /// from the content type / extension.
        /// </summary>
        Task<TripAttachment> AddAsync(int tripId, Stream content, string originalFileName, string? contentType);

        Task RenameAsync(TripAttachment attachment, string displayName);

        /// <summary>
        /// Deletes the record into the trash (G1) and moves the stored file to the trash folder, so the attachment
        /// can be restored for 30 days.
        /// </summary>
        Task DeleteAsync(TripAttachment attachment);

        /// <summary>Removes a trip's attachment folder (the rows are deleted by TripService's transaction).</summary>
        void DeleteFilesForTrip(int tripId);

        /// <summary>Moves a deleted attachment's file into the trash folder (G1). Missing files are ignored.</summary>
        void MoveFileToTrash(TripAttachment attachment);

        /// <summary>Moves a restored attachment's file back from the trash folder. False when it is not there.</summary>
        bool RestoreFileFromTrash(TripAttachment attachment);

        /// <summary>Deletes a trashed attachment's file for good (trash purge).</summary>
        void DeleteTrashedFile(TripAttachment attachment);

        /// <summary>
        /// Makes sure the file is on this device: true when it already is, otherwise downloads it from the cloud
        /// (an attachment added on another device - G5). False when it can't be had right now (offline, not uploaded yet).
        /// </summary>
        Task<bool> EnsureLocalFileAsync(TripAttachment attachment, CancellationToken cancellationToken = default);

        /// <summary>Fills in <see cref="TripAttachment.FullPath"/> for a loaded record.</summary>
        string GetFullPath(TripAttachment attachment);
    }
}
