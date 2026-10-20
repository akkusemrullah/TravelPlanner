using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// A trip's cover photo: cropped to 16:9 (<see cref="TripCoverService.CoverWidth"/> x <see cref="TripCoverService.CoverHeight"/>
    /// JPEG) with a small square thumbnail for the trip list. Both are kept on the device next to the trip's attachments
    /// and in the cloud (trip-files bucket), so the list shows them at once and the trip's other devices get them too.
    /// </summary>
    public interface ITripCoverService
    {
        /// <summary>
        /// Crops and shrinks a picked photo and keeps the cover and its thumbnail in a staging folder; returns the
        /// new cover's id. Nothing is linked to a trip yet (<see cref="SetAsync"/>), so a form can still be cancelled.
        /// </summary>
        Task<string> PrepareAsync(Stream photo, CancellationToken cancellationToken = default);

        /// <summary>A staged cover's file (for the form's preview), or null.</summary>
        string? StagedFile(string coverId);

        /// <summary>Forgets a staged cover that was not used (form cancelled, another photo picked).</summary>
        void DiscardStaged(string coverId);

        /// <summary>Makes a staged cover the trip's cover: shown at once, uploaded with the next sync.</summary>
        Task SetAsync(Trip trip, string coverId);

        /// <summary>Removes the trip's cover (here, in the cloud with the next sync, and on its other devices).</summary>
        Task RemoveAsync(Trip trip);

        /// <summary>The trip has a cover (one waiting for upload counts).</summary>
        bool HasCover(Trip trip);

        /// <summary>The cover (or its thumbnail) on this device, downloaded first when needed; null when there is none or it can't be had now.</summary>
        Task<string?> GetFileAsync(Trip trip, bool thumbnail, CancellationToken cancellationToken = default);

        /// <summary>Sets <see cref="Trip.CoverThumbFile"/> of each trip: at once when the thumbnail is here, otherwise when it's downloaded.</summary>
        void ShowThumbnails(IEnumerable<Trip> trips);

        /// <summary>Sync: uploads covers picked on this device and records them on their trips. Returns how many.</summary>
        Task<int> UploadPendingAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>Crops and shrinks a photo (platform image code; replaced in tests).</summary>
    public interface ICoverImageProcessor
    {
        /// <summary>
        /// The photo center-cropped to <paramref name="width"/> x <paramref name="height"/> as JPEG. Throws
        /// <see cref="InvalidDataException"/> when it isn't a picture the device can read.
        /// </summary>
        Task<byte[]> CropAsync(Stream photo, int width, int height, float quality);
    }
}
