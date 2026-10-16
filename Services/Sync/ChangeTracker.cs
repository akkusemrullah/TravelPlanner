using System.Text.Json;
using SQLite;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    /// <summary>
    /// Rows deleted by one user action (G1). Deleting a trip puts the trip and everything in it into one batch, so
    /// restoring the trip from the trash brings all of it back together.
    /// </summary>
    public sealed class TrashBatch
    {
        private TrashBatch(Type? rootType, int rootId)
        {
            RootType = rootType;
            RootId = rootId;
        }

        public string Id { get; } = Guid.NewGuid().ToString();

        public Type? RootType { get; }

        public int RootId { get; }

        /// <summary>A batch whose root is <paramref name="root"/> (the row the user chose to delete).</summary>
        public static TrashBatch For(ISyncEntity root) => new(root.GetType(), root.Id);

        /// <summary>A batch whose root is the <typeparamref name="T"/> row with <paramref name="id"/>.</summary>
        public static TrashBatch For<T>(int id) where T : ISyncEntity => new(typeof(T), id);

        internal bool IsRoot(ISyncEntity entity) => entity.GetType() == RootType && entity.Id == RootId;
    }

    /// <summary>
    /// The only way synced rows are written (G1). Every insert, update and delete of a <see cref="ISyncEntity"/>
    /// goes through here, inside the caller's SQLite transaction, and does three things at once:
    /// <list type="bullet">
    /// <item>keeps the sync metadata right (SyncId on insert, who/when changed it; identity fields can't be
    /// overwritten by a form that built a fresh object),</item>
    /// <item>queues the change in the <see cref="PendingChange"/> outbox - for an update only the fields that really
    /// changed, so two people editing different fields of the same expense offline both keep their edit,</item>
    /// <item>on delete, copies the row into the trash (<see cref="TrashItem"/>) so it can be restored.</item>
    /// </list>
    /// The methods on <see cref="SQLiteConnection"/> must be called inside a transaction
    /// (<c>RunInTransactionAsync</c>); the <see cref="SQLiteAsyncConnection"/> helpers open one themselves.
    /// </summary>
    public static class ChangeTracker
    {
        /// <summary>
        /// Account (Supabase auth uid) of the signed-in user, stamped on CreatedBy/UpdatedBy. Null while using the
        /// app as a guest. Set by the auth service (G3) on sign-in / sign-out.
        /// </summary>
        public static string? CurrentUserId { get; set; }

        /// <summary>
        /// Raised whenever a change was queued (G4 schedules a sync a few seconds later). Fired inside the writer's
        /// transaction - handlers must only schedule work, never touch the database.
        /// </summary>
        public static event Action? LocalChangeQueued;

        private static void RaiseLocalChangeQueued()
        {
            try
            {
                LocalChangeQueued?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChangeTracker] LocalChangeQueued handler failed: {ex}");
            }
        }

        /// <summary>Clock used for UpdatedAtUtc / trash dates (replaceable in tests).</summary>
        internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Properties that are bookkeeping, not data: never diffed, never taken from a caller's object.</summary>
        private static readonly HashSet<string> MetadataProperties = new(StringComparer.Ordinal)
        {
            nameof(ISyncEntity.Id),
            nameof(ISyncEntity.SyncId),
            nameof(ISyncEntity.CreatedBy),
            nameof(ISyncEntity.UpdatedBy),
            nameof(ISyncEntity.UpdatedAtUtc),
            nameof(ISyncEntity.Version),
            nameof(ISyncEntity.ChangeSeq),
        };

        public static string NewSyncId() => Guid.NewGuid().ToString();

        // ------------------------------------------------------------------ writes (inside a transaction)

        /// <summary>Inserts a new row (its Id is filled in, as with Insert) and queues it as a full upsert.</summary>
        public static int InsertTracked(this SQLiteConnection conn, ISyncEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            var info = SyncSchema.For(entity.GetType());

            if (string.IsNullOrEmpty(entity.SyncId))
                entity.SyncId = NewSyncId();
            entity.CreatedBy ??= CurrentUserId;
            entity.UpdatedBy = CurrentUserId ?? entity.UpdatedBy;
            entity.UpdatedAtUtc = UtcNow();
            entity.Version = 0;
            entity.ChangeSeq = 0;

            EnsureAllowed(conn, info.ResolveTripSyncId(conn, entity), entity, isDelete: false);

            var result = conn.Insert(entity);
            EnqueueUpsert(conn, info, entity, changedFields: null);
            return result;
        }

        /// <summary>
        /// Updates an existing row and queues the fields that actually changed. Returns 0 (and changes nothing)
        /// when the row no longer exists. An update that changes nothing is written but not queued.
        /// </summary>
        public static int UpdateTracked(this SQLiteConnection conn, ISyncEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            var info = SyncSchema.For(entity.GetType());
            var map = conn.GetMapping(entity.GetType());

            if (conn.Find(entity.Id, map) is not ISyncEntity stored)
                return 0;

            // Identity and server bookkeeping always come from the stored row: forms often build a fresh object
            // with just the Id for an edit, and that must not wipe the row's SyncId or version.
            entity.SyncId = stored.SyncId;
            entity.CreatedBy = stored.CreatedBy;
            entity.UpdatedBy = stored.UpdatedBy;
            entity.UpdatedAtUtc = stored.UpdatedAtUtc;
            entity.Version = stored.Version;
            entity.ChangeSeq = stored.ChangeSeq;

            List<string>? changed;
            if (string.IsNullOrEmpty(stored.SyncId))
            {
                // Should not happen after migration 3 - but never queue a row without an identity.
                entity.SyncId = NewSyncId();
                changed = null;
            }
            else
            {
                changed = ChangedProperties(map, stored, entity);
                if (changed.Count == 0)
                    return conn.Update(entity);
            }

            EnsureAllowed(conn, info.ResolveTripSyncId(conn, entity), entity, isDelete: false);

            entity.UpdatedBy = CurrentUserId ?? stored.UpdatedBy;
            entity.UpdatedAtUtc = UtcNow();

            var result = conn.Update(entity);
            EnqueueUpsert(conn, info, entity, changed);
            return result;
        }

        /// <summary>Insert when <c>Id == 0</c>, otherwise update (the services' usual "save").</summary>
        public static int SaveTracked(this SQLiteConnection conn, ISyncEntity entity) =>
            entity.Id == 0 ? conn.InsertTracked(entity) : conn.UpdateTracked(entity);

        /// <summary>
        /// Deletes the row: it goes into the trash (batch <paramref name="batch"/>) and a delete is queued.
        /// Returns 0 when the row no longer exists.
        /// </summary>
        public static int DeleteTracked(this SQLiteConnection conn, ISyncEntity entity, TrashBatch? batch = null)
        {
            ArgumentNullException.ThrowIfNull(entity);
            var map = conn.GetMapping(entity.GetType());
            return conn.Find(entity.Id, map) is ISyncEntity stored
                ? DeleteStored(conn, stored, batch ?? TrashBatch.For(entity))
                : 0;
        }

        /// <summary>Deletes the <typeparamref name="T"/> row with <paramref name="id"/> (see <see cref="DeleteTracked(SQLiteConnection, ISyncEntity, TrashBatch?)"/>).</summary>
        public static int DeleteTracked<T>(this SQLiteConnection conn, int id, TrashBatch? batch = null) where T : ISyncEntity
        {
            var map = conn.GetMapping(typeof(T));
            return conn.Find(id, map) is ISyncEntity stored
                ? DeleteStored(conn, stored, batch ?? TrashBatch.For<T>(id))
                : 0;
        }

        /// <summary>Deletes every <typeparamref name="T"/> row matching <paramref name="predicate"/> into <paramref name="batch"/>.</summary>
        public static int DeleteAllTracked<T>(this SQLiteConnection conn, System.Linq.Expressions.Expression<Func<T, bool>> predicate, TrashBatch batch)
            where T : class, ISyncEntity, new()
        {
            var deleted = 0;
            foreach (var row in conn.Table<T>().Where(predicate).ToList())
                deleted += DeleteStored(conn, row, batch);
            return deleted;
        }

        /// <summary>Deletes the given (already loaded) rows into <paramref name="batch"/>.</summary>
        public static int DeleteAllTracked(this SQLiteConnection conn, IEnumerable<ISyncEntity> rows, TrashBatch batch)
        {
            var deleted = 0;
            foreach (var row in rows.ToList())
                deleted += conn.DeleteTracked(row, batch);
            return deleted;
        }

        /// <summary>
        /// Puts a row from the trash back with its original Id and SyncId (so relations still match) and queues
        /// it as a full upsert.
        /// </summary>
        public static int RestoreTracked(this SQLiteConnection conn, ISyncEntity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);
            var info = SyncSchema.For(entity.GetType());

            if (string.IsNullOrEmpty(entity.SyncId))
                entity.SyncId = NewSyncId();
            entity.UpdatedBy = CurrentUserId ?? entity.UpdatedBy;
            entity.UpdatedAtUtc = UtcNow();

            EnsureAllowed(conn, info.ResolveTripSyncId(conn, entity), entity, isDelete: false);

            var result = conn.InsertOrReplace(entity);
            EnqueueUpsert(conn, info, entity, changedFields: null);
            return result;
        }

        // ------------------------------------------------------------------ async helpers (own transaction)

        public static async Task<int> InsertTrackedAsync(this SQLiteAsyncConnection db, ISyncEntity entity)
        {
            var result = 0;
            await db.RunInTransactionAsync(conn => result = conn.InsertTracked(entity));
            return result;
        }

        public static async Task<int> UpdateTrackedAsync(this SQLiteAsyncConnection db, ISyncEntity entity)
        {
            var result = 0;
            await db.RunInTransactionAsync(conn => result = conn.UpdateTracked(entity));
            return result;
        }

        public static async Task<int> SaveTrackedAsync(this SQLiteAsyncConnection db, ISyncEntity entity)
        {
            var result = 0;
            await db.RunInTransactionAsync(conn => result = conn.SaveTracked(entity));
            return result;
        }

        public static async Task<int> DeleteTrackedAsync(this SQLiteAsyncConnection db, ISyncEntity entity, TrashBatch? batch = null)
        {
            var result = 0;
            await db.RunInTransactionAsync(conn => result = conn.DeleteTracked(entity, batch));
            return result;
        }

        // ------------------------------------------------------------------ bulk (migration / backup restore)

        /// <summary>
        /// Gives every row without a SyncId one and queues every row of every synced table as a full upsert,
        /// parents first. Used once when upgrading an existing database (migration 3) and after restoring a
        /// backup - afterwards the outbox describes the whole local database.
        /// </summary>
        internal static int EnqueueAllRows(SQLiteConnection conn)
        {
            var queued = 0;
            foreach (var info in SyncSchema.Entities.OrderBy(e => e.Rank))
            {
                var map = conn.GetMapping(info.ClrType);
                foreach (var row in conn.Query(map, $"SELECT * FROM \"{map.TableName}\" ORDER BY Id").Cast<ISyncEntity>())
                {
                    if (string.IsNullOrEmpty(row.SyncId))
                    {
                        row.SyncId = NewSyncId();
                        row.UpdatedAtUtc = UtcNow();
                        conn.Update(row);
                    }

                    EnqueueUpsert(conn, info, row, changedFields: null);
                    queued++;
                }
            }

            return queued;
        }

        // ------------------------------------------------------------------ internals

        private static int DeleteStored(SQLiteConnection conn, ISyncEntity stored, TrashBatch batch)
        {
            var info = SyncSchema.For(stored.GetType());
            var tripSyncId = info.ResolveTripSyncId(conn, stored); // before the row (or its trip) is gone
            EnsureAllowed(conn, tripSyncId, stored, isDelete: true);

            if (string.IsNullOrEmpty(stored.SyncId))
                stored.SyncId = NewSyncId();

            conn.Insert(new TrashItem
            {
                BatchId = batch.Id,
                IsRoot = batch.IsRoot(stored),
                EntityType = info.EntityType,
                EntitySyncId = stored.SyncId,
                TripSyncId = tripSyncId,
                Title = Helpers.InputLimits.Truncate(info.ResolveTitle(stored), 150),
                Json = JsonSerializer.Serialize(stored, stored.GetType(), BackupService.JsonOptions),
                DeletedAtUtc = UtcNow(),
                DeletedBy = CurrentUserId,
            });

            var result = conn.Delete(stored);
            EnqueueDelete(conn, info, stored, tripSyncId);
            return result;
        }

        /// <summary>
        /// G6: a viewer may not change anything in a shared trip, and only its owner may delete the trip itself.
        /// The role comes from the TripMembership table the sync engine keeps; no row = the user's own trip.
        /// Guests (no account) have no restrictions.
        /// </summary>
        private static void EnsureAllowed(SQLiteConnection conn, string? tripSyncId, ISyncEntity entity, bool isDelete)
        {
            if (CurrentUserId is null || string.IsNullOrEmpty(tripSyncId))
                return;

            var role = TripRoles.Parse(conn.ExecuteScalar<string>("SELECT Role FROM TripMembership WHERE TripSyncId = ?", tripSyncId));
            if (role is null or TripRole.Owner)
                return;
            if (role == TripRole.Viewer)
                throw new TripAccessException(TripAccessDenied.ReadOnly);
            if (isDelete && entity is Trip)
                throw new TripAccessException(TripAccessDenied.OwnerOnly);
        }

        private static List<string> ChangedProperties(TableMapping map, object stored, object current)
        {
            var changed = new List<string>();
            foreach (var column in map.Columns)
            {
                if (column.IsPK || MetadataProperties.Contains(column.PropertyName))
                    continue;

                if (!Equals(column.GetValue(stored), column.GetValue(current)))
                    changed.Add(column.PropertyName);
            }

            return changed;
        }

        private static PendingChange? FindPending(SQLiteConnection conn, string entityType, string syncId) =>
            conn.Table<PendingChange>()
                .Where(p => p.EntityType == entityType && p.EntitySyncId == syncId)
                .FirstOrDefault();

        private static void EnqueueUpsert(SQLiteConnection conn, SyncEntityInfo info, ISyncEntity entity, IReadOnlyCollection<string>? changedFields)
        {
            var now = UtcNow();
            var tripSyncId = info.ResolveTripSyncId(conn, entity);
            var existing = FindPending(conn, info.EntityType, entity.SyncId);

            if (existing is { Operation: ChangeOperation.Upsert })
            {
                // Edited again before the last edit was sent: one entry, fields unioned (null = all fields wins).
                existing.ChangedFields = MergeFields(existing.ChangedFields, changedFields);
                existing.TripSyncId = tripSyncId;
                existing.UpdatedAtUtc = now;
                conn.Update(existing);
                RaiseLocalChangeQueued();
                return;
            }

            if (existing is not null)
            {
                // Restored before its delete was sent: drop the delete and queue a full upsert at the END of the
                // queue, so it is sent after the parents restored together with it.
                conn.Delete(existing);
                changedFields = null;
            }

            conn.Insert(new PendingChange
            {
                EntityType = info.EntityType,
                EntitySyncId = entity.SyncId,
                TripSyncId = tripSyncId,
                Operation = ChangeOperation.Upsert,
                ChangedFields = changedFields is null ? null : string.Join(",", changedFields),
                BaseVersion = entity.Version,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            RaiseLocalChangeQueued();
        }

        private static void EnqueueDelete(SQLiteConnection conn, SyncEntityInfo info, ISyncEntity entity, string? tripSyncId)
        {
            var existing = FindPending(conn, info.EntityType, entity.SyncId);
            if (existing is { Operation: ChangeOperation.Delete })
                return;

            // A pending upsert is pointless once the row is gone. The delete is always queued - even for a row that
            // may never have reached the server: deleting an unknown row is a no-op there, while skipping it could
            // leave an orphan behind if an upload was already on its way.
            if (existing is not null)
                conn.Delete(existing);

            var now = UtcNow();
            conn.Insert(new PendingChange
            {
                EntityType = info.EntityType,
                EntitySyncId = entity.SyncId,
                TripSyncId = tripSyncId,
                Operation = ChangeOperation.Delete,
                BaseVersion = entity.Version,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            });
            RaiseLocalChangeQueued();
        }

        internal static string? MergeFields(string? existing, IReadOnlyCollection<string>? added)
        {
            if (existing is null || added is null)
                return null;

            var fields = existing.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var field in added)
            {
                if (!fields.Contains(field, StringComparer.Ordinal))
                    fields.Add(field);
            }

            return string.Join(",", fields);
        }
    }
}
