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

        private readonly IDatabaseService _databaseService;
        private readonly IAttachmentService _attachments;

        public TrashService(IDatabaseService databaseService, IAttachmentService attachments)
        {
            _databaseService = databaseService;
            _attachments = attachments;
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
                                            x.Root.DeletedAtUtc, x.Root.DeletedBy, x.Count))
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

                    var existing = conn.Find(entity.Id, conn.GetMapping(info.ClrType)) as ISyncEntity;
                    if (existing is not null)
                        continue; // already back (or the id is taken) - never overwrite another row

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

        public async Task<int> PurgeExpiredAsync()
        {
            var db = await _databaseService.Connection;
            var cutoff = UtcNow().AddDays(-RetentionDays);
            var expired = await db.Table<TrashItem>().Where(t => t.DeletedAtUtc < cutoff).ToListAsync();
            if (expired.Count == 0)
                return 0;

            var attachmentType = SyncSchema.For<TripAttachment>().EntityType;
            foreach (var item in expired.Where(i => i.EntityType == attachmentType))
            {
                if (Deserialize(item, SyncSchema.For<TripAttachment>()) is TripAttachment attachment)
                    _attachments.DeleteTrashedFile(attachment);
            }

            await db.ExecuteAsync("DELETE FROM Trash WHERE DeletedAtUtc < ?", cutoff);
            return expired.Count;
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
