using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class AttachmentService : IAttachmentService
    {
        public const string FolderName = "attachments";

        /// <summary>Sub-folder of <see cref="FolderName"/> holding files of deleted attachments until the trash is purged (G1).</summary>
        public const string TrashFolderName = "trash";

        /// <summary>Upper bound per file, so a huge video picked by mistake can't fill the device.</summary>
        public const long MaxFileBytes = 50L * 1024 * 1024;

        private static readonly HashSet<string> PhotoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp", ".gif", ".bmp"
        };

        private readonly IDatabaseService _databaseService;
        private readonly ISupabaseClient? _remote;

        /// <param name="rootDirectory">The app passes FileSystem.AppDataDirectory; tests pass a temp folder.</param>
        /// <param name="remote">Cloud storage for files uploaded from other devices (G5); null = local only.</param>
        public AttachmentService(IDatabaseService databaseService, string rootDirectory, ISupabaseClient? remote = null)
        {
            _databaseService = databaseService;
            RootDirectory = rootDirectory;
            _remote = remote;
        }

        public string RootDirectory { get; private set; }

        public void UseRootDirectory(string rootDirectory) => RootDirectory = rootDirectory;

        public static AttachmentKind DetectKind(string fileName, string? contentType) =>
            contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true || PhotoExtensions.Contains(Path.GetExtension(fileName))
                ? AttachmentKind.Photo
                : AttachmentKind.Document;

        public string GetFullPath(TripAttachment attachment) =>
            Path.Combine(RootDirectory, attachment.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        public async Task<List<TripAttachment>> GetByTripIdAsync(int tripId)
        {
            var db = await _databaseService.Connection;
            var attachments = await db.Table<TripAttachment>()
                .Where(a => a.TripId == tripId)
                .OrderByDescending(a => a.CreatedDate)
                .ToListAsync();

            foreach (var attachment in attachments)
                attachment.FullPath = GetFullPath(attachment);

            return attachments;
        }

        public async Task<TripAttachment> AddAsync(int tripId, Stream content, string originalFileName, string? contentType)
        {
            var displayName = string.IsNullOrWhiteSpace(originalFileName) ? "file" : Path.GetFileName(originalFileName.Trim());
            var extension = Path.GetExtension(displayName);
            if (extension.Length > 10)
                extension = string.Empty;

            var relativePath = $"{FolderName}/{tripId}/{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
            var attachment = new TripAttachment
            {
                TripId = tripId,
                DisplayName = displayName.Length > 150 ? displayName[..150] : displayName,
                RelativePath = relativePath,
                ContentType = contentType,
                Kind = DetectKind(displayName, contentType),
                CreatedDate = DateTime.Now
            };

            var fullPath = GetFullPath(attachment);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

            try
            {
                await using (var target = File.Create(fullPath))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await content.ReadAsync(buffer)) > 0)
                    {
                        total += read;
                        if (total > MaxFileBytes)
                            throw new AttachmentTooLargeException(MaxFileBytes);
                        await target.WriteAsync(buffer.AsMemory(0, read));
                    }

                    attachment.SizeBytes = total;
                }

                var db = await _databaseService.Connection;
                await db.InsertTrackedAsync(attachment);
            }
            catch
            {
                // Never leave an orphaned half-written file behind.
                TryDeleteFile(fullPath);
                throw;
            }

            attachment.FullPath = fullPath;
            return attachment;
        }

        public async Task RenameAsync(TripAttachment attachment, string displayName)
        {
            var name = (displayName ?? string.Empty).Trim();
            if (name.Length == 0)
                return;

            attachment.DisplayName = name.Length > 150 ? name[..150] : name;
            var db = await _databaseService.Connection;
            await db.UpdateTrackedAsync(attachment);
        }

        public async Task DeleteAsync(TripAttachment attachment)
        {
            var db = await _databaseService.Connection;
            await db.DeleteTrackedAsync(attachment);
            MoveFileToTrash(attachment);
        }

        public async Task<bool> EnsureLocalFileAsync(TripAttachment attachment, CancellationToken cancellationToken = default)
        {
            var path = GetFullPath(attachment);
            attachment.FullPath = path;
            if (File.Exists(path))
                return true;
            if (_remote is null || string.IsNullOrEmpty(attachment.RemotePath))
                return false;

            // Download next to the target first, so a broken connection never leaves half a file behind.
            var temporary = path + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var file = File.Create(temporary))
                    await _remote.DownloadAsync(SyncService.Bucket, attachment.RemotePath, file, cancellationToken);
                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is SyncException or IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Download of {attachment.RemotePath} failed: {ex.Message}");
                TryDeleteFile(temporary);
                return false;
            }
        }

        /// <summary>"…/attachments/trash/&lt;SyncId&gt;.pdf" - where a deleted attachment's file waits for restore or purge.</summary>
        public string GetTrashPath(TripAttachment attachment) =>
            Path.Combine(RootDirectory, FolderName, TrashFolderName, attachment.SyncId + Path.GetExtension(attachment.RelativePath).ToLowerInvariant());

        public void MoveFileToTrash(TripAttachment attachment)
        {
            var source = GetFullPath(attachment);
            if (string.IsNullOrEmpty(attachment.SyncId))
            {
                TryDeleteFile(source); // can't be restored without an identity - behave as before G1
                return;
            }

            try
            {
                if (!File.Exists(source))
                    return;

                var target = GetTrashPath(attachment);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Could not move {source} to the trash: {ex}");
                TryDeleteFile(source);
            }
        }

        public bool RestoreFileFromTrash(TripAttachment attachment)
        {
            if (string.IsNullOrEmpty(attachment.SyncId))
                return false;

            var source = GetTrashPath(attachment);
            var target = GetFullPath(attachment);
            try
            {
                if (!File.Exists(source))
                    return false;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(source, target, overwrite: true);
                attachment.FullPath = target;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Could not restore {target}: {ex}");
                return false;
            }
        }

        public void DeleteTrashedFile(TripAttachment attachment)
        {
            if (!string.IsNullOrEmpty(attachment.SyncId))
                TryDeleteFile(GetTrashPath(attachment));
        }

        public void DeleteFilesForTrip(int tripId)
        {
            var folder = Path.Combine(RootDirectory, FolderName, tripId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Could not delete {folder}: {ex}");
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Could not delete {path}: {ex}");
            }
        }
    }

    public sealed class AttachmentTooLargeException : Exception
    {
        public AttachmentTooLargeException(long maxBytes)
            : base($"Attachment exceeds {maxBytes} bytes.")
        {
            MaxBytes = maxBytes;
        }

        public long MaxBytes { get; }
    }
}
