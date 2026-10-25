using System.Text.Json;
using SQLite;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class TrashService : ITrashService
    {
        /// <summary>How long deleted items can be restored.</summary>
        public const int RetentionDays = 30;

        /// <summary>How long removing cloud copies of deleted files may take before it is left for later.</summary>
        private static readonly TimeSpan CloudTimeout = TimeSpan.FromSeconds(20);

        private readonly IDatabaseService _databaseService;
        private readonly IAttachmentService _attachments;
        private readonly ISupabaseClient? _cloud;
        private readonly IAuthService? _auth;

        /// <param name="cloud">
        /// When given (and signed in), the cloud copies of attachment files are removed together with the trash
        /// entries - after that nothing could restore them. Null = local only (tests, guests).
        /// </param>
        public TrashService(IDatabaseService databaseService, IAttachmentService attachments,
                            ISupabaseClient? cloud = null, IAuthService? auth = null)
        {
            _databaseService = databaseService;
            _attachments = attachments;
            _cloud = cloud;
            _auth = auth;
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public async Task<List<TrashEntry>> GetEntriesAsync(string? tripSyncId = null)
        {
            var db = await _databaseService.Connection;
            var items = tripSyncId is null
                ? await db.Table<TrashItem>().ToListAsync()
                : await db.Table<TrashItem>().Where(t => t.TripSyncId == tripSyncId).ToListAsync();

            // Batches without a root (e.g. split shares replaced while editing an expense) are bookkeeping,
            // not something the user deleted - they are not listed.
            return items
                .GroupBy(i => i.BatchId)
                .Select(g => (Root: g.FirstOrDefault(i => i.IsRoot), Count: g.Count()))
                .Where(x => x.Root is not null)
                .Select(x => (Root: x.Root!, x.Count))
                .Select(x => new TrashEntry(x.Root.BatchId, x.Root.EntityType, x.Root.Title, x.Root.TripSyncId,
                                            x.Root.DeletedAtUtc, x.Root.DeletedBy, x.Count) { RootItemId = x.Root.Id })
                .OrderByDescending(e => e.DeletedAtUtc)
                .ToList();
        }

        public async Task<int> RestoreAsync(string batchId)
        {
            var db = await _databaseService.Connection;
            var restored = 0;
            var restoredAttachments = new List<TripAttachment>();

            await db.RunInTransactionAsync(conn =>
            {
                var items = conn.Table<TrashItem>().Where(t => t.BatchId == batchId).ToList();

                // Parents first (trip before its expenses, expenses before their shares …).
                var rows = items
                    .Select(item => (Item: item, Info: SyncSchema.TryForEntityType(item.EntityType, out var info) ? info : null))
                    .Where(x => x.Info is not null)
                    .OrderBy(x => x.Info!.Rank)
                    .ThenBy(x => x.Item.Id);

                foreach (var (item, maybeInfo) in rows)
                {
                    var info = maybeInfo!;
                    if (Deserialize(item, info) is not { } entity)
                        continue;

                    var map = conn.GetMapping(info.ClrType);
                    var existing = conn.Find(entity.Id, map) as ISyncEntity;
                    if (existing is not null)
                        continue; // already back (or the id is taken) - never overwrite another row
                    if (!string.IsNullOrEmpty(entity.SyncId) &&
                        conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{map.TableName}\" WHERE SyncId = ?", entity.SyncId) > 0)
                        continue; // TP-66: someone else already brought it back (it came in with the sync)

                    if (!ResolveReferences(conn, info, entity))
                        continue;

                    conn.RestoreTracked(entity);
                    restored++;

                    if (entity is TripAttachment attachment)
                        restoredAttachments.Add(attachment);
                }

                conn.Execute("DELETE FROM Trash WHERE BatchId = ?", batchId);
            });

            foreach (var attachment in restoredAttachments)
                _attachments.RestoreFileFromTrash(attachment);

            return restored;
        }

        public async Task<int> DeleteForeverAsync(IReadOnlyCollection<string> batchIds)
        {
            ArgumentNullException.ThrowIfNull(batchIds);
            var db = await _databaseService.Connection;

            var items = new List<TrashItem>();
            foreach (var batchId in batchIds.Where(id => !string.IsNullOrEmpty(id)).Distinct())
                items.AddRange(await db.Table<TrashItem>().Where(t => t.BatchId == batchId).ToListAsync());

            await RemoveForGoodAsync(db, items);
            return items.Count;
        }

        public async Task<int> PurgeExpiredAsync()
        {
            var db = await _databaseService.Connection;
            var cutoff = UtcNow().AddDays(-RetentionDays);
            var expired = await db.Table<TrashItem>().Where(t => t.DeletedAtUtc < cutoff).ToListAsync();

            await RemoveForGoodAsync(db, expired);
            return expired.Count;
        }

        /// <summary>
        /// The one way trash rows leave for good (expired or "Kalıcı olarak sil"): the rows, the attachment files
        /// kept for restoring, and - signed in - the cloud copies of those files.
        /// </summary>
        private async Task RemoveForGoodAsync(SQLiteAsyncConnection db, IReadOnlyList<TrashItem> items)
        {
            if (items.Count == 0)
                return;

            var attachmentInfo = SyncSchema.For<TripAttachment>();
            var attachments = items
                .Where(i => i.EntityType == attachmentInfo.EntityType)
                .Select(i => (Item: i, Attachment: Deserialize(i, attachmentInfo) as TripAttachment))
                .Where(x => x.Attachment is not null)
                .Select(x => (x.Item.TripSyncId, Attachment: x.Attachment!))
                .ToList();

            var ids = items.Select(i => i.Id).ToList();
            await db.RunInTransactionAsync(conn =>
            {
                foreach (var id in ids)
                    conn.Delete<TrashItem>(id);
            });

            foreach (var (_, attachment) in attachments)
                _attachments.DeleteTrashedFile(attachment);

            await DeleteCloudFilesAsync(attachments);
        }

        /// <summary>
        /// Best effort: offline (or with a trip the account can no longer change) the cloud file simply stays; it can't
        /// be reached from the app any more either way.
        /// </summary>
        private async Task DeleteCloudFilesAsync(IReadOnlyList<(string? TripSyncId, TripAttachment Attachment)> attachments)
        {
            if (_cloud is null || _auth?.IsSignedIn != true)
                return;

            var paths = attachments
                .Where(a => !string.IsNullOrEmpty(a.TripSyncId) && !string.IsNullOrEmpty(a.Attachment.SyncId))
                .Select(a => SyncService.RemotePathFor(a.TripSyncId!, a.Attachment))
                .Distinct()
                .ToList();
            if (paths.Count == 0)
                return;

            try
            {
                using var timeout = new CancellationTokenSource(CloudTimeout);
                await _cloud.DeleteAsync(SyncService.Bucket, paths, timeout.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrashService] Cloud files not removed: {ex.Message}");
            }
        }

        private static ISyncEntity? Deserialize(TrashItem item, SyncEntityInfo info)
        {
            try
            {
                return JsonSerializer.Deserialize(item.Json, info.ClrType, BackupService.JsonOptions) as ISyncEntity;
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrashService] Unreadable trash item {item.Id}: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Every integer relation of <paramref name="entity"/> must point at an existing row. An optional one
        /// (activity → place, expense → payer) that doesn't is cleared; a required one (→ trip, → expense) makes
        /// the row unrestorable.
        /// </summary>
        private static bool ResolveReferences(SQLiteConnection conn, SyncEntityInfo info, ISyncEntity entity)
        {
            foreach (var key in info.ForeignKeys)
            {
                var property = info.ClrType.GetProperty(key.PropertyName);
                if (property?.GetValue(entity) is not int id)
                    continue;

                var table = SyncSchema.For(key.Target).EntityType;
                var exists = conn.ExecuteScalar<int>($"SELECT COUNT(*) FROM \"{table}\" WHERE Id = ?", id) > 0;
                if (exists)
                    continue;

                if (!key.IsNullable)
                    return false;

                property!.SetValue(entity, null);
            }

            return true;
        }
    }
}
