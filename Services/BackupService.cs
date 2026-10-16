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

        public async Task<string> ExportAsync()
        {
            var data = await ReadAllAsync();
            Directory.CreateDirectory(_outputDirectory);
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

        public async Task<BackupImportResult> ImportAsync(Stream content, string fileName)
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
                    using var stream = entry.Open();
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

            using (zip)
            {
                if (data.Format != FormatName || data.Version < 1 || data.Version > FormatVersion)
                    throw new InvalidBackupException($"unsupported format {data.Format} v{data.Version}");

                Sanitize(data, Path.GetFullPath(Path.Combine(_attachments.RootDirectory, AttachmentService.FolderName)));

                await ReplaceAllAsync(data);
                ClearTrashFolder();
                var restoredFiles = zip is null ? 0 : RestoreFiles(zip, data.Attachments);
                return new BackupImportResult(data.Trips.Count, data.Expenses.Count, restoredFiles, data.ExportedAt);
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
                    entry.ExtractToFile(target, overwrite: true);
                    restored++;
                }
                catch (Exception ex)
                {
                    // The data is already restored; one unreadable file must not fail the whole restore.
                    System.Diagnostics.Debug.WriteLine($"[BackupService] Could not restore {attachment.RelativePath}: {ex}");
                }
            }

            return restored;
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
        /// enforce apply here: money/rates/dates within <see cref="InputLimits"/> (otherwise totals, conversions and
        /// reminder date math can overflow or crash), coordinates on Earth, text trimmed to column limits.
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

            foreach (var trip in data.Trips)
            {
                InputLimits.EnforceTextLimits(trip);
                trip.StartDate = trip.StartDate.Date;
                trip.EndDate = trip.EndDate.Date;
                if (!InputLimits.IsValidDate(trip.StartDate) || !InputLimits.IsValidDate(trip.EndDate) || trip.EndDate < trip.StartDate)
                    throw new InvalidBackupException($"trip {trip.Id}: invalid dates");
                if (!InputLimits.IsValidMinor(trip.PlannedBudgetMinor))
                    throw new InvalidBackupException($"trip {trip.Id}: invalid budget");
                if (trip.CurrencyCode is not null && !Helpers.MoneyFormatter.IsSupported(trip.CurrencyCode))
                    trip.CurrencyCode = null; // falls back to the default currency, like pre-TP-403 trips
            }

            foreach (var expense in data.Expenses)
            {
                InputLimits.EnforceTextLimits(expense);
                if (!InputLimits.IsValidMinor(expense.AmountMinor) || !InputLimits.IsValidMinor(expense.OriginalAmountMinor))
                    throw new InvalidBackupException($"expense {expense.Id}: invalid amount");
                if (!InputLimits.IsValidRate(expense.ExchangeRate))
                    throw new InvalidBackupException($"expense {expense.Id}: invalid exchange rate");
                if (!InputLimits.IsValidDate(expense.Date))
                    throw new InvalidBackupException($"expense {expense.Id}: invalid date");
                if (expense.OriginalCurrencyCode is not null && !Helpers.MoneyFormatter.IsSupported(expense.OriginalCurrencyCode))
                    throw new InvalidBackupException($"expense {expense.Id}: unknown currency");
            }

            foreach (var place in data.Places)
            {
                InputLimits.EnforceTextLimits(place);
                if (!PlaceService.IsValidCoordinate(place.Latitude, place.Longitude))
                    throw new InvalidBackupException($"place {place.Id}: invalid coordinates");
            }

            foreach (var activity in data.Activities)
            {
                InputLimits.EnforceTextLimits(activity);
                activity.Date = activity.Date.Date;
                if (!InputLimits.IsValidDate(activity.Date))
                    throw new InvalidBackupException($"activity {activity.Id}: invalid date");
                if (activity.StartTime is { } start && (start < TimeSpan.Zero || start >= TimeSpan.FromDays(1)))
                    activity.StartTime = null;
                if (activity.StartTime is null || activity.EndTime is { } end && (end < TimeSpan.Zero || end >= TimeSpan.FromDays(1)))
                    activity.EndTime = null;
            }

            foreach (var todo in data.Todos)
                InputLimits.EnforceTextLimits(todo);
            foreach (var participant in data.Participants)
                InputLimits.EnforceTextLimits(participant);
            foreach (var template in data.PackingTemplates)
            {
                InputLimits.EnforceTextLimits(template);
                template.Items ??= string.Empty;
            }

            data.Attachments = data.Attachments.Where(a => IsSafeAttachmentPath(a.RelativePath, attachmentsRoot)).ToList();
            foreach (var attachment in data.Attachments)
                InputLimits.EnforceTextLimits(attachment);
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
