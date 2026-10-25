using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    public class BackupService : IBackupService
    {
        public const string FormatName = "TravelPlanner.Backup";
        /// <summary>
        /// 2 (G1): rows carry their sync metadata (SyncId, version …). Version 1 backups are still restored - their
        /// rows simply get new SyncIds.
        /// </summary>
        public const int FormatVersion = 2;
        public const string DataEntryName = "travelplanner-backup.json";

        /// <summary>Largest backup file accepted for restore (data + every attachment).</summary>
        public const long MaxBackupBytes = 2L * 1024 * 1024 * 1024;

        /// <summary>Largest (uncompressed) data JSON accepted - far beyond any real backup, but stops a zip bomb.</summary>
        public const long MaxDataEntryBytes = 100L * 1024 * 1024;

        private readonly IDatabaseService _databaseService;
        private readonly IAttachmentService _attachments;
        private readonly string _outputDirectory;

        /// <summary>
        /// Model classes carry UI-only computed properties marked with SQLite's [Ignore]; the backup contains
        /// exactly the stored columns, nothing device-specific (e.g. an attachment's absolute path).
        /// </summary>
        internal static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    info =>
                    {
                        if (info.Kind != JsonTypeInfoKind.Object || info.Type.Namespace != typeof(Trip).Namespace)
                            return;

                        foreach (var property in info.Properties.ToList())
                        {
                            if (property.AttributeProvider?.IsDefined(typeof(SQLite.IgnoreAttribute), true) == true)
                                info.Properties.Remove(property);
                        }
                    }
                }
            }
        };

        /// <param name="outputDirectory">The app passes FileSystem.CacheDirectory.</param>
        public BackupService(IDatabaseService databaseService, IAttachmentService attachments, string outputDirectory)
        {
            _databaseService = databaseService;
            _attachments = attachments;
            _outputDirectory = outputDirectory;
        }

        /// <summary>The JSON document inside a backup.</summary>
        public sealed class BackupData
        {
            // [JsonRequired]: without it any JSON file (e.g. {"hello":1}) deserialized with these defaults, passed
            // the format check and replaced - i.e. wiped - every table with empty lists.
            [JsonRequired]
            public string Format { get; set; } = FormatName;
            [JsonRequired]
            public int Version { get; set; } = FormatVersion;
            public DateTime ExportedAt { get; set; } = DateTime.Now;
            public List<Trip> Trips { get; set; } = new();
            public List<TodoItem> Todos { get; set; } = new();
            public List<Expense> Expenses { get; set; } = new();
            public List<Place> Places { get; set; } = new();
            public List<TripActivity> Activities { get; set; } = new();
            public List<PackingTemplate> PackingTemplates { get; set; } = new();
            public List<Participant> Participants { get; set; } = new();
            public List<ExpenseShare> ExpenseShares { get; set; } = new();
            public List<TripAttachment> Attachments { get; set; } = new();
        }

        public async Task<BackupData> ReadAllAsync()
        {
            var db = await _databaseService.Connection;
            return new BackupData
            {
                ExportedAt = DateTime.Now,
                Trips = await db.Table<Trip>().ToListAsync(),
                Todos = await db.Table<TodoItem>().ToListAsync(),
                Expenses = await db.Table<Expense>().ToListAsync(),
                Places = await db.Table<Place>().ToListAsync(),
                Activities = await db.Table<TripActivity>().ToListAsync(),
                PackingTemplates = await db.Table<PackingTemplate>().ToListAsync(),
                Participants = await db.Table<Participant>().ToListAsync(),
                ExpenseShares = await db.Table<ExpenseShare>().ToListAsync(),
                Attachments = await db.Table<TripAttachment>().ToListAsync(),
            };
        }

        /// <summary>
        /// TP-72: removes backup zips this app exported into <paramref name="cacheDirectory"/> earlier (and restore
        /// buffers a crash left behind) once they are older than <paramref name="olderThan"/>. The share sheet only
        /// hands the file over - the app it went to may still be reading it - so it isn't deleted right after
        /// sharing, but at the next export or app start. A copy of all the user's data shouldn't stay in the cache.
        /// </summary>
        public static int DeleteOldExports(string cacheDirectory, TimeSpan olderThan)
        {
            if (!Directory.Exists(cacheDirectory))
                return 0;

            var cutoff = DateTime.UtcNow - olderThan;
            var deleted = 0;
            foreach (var file in Directory.EnumerateFiles(cacheDirectory, "TravelPlanner-*.zip")
                         .Concat(Directory.EnumerateFiles(cacheDirectory, "restore-*.tmp")))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) <= cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (IOException) { /* in use - next time */ }
                catch (UnauthorizedAccessException) { }
            }
            return deleted;
        }

        public async Task<string> ExportAsync()
        {
            var data = await ReadAllAsync();
            Directory.CreateDirectory(_outputDirectory);
            DeleteOldExports(_outputDirectory, TimeSpan.Zero);
            var path = Path.Combine(_outputDirectory, $"TravelPlanner-{data.ExportedAt:yyyy-MM-dd-HHmm}.zip");

            await Task.Run(() =>
            {
                if (File.Exists(path))
                    File.Delete(path);

                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

                var json = zip.CreateEntry(DataEntryName, CompressionLevel.Optimal);
                using (var stream = json.Open())
                    JsonSerializer.Serialize(stream, data, JsonOptions);

                foreach (var attachment in data.Attachments)
                {
                    var file = _attachments.GetFullPath(attachment);
                    if (File.Exists(file))
                        zip.CreateEntryFromFile(file, attachment.RelativePath.Replace('\\', '/'), CompressionLevel.Fastest);
                }
            });

            return path;
        }

        public async Task<BackupImportResult> ImportAsync(Stream content, string fileName,
            Func<SharedTripsInBackup, Task<SharedTripRestoreChoice>>? decideSharedTrips = null)
        {
            // Copy to a temporary file first: the picked stream may not be seekable, and nothing is touched until
            // the whole backup has been validated. (A file, not a MemoryStream: a large backup with photos would
            // otherwise have to fit in RAM.) Size-capped, and deleted again however this method ends.
            Directory.CreateDirectory(_outputDirectory);
            var tempPath = Path.Combine(_outputDirectory, $"restore-{Guid.NewGuid():N}.tmp");
            await using var buffer = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await CopyWithLimitAsync(content, buffer, MaxBackupBytes);
            buffer.Position = 0;

            ZipArchive? zip = null;
            BackupData data;
            try
            {
                if (IsZip(buffer))
                {
                    zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
                    var entry = zip.GetEntry(DataEntryName) ?? throw new InvalidBackupException("missing data entry");
                    if (entry.Length > MaxDataEntryBytes)
                        throw new InvalidBackupException("data entry too large");
                    // TP-72: entry.Length is what the zip claims - count what really comes out (zip bomb)
                    using var stream = new LimitedReadStream(entry.Open(), MaxDataEntryBytes);
                    data = JsonSerializer.Deserialize<BackupData>(stream, JsonOptions) ?? throw new InvalidBackupException("empty");
                }
                else
                {
                    data = JsonSerializer.Deserialize<BackupData>(buffer, JsonOptions) ?? throw new InvalidBackupException("empty");
                }
            }
            catch (JsonException ex)
            {
                zip?.Dispose();
                throw new InvalidBackupException("not valid JSON", ex);
            }
            catch (InvalidDataException ex)
            {
                zip?.Dispose();
                throw new InvalidBackupException("not a valid zip", ex);
            }
            catch (InvalidBackupException)
            {
                zip?.Dispose();
                throw;
            }
            catch (IOException ex) when (ex.InnerException is InvalidBackupException inner)
            {
                zip?.Dispose();
                throw inner;
            }

            using (zip)
            {
                if (data.Format != FormatName || data.Version < 1 || data.Version > FormatVersion)
                    throw new InvalidBackupException($"unsupported format {data.Format} v{data.Version}");

                Sanitize(data, Path.GetFullPath(Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName)));

                // TP-10: shared trips are never restored over the cloud copy - nothing has been touched yet.
                int skipped = 0, copied = 0;
                var shared = await FindSharedTripsAsync(data);
                if (shared.Count > 0)
                {
                    var choice = decideSharedTrips is null
                        ? SharedTripRestoreChoice.Skip
                        : await decideSharedTrips(new SharedTripsInBackup(shared.Select(t => t.Name).ToList()));

                    var ids = shared.Select(t => t.Id).ToHashSet();
                    switch (choice)
                    {
                        case SharedTripRestoreChoice.Cancel:
                            throw new OperationCanceledException("restore cancelled");
                        case SharedTripRestoreChoice.Copy:
                            CopySharedTrips(data, ids);
                            copied = ids.Count;
                            break;
                        default:
                            RemoveTrips(data, ids);
                            skipped = ids.Count;
                            break;
                    }
                }

                await ReplaceAllAsync(data);
                ClearTrashFolder();
                var restoredFiles = zip is null ? 0 : RestoreFiles(zip, data.Attachments);
                return new BackupImportResult(data.Trips.Count, data.Expenses.Count, restoredFiles, data.ExportedAt, skipped, copied);
            }
        }

        /// <summary>
        /// TP-10: the backup's trips that other people take part in, for a signed-in user (a guest has no shared
        /// trips). A trip counts as shared when the user is an editor/viewer in it (the memberships the last sync
        /// brought), or when someone else created or edited one of its rows - in the backup or in the data on this
        /// device now - or is linked to one of its participants. That also catches the user's own trips that others
        /// joined, and trips the user has since been removed from.
        /// </summary>
        internal async Task<List<Trip>> FindSharedTripsAsync(BackupData data)
        {
            var me = Sync.ChangeTracker.CurrentUserId;
            if (me is null || data.Trips.Count == 0)
                return new List<Trip>();

            bool IsOther(string? userId) => !string.IsNullOrEmpty(userId) && !string.Equals(userId, me, StringComparison.OrdinalIgnoreCase);
            bool TouchedByOther(ISyncEntity row) => IsOther(row.CreatedBy) || IsOther(row.UpdatedBy);

            var db = await _databaseService.Connection;
            var roles = (await db.Table<TripMembership>().ToListAsync())
                .GroupBy(m => m.TripSyncId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => TripRoles.Parse(g.First().Role), StringComparer.OrdinalIgnoreCase);

            // Trips (by SyncId) that someone else has touched in the data on this device now.
            var users = await db.QueryAsync<TripUserRow>(
                "SELECT t.SyncId AS TripSyncId, x.UserId AS UserId FROM Trip t JOIN (" +
                " SELECT Id AS TripId, CreatedBy AS UserId FROM Trip UNION ALL SELECT Id, UpdatedBy FROM Trip" +
                " UNION ALL SELECT TripId, CreatedBy FROM Expense UNION ALL SELECT TripId, UpdatedBy FROM Expense" +
                " UNION ALL SELECT TripId, CreatedBy FROM Todo UNION ALL SELECT TripId, UpdatedBy FROM Todo" +
                " UNION ALL SELECT TripId, CreatedBy FROM Place UNION ALL SELECT TripId, UpdatedBy FROM Place" +
                " UNION ALL SELECT TripId, CreatedBy FROM Activity UNION ALL SELECT TripId, UpdatedBy FROM Activity" +
                " UNION ALL SELECT TripId, CreatedBy FROM Attachment UNION ALL SELECT TripId, UpdatedBy FROM Attachment" +
                " UNION ALL SELECT TripId, CreatedBy FROM Participant UNION ALL SELECT TripId, UpdatedBy FROM Participant" +
                " UNION ALL SELECT TripId, UserId FROM Participant" +
                ") x ON x.TripId = t.Id WHERE x.UserId IS NOT NULL AND x.UserId <> '' AND t.SyncId IS NOT NULL");
            var touchedHere = users.Where(u => IsOther(u.UserId)).Select(u => u.TripSyncId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ... and in the backup.
            var touchedInBackup = new HashSet<int>();
            void Scan<T>(IEnumerable<T> rows, Func<T, int> tripId, Func<T, bool>? extra = null) where T : ISyncEntity
            {
                foreach (var row in rows)
                {
                    if (TouchedByOther(row) || extra?.Invoke(row) == true)
                        touchedInBackup.Add(tripId(row));
                }
            }

            Scan(data.Trips, t => t.Id);
            Scan(data.Expenses, e => e.TripId);
            Scan(data.Todos, t => t.TripId);
            Scan(data.Places, p => p.TripId);
            Scan(data.Activities, a => a.TripId);
            Scan(data.Attachments, a => a.TripId);
            Scan(data.Participants, p => p.TripId, p => IsOther(p.UserId));
            var expenseTrips = data.Expenses.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().TripId);
            Scan(data.ExpenseShares, s => expenseTrips.TryGetValue(s.ExpenseId, out var tripId) ? tripId : 0);

            return data.Trips.Where(trip =>
                    touchedInBackup.Contains(trip.Id) ||
                    (!string.IsNullOrEmpty(trip.SyncId) &&
                     ((roles.TryGetValue(trip.SyncId, out var role) && role is TripRole.Editor or TripRole.Viewer) ||
                      touchedHere.Contains(trip.SyncId))))
                .ToList();
        }

        private sealed class TripUserRow
        {
            public string TripSyncId { get; set; } = string.Empty;
            public string? UserId { get; set; }
        }

        /// <summary>Leaves the given trips and everything in them out of the restore.</summary>
        internal static void RemoveTrips(BackupData data, IReadOnlySet<int> tripIds)
        {
            var expenseIds = data.Expenses.Where(e => tripIds.Contains(e.TripId)).Select(e => e.Id).ToHashSet();
            data.Trips.RemoveAll(t => tripIds.Contains(t.Id));
            data.Expenses.RemoveAll(e => tripIds.Contains(e.TripId));
            data.ExpenseShares.RemoveAll(s => expenseIds.Contains(s.ExpenseId));
            data.Todos.RemoveAll(t => tripIds.Contains(t.TripId));
            data.Places.RemoveAll(p => tripIds.Contains(p.TripId));
            data.Activities.RemoveAll(a => tripIds.Contains(a.TripId));
            data.Participants.RemoveAll(p => tripIds.Contains(p.TripId));
            data.Attachments.RemoveAll(a => tripIds.Contains(a.TripId));
        }

        /// <summary>
        /// Turns the given trips into new trips of the user's own: every row gets a new SyncId and starts unsynced,
        /// so the upload creates new rows in the cloud instead of overwriting the shared ones. Local ids (and so
        /// every relation inside the trip) stay as they are; the shared original comes back with the next sync
        /// under a new local id. Attachment files are uploaded again for the copy.
        /// </summary>
        internal static void CopySharedTrips(BackupData data, IReadOnlySet<int> tripIds)
        {
            var me = Sync.ChangeTracker.CurrentUserId;
            void Renew(ISyncEntity row)
            {
                row.SyncId = Sync.ChangeTracker.NewSyncId();
                row.Version = 0;
                row.ChangeSeq = 0;
                row.CreatedBy = me;
                row.UpdatedBy = me;
                row.UpdatedAtUtc = DateTime.UtcNow;
            }

            var expenseIds = data.Expenses.Where(e => tripIds.Contains(e.TripId)).Select(e => e.Id).ToHashSet();
            foreach (var trip in data.Trips.Where(t => tripIds.Contains(t.Id)))
            {
                Renew(trip);
                trip.Name = InputLimits.Truncate($"{trip.Name} {Resources.Strings.AppResources.Backup_CopySuffix}", 100)!;
            }

            foreach (var row in data.Expenses.Where(e => tripIds.Contains(e.TripId))) Renew(row);
            foreach (var row in data.ExpenseShares.Where(s => expenseIds.Contains(s.ExpenseId))) Renew(row);
            foreach (var row in data.Todos.Where(t => tripIds.Contains(t.TripId))) Renew(row);
            foreach (var row in data.Places.Where(p => tripIds.Contains(p.TripId))) Renew(row);
            foreach (var row in data.Activities.Where(a => tripIds.Contains(a.TripId))) Renew(row);
            foreach (var row in data.Participants.Where(p => tripIds.Contains(p.TripId))) Renew(row);
            foreach (var row in data.Attachments.Where(a => tripIds.Contains(a.TripId)))
            {
                Renew(row);
                row.RemotePath = null; // the old storage path belongs to the shared trip
            }
        }

        private async Task ReplaceAllAsync(BackupData data)
        {
            var db = await _databaseService.Connection;
            await db.RunInTransactionAsync(conn =>
            {
                foreach (var table in new[] { "ExpenseShare", "Participant", "Attachment", "Activity", "Todo", "Expense", "Place", "PackingTemplate", "Trip" })
                    conn.Execute($"DELETE FROM {table}");

                // G1: the restored data replaces everything, so the old outbox and trash no longer describe it.
                conn.Execute("DELETE FROM PendingChange");
                conn.Execute("DELETE FROM Trash");

                // G4: for a signed-in account the next sync must read the cloud from the start again. Otherwise rows
                // that are in the cloud but not in the backup (added later, or on another device) would never come
                // back to this device - the pull cursor already lies past them. Restored rows keep their local
                // values: their queued uploads (below) win over the server copy.
                conn.Execute("DELETE FROM SyncState WHERE Key = ?", Sync.SyncService.CursorKey);

                // InsertOrReplace keeps the original ids, so every TripId / ExpenseId / PlaceId still points
                // at the right row.
                void Put<T>(IEnumerable<T> rows) where T : class
                {
                    foreach (var row in rows)
                        conn.InsertOrReplace(row);
                }

                Put(data.Trips);
                Put(data.Todos);
                Put(data.Expenses);
                Put(data.Places);
                Put(data.Activities);
                Put(data.PackingTemplates);
                Put(data.Participants);
                Put(data.ExpenseShares);
                Put(data.Attachments);

                // Rows from a v1 backup get their SyncId here; every restored row is queued for upload, so the
                // cloud copy catches up with the restored data on the next sync.
                Sync.ChangeTracker.EnqueueAllRows(conn);
            });
        }

        private void ClearTrashFolder()
        {
            var trash = Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName, AttachmentService.TrashFolderName);
            try
            {
                if (Directory.Exists(trash))
                    Directory.Delete(trash, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackupService] Could not clear the trash folder: {ex}");
            }
        }

        private int RestoreFiles(ZipArchive zip, IReadOnlyList<TripAttachment> attachments)
        {
            var root = Path.GetFullPath(Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName));
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[BackupService] Could not clear attachments: {ex}");
            }

            var restored = 0;
            long written = 0;
            foreach (var attachment in attachments)
            {
                var entry = zip.GetEntry(attachment.RelativePath.Replace('\\', '/'));
                if (entry is null || entry.Length > AttachmentService.MaxFileBytes)
                    continue;

                // Zip-slip guard: the target must stay inside the attachments folder.
                var target = Path.GetFullPath(_attachments.GetFullPath(attachment));
                if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    // TP-72: copy with a real byte limit - per file, and for all files together - instead of trusting
                    // the size the zip claims (ExtractToFile would write whatever the entry inflates to)
                    var limit = Math.Min(AttachmentService.MaxFileBytes, MaxBackupBytes - written);
                    using (var source = new LimitedReadStream(entry.Open(), limit))
                    using (var output = File.Create(target))
                        source.CopyTo(output);
                    written += new FileInfo(target).Length;
                    restored++;
                }
                catch (Exception ex)
                {
                    TryDelete(target); // a half-written or too-large file
                    // The data is already restored; one unreadable file must not fail the whole restore.
                    System.Diagnostics.Debug.WriteLine($"[BackupService] Could not restore {attachment.RelativePath}: {ex}");
                }
            }

            return restored;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>TP-72: a read-only stream that fails once more than <c>maxBytes</c> have come out of it.</summary>
        internal sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
        {
            private long _read;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _read; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

            public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
                Count(await inner.ReadAsync(buffer, cancellationToken));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            private int Count(int read)
            {
                _read += read;
                if (_read > maxBytes)
                    throw new InvalidBackupException("entry larger than allowed");
                return read;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    inner.Dispose();
                base.Dispose(disposing);
            }
        }

        private static async Task CopyWithLimitAsync(Stream source, Stream target, long maxBytes)
        {
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(chunk)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidBackupException("backup file too large");
                await target.WriteAsync(chunk.AsMemory(0, read));
            }
        }

        /// <summary>
        /// Checks a parsed backup before anything is replaced. The file may be old, hand-edited or corrupt, and
        /// its rows go straight into tables the rest of the app trusts - so the same limits the forms and services
        /// enforce apply here (<see cref="EntityValidator"/>, shared with the sync pull): money/rates/dates within
        /// <see cref="InputLimits"/> (otherwise totals, conversions and reminder date math can overflow or crash),
        /// known categories, coordinates on Earth, text trimmed to column limits.
        /// Broken money/dates/coordinates reject the whole backup (nothing has been touched yet); an attachment
        /// whose stored path points outside the attachments folder is dropped, because opening or deleting it
        /// later would touch an arbitrary file.
        /// </summary>
        internal static void Sanitize(BackupData data, string attachmentsRoot)
        {
            data.Trips ??= new();
            data.Todos ??= new();
            data.Expenses ??= new();
            data.Places ??= new();
            data.Activities ??= new();
            data.PackingTemplates ??= new();
            data.Participants ??= new();
            data.ExpenseShares ??= new();
            data.Attachments ??= new();

            if (data.Trips.Any(t => t is null) || data.Expenses.Any(e => e is null) || data.Todos.Any(t => t is null) ||
                data.Places.Any(p => p is null) || data.Activities.Any(a => a is null) || data.PackingTemplates.Any(t => t is null) ||
                data.Participants.Any(p => p is null) || data.ExpenseShares.Any(s => s is null) || data.Attachments.Any(a => a is null))
                throw new InvalidBackupException("null rows");

            // SEC-01: the same rules as rows pulled from the server (EntityValidator); here a broken row rejects
            // the whole backup, because nothing has been replaced yet and a half-restored backup helps nobody.
            Check(data.Trips, "trip", t => t.Id);
            Check(data.Expenses, "expense", e => e.Id);
            Check(data.Places, "place", p => p.Id);
            Check(data.Activities, "activity", a => a.Id);
            Check(data.Todos, "todo", t => t.Id);
            Check(data.Participants, "participant", p => p.Id);
            Check(data.ExpenseShares, "expense share", s => s.Id);
            Check(data.PackingTemplates, "packing template", t => t.Id);

            data.Attachments = data.Attachments.Where(a => IsSafeAttachmentPath(a.RelativePath, attachmentsRoot)).ToList();
            Check(data.Attachments, "attachment", a => a.Id);
        }

        private static void Check<T>(IEnumerable<T> rows, string what, Func<T, int> id) where T : class
        {
            foreach (var row in rows)
            {
                if (EntityValidator.Validate(row) is { } problem)
                    throw new InvalidBackupException($"{what} {id(row)}: {problem}");
            }
        }

        /// <summary>"attachments/&lt;tripId&gt;/&lt;file&gt;" that resolves inside the attachments folder - nothing else.</summary>
        internal static bool IsSafeAttachmentPath(string? relativePath, string attachmentsRoot)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Length > 260 || Path.IsPathRooted(relativePath))
                return false;

            var normalized = relativePath.Replace('\\', '/');
            if (!normalized.StartsWith(AttachmentService.FolderName + "/", StringComparison.Ordinal) ||
                normalized.Split('/').Any(part => part is "" or "." or ".."))
                return false;

            var root = Path.GetFullPath(attachmentsRoot);
            var full = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(root)!, normalized.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static bool IsZip(Stream stream)
        {
            Span<byte> header = stackalloc byte[4];
            var read = stream.Read(header);
            stream.Position = 0;
            return read == 4 && header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
        }
    }
}
