using System.Text.Json;
using SQLite;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    /// <summary>
    /// Writes the answer of pull_changes into the local database (G4), inside one transaction. These writes do NOT go
    /// through ChangeTracker: they come from the server, so nothing is queued and nothing goes to the trash.
    /// Rules:
    /// - a row with deleted_at is deleted locally (a trip with everything in it);
    /// - a row we already have at the same or a newer change_seq is skipped (e.g. the echo of our own push);
    /// - fields with a local edit that hasn't been uploaded yet keep the local value (it goes up next and wins there);
    /// - a row we deleted locally (delete still queued) is not brought back;
    /// - trips that no longer appear in memberships (removed from the trip) are removed locally;
    /// - SEC-01: every row is checked like a restored backup (<see cref="EntityValidator"/>). A row that can't be
    ///   read or breaks a limit is logged and skipped on its own (inside a savepoint, so nothing half-written stays);
    ///   the rest of the answer is applied and the cursor still moves past it. Otherwise one bad row - written by
    ///   any editor straight through the REST API - would roll back every pull, forever, for every member.
    /// </summary>
    public static class RemoteApplier
    {
        public sealed class Outcome
        {
            public int Changed { get; set; }

            /// <summary>Rows that couldn't be stored because their trip/parent isn't here (yet).</summary>
            public int Skipped { get; set; }

            /// <summary>SEC-01: rows that couldn't be read or broke a limit, and were left out ("Expense &lt;id&gt;: invalid amount").</summary>
            public List<string> Invalid { get; } = new();

            public HashSet<int> ChangedTripIds { get; } = new();

            public HashSet<int> RemovedTripIds { get; } = new();

            /// <summary>Attachment files (relative paths) whose rows were removed.</summary>
            public List<string> FilesToDelete { get; } = new();

            public long? Cursor { get; set; }
        }

        public static Outcome Apply(SQLiteConnection conn, JsonElement response)
        {
            var outcome = new Outcome();
            if (response.ValueKind != JsonValueKind.Object)
                return outcome;

            // 1) memberships (roles; and which trips we still have access to)
            HashSet<string>? memberTrips = null;
            if (response.TryGetProperty("memberships", out var memberships) && memberships.ValueKind == JsonValueKind.Array)
            {
                memberTrips = new HashSet<string>(StringComparer.Ordinal);
                conn.Execute("DELETE FROM TripMembership");
                foreach (var membership in memberships.EnumerateArray())
                {
                    var tripSyncId = SyncFieldMapper.GetString(membership, "trip_id");
                    if (string.IsNullOrEmpty(tripSyncId))
                        continue;
                    memberTrips.Add(tripSyncId);
                    conn.InsertOrReplace(new TripMembership { TripSyncId = tripSyncId, Role = SyncFieldMapper.GetString(membership, "role") ?? string.Empty });
                }
            }

            // 2) rows, parents first
            foreach (var info in SyncSchema.Entities.OrderBy(e => e.Rank))
            {
                if (!response.TryGetProperty(SyncSchema.ServerTable(info), out var rows) || rows.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var row in rows.EnumerateArray())
                    ApplyRowSafely(conn, info, row, outcome);
            }

            // 3) trips we were removed from (only ones the server had seen - never a trip still waiting to be uploaded)
            if (memberTrips is not null)
            {
                foreach (var trip in conn.Query<Trip>("SELECT * FROM Trip WHERE ChangeSeq > 0"))
                {
                    if (!memberTrips.Contains(trip.SyncId))
                    {
                        RemoveTrip(conn, trip, outcome);
                        outcome.Changed++;
                    }
                }
            }

            // 4) cursor
            if (response.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Number &&
                cursor.TryGetInt64(out var nextCursor) && nextCursor >= 0)
            {
                outcome.Cursor = nextCursor;
                conn.InsertOrReplace(new SyncStateEntry
                {
                    Key = SyncService.CursorKey,
                    Value = outcome.Cursor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }

            return outcome;
        }

        /// <summary>
        /// One row in its own savepoint: a row that throws (a date PostgreSQL accepts but DateTime can't hold, a
        /// number too big for its column, an unknown enum …) is rolled back alone and counted in
        /// <see cref="Outcome.Invalid"/>; the pull goes on. Errors of the database itself (disk full, I/O …) are not
        /// the row's fault - they still fail the whole pull, so nothing is skipped by accident.
        /// </summary>
        private static void ApplyRowSafely(SQLiteConnection conn, SyncEntityInfo info, JsonElement row, Outcome outcome)
        {
            var savepoint = conn.SaveTransactionPoint();
            var changed = outcome.Changed;
            var skipped = outcome.Skipped;
            var changedTrips = outcome.ChangedTripIds.ToArray();
            var removedTrips = outcome.RemovedTripIds.ToArray();
            var files = outcome.FilesToDelete.Count;
            try
            {
                ApplyRow(conn, info, row, outcome);
                conn.Release(savepoint);
            }
            catch (Exception ex) when (IsBadRow(ex))
            {
                conn.RollbackTo(savepoint);
                conn.Execute("RELEASE " + savepoint); // RollbackTo keeps the savepoint open; close it (depth is already back)

                // what the rolled-back row had added to the outcome no longer happened
                outcome.Changed = changed;
                outcome.Skipped = skipped;
                outcome.ChangedTripIds.Clear();
                outcome.ChangedTripIds.UnionWith(changedTrips);
                outcome.RemovedTripIds.Clear();
                outcome.RemovedTripIds.UnionWith(removedTrips);
                outcome.FilesToDelete.RemoveRange(files, outcome.FilesToDelete.Count - files);

                Reject(info, row, $"{ex.GetType().Name}: {ex.Message}", outcome);
            }
        }

        private static bool IsBadRow(Exception ex) =>
            ex is not (OutOfMemoryException or OperationCanceledException) &&
            (ex is not SQLiteException sqlite || sqlite.Result == SQLite3.Result.Constraint);

        private static void Reject(SyncEntityInfo info, JsonElement row, string reason, Outcome outcome)
        {
            var id = row.ValueKind == JsonValueKind.Object && row.TryGetProperty("id", out var value) ? value.ToString() : "?";
            if (id.Length > 64)
                id = id[..64];
            var entry = $"{info.EntityType} {id}: {reason}";
            outcome.Invalid.Add(entry);
            System.Diagnostics.Debug.WriteLine($"[Sync] Skipped invalid remote row - {entry}");
        }

        private static void ApplyRow(SQLiteConnection conn, SyncEntityInfo info, JsonElement row, Outcome outcome)
        {
            var syncId = SyncFieldMapper.GetString(row, "id");
            if (string.IsNullOrEmpty(syncId))
                return;

            var map = conn.GetMapping(info.ClrType);
            var local = conn.Query(map, $"SELECT * FROM \"{map.TableName}\" WHERE SyncId = ?", syncId).FirstOrDefault() as ISyncEntity;
            var entityType = info.EntityType;
            var pending = conn.Table<PendingChange>()
                .Where(p => p.EntityType == entityType && p.EntitySyncId == syncId)
                .FirstOrDefault();

            var deleted = row.TryGetProperty("deleted_at", out var deletedAt) && deletedAt.ValueKind == JsonValueKind.String;
            var changeSeq = row.TryGetProperty("change_seq", out var seq) && seq.ValueKind == JsonValueKind.Number ? seq.GetInt64() : 0L;

            if (deleted)
            {
                if (local is not null)
                {
                    RemoveLocal(conn, local, outcome);
                    outcome.Changed++;
                }
                if (pending is not null)
                    conn.Delete(pending); // the delete wins over an edit that waited offline
                return;
            }

            if (pending is { Operation: ChangeOperation.Delete })
                return; // we deleted it here; our delete is on its way

            if (local is not null && changeSeq <= local.ChangeSeq)
                return; // already have this version (e.g. our own push)

            var keep = pending?.ChangedFieldList;
            Func<string, bool> keepLocal = pending is null
                ? _ => false
                : keep is null ? _ => true : name => keep.Contains(name);

            if (local is null)
            {
                var entity = (ISyncEntity)Activator.CreateInstance(info.ClrType)!;
                if (!SyncFieldMapper.ApplyRow(conn, info, row, entity, _ => false))
                {
                    outcome.Skipped++;
                    return;
                }

                if (EntityValidator.Validate(entity) is { } problem)
                {
                    Reject(info, row, problem, outcome);
                    return;
                }

                SyncFieldMapper.ApplyMetadata(row, entity);
                if (entity is TripAttachment attachment)
                    attachment.RelativePath = LocalPathFor(attachment);

                conn.Insert(entity);
                Track(entity, outcome);
                outcome.Changed++;
                return;
            }

            if (!SyncFieldMapper.ApplyRow(conn, info, row, local, keepLocal))
            {
                outcome.Skipped++;
                return;
            }

            if (EntityValidator.Validate(local) is { } invalid)
            {
                Reject(info, row, invalid, outcome); // the stored copy stays as it was (the object isn't saved)
                return;
            }

            SyncFieldMapper.ApplyMetadata(row, local);
            conn.Update(local);
            Track(local, outcome);
            outcome.Changed++;
        }

        private static void Track(ISyncEntity entity, Outcome outcome)
        {
            if (entity is Trip trip)
                outcome.ChangedTripIds.Add(trip.Id);
        }

        /// <summary>"attachments/&lt;local trip id&gt;/&lt;SyncId&gt;.ext" - where a downloaded file will be kept.</summary>
        internal static string LocalPathFor(TripAttachment attachment)
        {
            var source = !string.IsNullOrEmpty(attachment.RemotePath) ? attachment.RemotePath : attachment.DisplayName;
            var extension = Path.GetExtension(source ?? string.Empty).ToLowerInvariant();
            if (extension.Length > 10)
                extension = string.Empty;
            return $"{AttachmentService.FolderName}/{attachment.TripId}/{attachment.SyncId}{extension}";
        }

        private static void RemoveLocal(SQLiteConnection conn, ISyncEntity local, Outcome outcome)
        {
            switch (local)
            {
                case Trip trip:
                    RemoveTrip(conn, trip, outcome);
                    return;
                case Expense expense:
                    conn.Execute("DELETE FROM ExpenseShare WHERE ExpenseId = ?", expense.Id);
                    break;
                case Participant participant:
                    conn.Execute("DELETE FROM ExpenseShare WHERE ParticipantId = ?", participant.Id);
                    conn.Execute("UPDATE Expense SET PaidByParticipantId = NULL WHERE PaidByParticipantId = ?", participant.Id);
                    break;
                case Place place:
                    conn.Execute("UPDATE Activity SET PlaceId = NULL WHERE PlaceId = ?", place.Id);
                    break;
                case TripAttachment attachment:
                    outcome.FilesToDelete.Add(attachment.RelativePath);
                    break;
            }

            conn.Delete(local);
        }

        private static void RemoveTrip(SQLiteConnection conn, Trip trip, Outcome outcome)
        {
            var tripId = trip.Id;
            conn.Execute("DELETE FROM ExpenseShare WHERE ExpenseId IN (SELECT Id FROM Expense WHERE TripId = ?)", tripId);
            foreach (var table in new[] { "Expense", "Todo", "Activity", "Place", "Participant", "Attachment" })
                conn.Execute($"DELETE FROM \"{table}\" WHERE TripId = ?", tripId);
            conn.Delete(trip);

            // Whatever was still queued for this trip can't be applied any more.
            conn.Execute("DELETE FROM PendingChange WHERE TripSyncId = ?", trip.SyncId);
            conn.Execute("DELETE FROM TripMembership WHERE TripSyncId = ?", trip.SyncId);

            outcome.RemovedTripIds.Add(tripId);
            outcome.ChangedTripIds.Remove(tripId);
        }
    }
}
