using System.Text.RegularExpressions;
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

        /// <summary>
        /// TP-3: the only file types the cloud accepts (the "trip-files" bucket's allowed_mime_types,
        /// supabase/migrations/…13_quota_limits.sql), by file extension.
        /// </summary>
        private static readonly Dictionary<string, string> CloudTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".heic"] = "image/heic",
            [".heif"] = "image/heif",
            [".webp"] = "image/webp",
            [".pdf"] = "application/pdf",
        };

        private static readonly HashSet<string> CloudTypes = new(CloudTypesByExtension.Values, StringComparer.Ordinal);

        /// <summary>TP-5: the file extension each cloud type is stored under.</summary>
        private static readonly Dictionary<string, string> ExtensionsByCloudType = new(StringComparer.Ordinal)
        {
            ["image/jpeg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/heic"] = ".heic",
            ["image/heif"] = ".heif",
            ["image/webp"] = ".webp",
            ["application/pdf"] = ".pdf",
        };

        /// <summary>
        /// TP-5: the only shape of an attachment's cloud path - "trips/&lt;trip SyncId&gt;/&lt;attachment SyncId&gt;.&lt;ext&gt;",
        /// the same rule as the server's attachments_storage_path_format (supabase/migrations/…15_attachment_paths.sql).
        /// \z rather than $: $ would also accept a trailing line break.
        /// </summary>
        private static readonly Regex RemotePathPattern = new(
            @"^trips/(?<trip>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/(?<id>[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})(?<ext>\.[a-z0-9]{1,10})\z",
            RegexOptions.CultureInvariant);

        /// <summary>
        /// TP-5: the extension a file is stored (locally and in the cloud) under - its own when that is one of the
        /// cloud types' (.jpg .jpeg .png .heic .heif .webp .pdf), otherwise the one its content type stands for;
        /// null when it is neither.
        /// </summary>
        public static string? CloudExtension(string? fileName, string? contentType)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (CloudTypesByExtension.ContainsKey(extension))
                return extension;

            return CloudContentType(null, contentType) is { } type && ExtensionsByCloudType.TryGetValue(type, out var byType)
                ? byType
                : null;
        }

        /// <summary>TP-5: the content type a file is opened with - from its extension and the whitelist only, never from the server.</summary>
        public static string? ContentTypeForExtension(string? fileName) =>
            CloudTypesByExtension.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out var type) ? type : null;

        /// <summary>TP-5: <paramref name="remotePath"/> has the shape "trips/&lt;uuid&gt;/&lt;uuid&gt;.&lt;ext&gt;" (ids not checked).</summary>
        public static bool IsWellFormedRemotePath(string? remotePath) =>
            remotePath is not null && RemotePathPattern.IsMatch(remotePath);

        /// <summary>
        /// TP-5: <paramref name="remotePath"/> is exactly this attachment's own cloud file - "trips/&lt;its trip's SyncId&gt;/&lt;its SyncId&gt;"
        /// plus a photo/PDF extension. Anything else (another row's or another trip's file, "..", "?", "#", an .apk …) is never downloaded.
        /// </summary>
        public static bool IsValidRemotePath(string? remotePath, string? tripSyncId, string? attachmentSyncId)
        {
            if (remotePath is null || string.IsNullOrEmpty(tripSyncId) || string.IsNullOrEmpty(attachmentSyncId))
                return false;

            var match = RemotePathPattern.Match(remotePath);
            return match.Success
                && string.Equals(match.Groups["trip"].Value, tripSyncId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(match.Groups["id"].Value, attachmentSyncId, StringComparison.OrdinalIgnoreCase)
                && CloudTypesByExtension.ContainsKey(match.Groups["ext"].Value);
        }

        /// <summary>
        /// TP-5: the type to hand the device's viewer: from the local file's extension, else from the (well-formed)
        /// cloud path's - always one of the photo/PDF types, or null when the file is neither.
        /// </summary>
        public static string? OpenContentType(TripAttachment attachment) =>
            ContentTypeForExtension(attachment.RelativePath)
            ?? (IsWellFormedRemotePath(attachment.RemotePath) ? ContentTypeForExtension(attachment.RemotePath) : null);

        /// <summary>
        /// The MIME type a file is stored and uploaded with: the picker's type when the cloud accepts it, otherwise
        /// the one its extension stands for; null when the file is none of JPEG, PNG, HEIC/HEIF, WebP or PDF.
        /// </summary>
        public static string? CloudContentType(string? fileName, string? contentType)
        {
            var type = contentType?.Split(';')[0].Trim().ToLowerInvariant();
            if (type is "image/jpg" or "image/pjpeg")
                type = "image/jpeg";
            if (type is not null && CloudTypes.Contains(type))
                return type;

            return CloudTypesByExtension.TryGetValue(Path.GetExtension(fileName ?? string.Empty), out var byExtension)
                ? byExtension
                : null;
        }

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
            // TP-3: only what the cloud accepts, so every attachment can reach the trip's other devices
            contentType = CloudContentType(displayName, contentType) ?? throw new UnsupportedAttachmentTypeException();
            // TP-5: always a photo/PDF extension (also for "IMG_1234" without one), so the cloud path and opening agree
            var extension = CloudExtension(displayName, contentType) ?? throw new UnsupportedAttachmentTypeException();

            var relativePath = $"{FolderName}/{tripId}/{Guid.NewGuid():N}{extension}";
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

            // TP-5: only this attachment's own file - a path an editor pointed elsewhere is never requested
            var db = await _databaseService.Connection;
            var tripSyncId = await db.ExecuteScalarAsync<string>("SELECT SyncId FROM Trip WHERE Id = ?", attachment.TripId);
            if (!IsValidRemotePath(attachment.RemotePath, tripSyncId, attachment.SyncId))
            {
                System.Diagnostics.Debug.WriteLine($"[AttachmentService] Not downloading {attachment.SyncId}: unexpected cloud path.");
                return false;
            }

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

    /// <summary>TP-3: the file is not a photo (JPEG, PNG, HEIC/HEIF, WebP) or a PDF.</summary>
    public sealed class UnsupportedAttachmentTypeException : Exception
    {
        public UnsupportedAttachmentTypeException()
            : base("Only JPEG, PNG, HEIC/HEIF, WebP and PDF files can be attached.")
        {
        }
    }
}
