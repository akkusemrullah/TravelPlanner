using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Trip cover photos (see <see cref="ITripCoverService"/>). Where the files are:
    /// <list type="bullet">
    /// <item>device: "attachments/&lt;trip id&gt;/cover-&lt;id&gt;.jpg" and "…-thumb.jpg", next to the trip's attachments, so
    /// they go when the trip goes; a photo picked in a form waits in "attachments/covers-staged" until it is saved;</item>
    /// <item>cloud: "trips/&lt;trip SyncId&gt;/cover-&lt;id&gt;.jpg" and "…-thumb.jpg" in the trip-files bucket
    /// (supabase/migrations/…32_trip_cover.sql). <see cref="Trip.CoverPath"/> names it.</item>
    /// </list>
    /// Every new photo gets a new id, so a file on a device is right as long as its name matches - nothing to compare.
    /// A cover picked here is shown at once (<see cref="Trip.PendingCoverId"/>); the sync uploads both files and only
    /// then sets <see cref="Trip.CoverPath"/>, so other devices never look for a file that isn't there yet. Other devices
    /// download the thumbnail when the list shows the trip, the cover when the trip is opened.
    /// </summary>
    public class TripCoverService : ITripCoverService
    {
        public const int CoverWidth = 1280;
        public const int CoverHeight = 720;
        public const int ThumbSize = 256;
        private const float CoverQuality = 0.82f;
        private const float ThumbQuality = 0.8f;

        /// <summary>Photos larger than this aren't read (the picker already shrinks them; this guards odd files).</summary>
        public const long MaxPhotoBytes = 40L * 1024 * 1024;

        public const string StagedFolder = "covers-staged";

        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(1);

        private static readonly Regex IdPattern = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        private readonly IDatabaseService _database;
        private readonly IAttachmentService _attachments;
        private readonly ICoverImageProcessor _processor;
        private readonly ISupabaseClient? _remote;

        private readonly ConcurrentDictionary<string, Task<string?>> _downloads = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, DateTime> _failedAt = new(StringComparer.Ordinal);

        public TripCoverService(IDatabaseService database, IAttachmentService attachments, ICoverImageProcessor processor, ISupabaseClient? remote = null)
        {
            _database = database;
            _attachments = attachments;
            _processor = processor;
            _remote = remote;
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Runs UI updates on the main thread (tests run them in place).</summary>
        internal Action<Action> OnMainThread { get; set; } = action =>
        {
            if (Application.Current?.Dispatcher is { } dispatcher)
                dispatcher.Dispatch(action);
            else
                action();
        };

        // ------------------------------------------------------------------ names

        /// <summary>"trips/&lt;trip&gt;/cover-&lt;id&gt;.jpg" - the only shape the server accepts.</summary>
        public static string RemotePath(string tripSyncId, string coverId, bool thumbnail = false) =>
            $"trips/{tripSyncId.ToLowerInvariant()}/cover-{coverId}{(thumbnail ? "-thumb" : string.Empty)}.jpg";

        /// <summary>The cover id in <paramref name="coverPath"/> when it is exactly this trip's cover path; null otherwise.</summary>
        public static string? CoverIdOf(string? coverPath, string? tripSyncId)
        {
            if (string.IsNullOrEmpty(coverPath) || string.IsNullOrEmpty(tripSyncId))
                return null;
            var prefix = $"trips/{tripSyncId.ToLowerInvariant()}/cover-";
            if (!coverPath.StartsWith(prefix, StringComparison.Ordinal) || !coverPath.EndsWith(".jpg", StringComparison.Ordinal))
                return null;
            var id = coverPath[prefix.Length..^4];
            return IdPattern.IsMatch(id) ? id : null;
        }

        private static bool IsId(string? id) => id is not null && IdPattern.IsMatch(id);

        private string TripFolder(int tripId) =>
            Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName, tripId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        private string StagingFolder => Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName, StagedFolder);

        internal string LocalFile(int tripId, string coverId, bool thumbnail) =>
            Path.Combine(TripFolder(tripId), $"cover-{coverId}{(thumbnail ? "-thumb" : string.Empty)}.jpg");

        private string StagedPath(string coverId, bool thumbnail) =>
            Path.Combine(StagingFolder, $"cover-{coverId}{(thumbnail ? "-thumb" : string.Empty)}.jpg");

        /// <summary>The cover shown on this device: the one waiting for upload, else the trip's.</summary>
        private static string? ShownCoverId(Trip trip) =>
            IsId(trip.PendingCoverId) ? trip.PendingCoverId : CoverIdOf(trip.CoverPath, trip.SyncId);

        public bool HasCover(Trip trip) => ShownCoverId(trip) is not null;

        // ------------------------------------------------------------------ picking

        public async Task<PickedPhoto> OpenAsync(Stream photo, CancellationToken cancellationToken = default)
        {
            var source = await ReadPhotoAsync(photo, cancellationToken);
            var (width, height) = await _processor.MeasureAsync(source);
            if (width < 1 || height < 1)
                throw new InvalidDataException("Not a picture this device can read.");

            Directory.CreateDirectory(StagingFolder);
            DeleteOldStaged();
            var file = Path.Combine(StagingFolder, $"picked-{Guid.NewGuid():N}.img");
            await File.WriteAllBytesAsync(file, source.ToArray(), cancellationToken);
            return new PickedPhoto(file, width, height);
        }

        public void DiscardPicked(PickedPhoto photo)
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(photo.File)), Path.GetFullPath(StagingFolder), StringComparison.Ordinal)
                && Path.GetFileName(photo.File).StartsWith("picked-", StringComparison.Ordinal))
                TryDelete(photo.File);
        }

        public async Task<string> PrepareAsync(Stream photo, CropRegion? region = null, CancellationToken cancellationToken = default)
        {
            if (region is { IsValid: false })
                throw new ArgumentOutOfRangeException(nameof(region), region, "The crop region must be inside the photo.");

            var source = await ReadPhotoAsync(photo, cancellationToken);
            var cover = await _processor.CropAsync(source, region, CoverWidth, CoverHeight, CoverQuality);
            // the thumbnail from the cover: a much smaller picture to decode, and the same part of the photo
            var thumb = await _processor.CropAsync(new MemoryStream(cover), null, ThumbSize, ThumbSize, ThumbQuality);

            var id = Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(StagingFolder);
            DeleteOldStaged();
            await File.WriteAllBytesAsync(StagedPath(id, thumbnail: false), cover, cancellationToken);
            await File.WriteAllBytesAsync(StagedPath(id, thumbnail: true), thumb, cancellationToken);
            return id;
        }

        private static async Task<MemoryStream> ReadPhotoAsync(Stream photo, CancellationToken cancellationToken)
        {
            var source = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await photo.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (source.Length + read > MaxPhotoBytes)
                    throw new InvalidDataException("The photo is too large.");
                source.Write(buffer, 0, read);
            }
            if (source.Length == 0)
                throw new InvalidDataException("The photo is empty.");
            source.Position = 0;
            return source;
        }

        public string? StagedFile(string coverId)
        {
            if (!IsId(coverId))
                return null;
            var path = StagedPath(coverId, thumbnail: false);
            return File.Exists(path) ? path : null;
        }

        public void DiscardStaged(string coverId)
        {
            if (!IsId(coverId))
                return;
            TryDelete(StagedPath(coverId, thumbnail: false));
            TryDelete(StagedPath(coverId, thumbnail: true));
        }

        public async Task SetAsync(Trip trip, string coverId)
        {
            if (!IsId(coverId))
                throw new ArgumentException("Not a cover id.", nameof(coverId));
            var staged = StagedPath(coverId, thumbnail: false);
            var stagedThumb = StagedPath(coverId, thumbnail: true);
            if (!File.Exists(staged) || !File.Exists(stagedThumb))
                throw new FileNotFoundException("The picked cover is gone.", staged);

            Directory.CreateDirectory(TripFolder(trip.Id));
            File.Move(staged, LocalFile(trip.Id, coverId, thumbnail: false), overwrite: true);
            File.Move(stagedThumb, LocalFile(trip.Id, coverId, thumbnail: true), overwrite: true);

            var previousPending = trip.PendingCoverId;
            var db = await _database.Connection;
            // device-only column: written directly, nothing to sync until the upload is done
            await db.ExecuteAsync("UPDATE Trip SET PendingCoverId = ? WHERE Id = ?", coverId, trip.Id);
            trip.PendingCoverId = coverId;

            // a photo picked earlier that never got uploaded is replaced
            if (IsId(previousPending) && previousPending != coverId)
                DeleteLocal(trip.Id, previousPending!);
        }

        public async Task RemoveAsync(Trip trip)
        {
            var db = await _database.Connection;
            var stored = await db.FindAsync<Trip>(trip.Id);
            if (stored is null)
                return;

            var ids = new[] { stored.PendingCoverId, CoverIdOf(stored.CoverPath, stored.SyncId) };
            await db.ExecuteAsync("UPDATE Trip SET PendingCoverId = NULL WHERE Id = ?", trip.Id);
            stored.PendingCoverId = null;
            if (stored.CoverPath is not null)
            {
                stored.CoverPath = null;
                await db.UpdateTrackedAsync(stored);
            }

            trip.PendingCoverId = null;
            trip.CoverPath = null;
            trip.CoverThumbFile = null;
            foreach (var id in ids.Where(IsId))
                DeleteLocal(trip.Id, id!);
        }

        // ------------------------------------------------------------------ showing

        public async Task<string?> GetFileAsync(Trip trip, bool thumbnail, CancellationToken cancellationToken = default)
        {
            if (IsId(trip.PendingCoverId))
            {
                var pending = LocalFile(trip.Id, trip.PendingCoverId!, thumbnail);
                if (File.Exists(pending))
                    return pending;
            }

            var coverId = CoverIdOf(trip.CoverPath, trip.SyncId);
            if (coverId is null)
                return null;
            var local = LocalFile(trip.Id, coverId, thumbnail);
            if (File.Exists(local))
                return local;
            if (_remote is null)
                return null;

            var remote = RemotePath(trip.SyncId, coverId, thumbnail);
            if (_failedAt.TryGetValue(remote, out var failed) && UtcNow() - failed < FailureCooldown)
                return null;

            // one download per file, whoever asks (the list and the detail page may ask together)
            var download = _downloads.GetOrAdd(remote, _ => DownloadAsync(remote, local));
            try
            {
                var file = await download.WaitAsync(cancellationToken);
                if (file is not null)
                    await PruneOldCoversAsync(trip.Id, coverId);
                return file;
            }
            finally
            {
                if (download.IsCompleted)
                    _downloads.TryRemove(new KeyValuePair<string, Task<string?>>(remote, download));
            }
        }

        private async Task<string?> DownloadAsync(string remote, string local)
        {
            var temporary = local + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                await using (var file = File.Create(temporary))
                    await _remote!.DownloadAsync(SyncService.Bucket, remote, file);
                if (new FileInfo(temporary).Length == 0)
                    throw new IOException("Empty cover file.");
                File.Move(temporary, local, overwrite: true);
                _failedAt.TryRemove(remote, out _);
                return local;
            }
            catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[Covers] Download of {remote} failed: {ex.Message}");
                _failedAt[remote] = UtcNow();
                TryDelete(temporary);
                return null;
            }
        }

        public void ShowThumbnails(IEnumerable<Trip> trips)
        {
            foreach (var trip in trips)
            {
                var coverId = ShownCoverId(trip);
                if (coverId is null)
                {
                    trip.CoverThumbFile = null;
                    continue;
                }

                var local = LocalFile(trip.Id, coverId, thumbnail: true);
                if (File.Exists(local))
                {
                    trip.CoverThumbFile = local;
                    continue;
                }

                _ = ShowWhenDownloadedAsync(trip);
            }
        }

        private async Task ShowWhenDownloadedAsync(Trip trip)
        {
            try
            {
                var file = await GetFileAsync(trip, thumbnail: true);
                if (file is not null)
                    OnMainThread(() => trip.CoverThumbFile = file);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Covers] Thumbnail of trip {trip.Id}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ sync

        public async Task<int> UploadPendingAsync(CancellationToken cancellationToken = default)
        {
            if (_remote is null)
                return 0;

            var db = await _database.Connection;
            // ChangeSeq <> 0: the trip itself is on the server (its folder's rules need the trip row)
            var trips = await db.QueryAsync<Trip>(
                "SELECT * FROM Trip WHERE PendingCoverId IS NOT NULL AND PendingCoverId <> '' AND ChangeSeq <> 0");

            var uploaded = 0;
            foreach (var trip in trips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var coverId = trip.PendingCoverId!;
                var cover = LocalFile(trip.Id, coverId, thumbnail: false);
                var thumb = LocalFile(trip.Id, coverId, thumbnail: true);
                if (!IsId(coverId) || !File.Exists(cover) || !File.Exists(thumb))
                {
                    // the files are gone (backup restored elsewhere, storage cleared): nothing to send
                    await db.ExecuteAsync("UPDATE Trip SET PendingCoverId = NULL WHERE Id = ?", trip.Id);
                    continue;
                }

                try
                {
                    // the thumbnail first: when the path arrives, the list's file is surely there
                    await using (var stream = File.OpenRead(thumb))
                        await _remote.UploadAsync(SyncService.Bucket, RemotePath(trip.SyncId, coverId, thumbnail: true), stream, "image/jpeg", cancellationToken);
                    await using (var stream = File.OpenRead(cover))
                        await _remote.UploadAsync(SyncService.Bucket, RemotePath(trip.SyncId, coverId), stream, "image/jpeg", cancellationToken);
                }
                catch (SyncException ex) when (ex.Kind == SyncErrorKind.Rejected)
                {
                    // e.g. the trip's storage is full or this account became a viewer: stays on this device, retried later
                    System.Diagnostics.Debug.WriteLine($"[Covers] Upload for trip {trip.Id} refused: {ex.Message}");
                    continue;
                }

                var stored = await db.FindAsync<Trip>(trip.Id);
                if (stored is null || stored.PendingCoverId != coverId)
                    continue; // deleted or another photo picked meanwhile - that one goes next round

                var previous = CoverIdOf(stored.CoverPath, stored.SyncId);
                stored.CoverPath = RemotePath(stored.SyncId, coverId);
                try
                {
                    await db.UpdateTrackedAsync(stored);
                }
                catch (TripAccessException)
                {
                    continue;
                }
                await db.ExecuteAsync("UPDATE Trip SET PendingCoverId = NULL WHERE Id = ? AND PendingCoverId = ?", trip.Id, coverId);
                if (previous is not null && previous != coverId)
                    DeleteLocal(trip.Id, previous);
                uploaded++;
            }

            return uploaded;
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// A new cover arrived from another device: the trip's older cover files here are of no use any more. Kept:
        /// the trip's cover and a photo picked here that waits for upload.
        /// </summary>
        private async Task PruneOldCoversAsync(int tripId, string coverId)
        {
            var db = await _database.Connection;
            var pending = await db.ExecuteScalarAsync<string>("SELECT PendingCoverId FROM Trip WHERE Id = ?", tripId);
            try
            {
                foreach (var file in Directory.EnumerateFiles(TripFolder(tripId), "cover-*.jpg"))
                {
                    var name = Path.GetFileName(file);
                    if (!name.StartsWith("cover-" + coverId, StringComparison.Ordinal) &&
                        !(IsId(pending) && name.StartsWith("cover-" + pending, StringComparison.Ordinal)))
                        TryDelete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[Covers] Could not tidy the covers of trip {tripId}: {ex.Message}");
            }
        }

        /// <summary>Photos picked in a form that was left without saving (the app closed meanwhile): gone after a day.</summary>
        private void DeleteOldStaged()
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(StagingFolder, "cover-*.jpg")
                                              .Concat(Directory.EnumerateFiles(StagingFolder, "picked-*.img")))
                {
                    if (UtcNow() - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(1))
                        TryDelete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[Covers] Could not tidy the staged covers: {ex.Message}");
            }
        }

        private void DeleteLocal(int tripId, string coverId)
        {
            TryDelete(LocalFile(tripId, coverId, thumbnail: false));
            TryDelete(LocalFile(tripId, coverId, thumbnail: true));
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[Covers] Could not delete {path}: {ex.Message}");
            }
        }
    }
}
