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
    /// - trips that no longer appear in memberships (removed from the trip) are removed locally.
    /// </summary>
    public static class RemoteApplier
    {
        public sealed class Outcome
        {
            public int Changed { get; set; }

            /// <summary>Rows that couldn't be stored because their trip/parent isn't here (yet).</summary>
            public int Skipped { get; set; }

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
                    ApplyRow(conn, info, row, outcome);
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
            if (response.TryGetProperty("cursor", out var cursor) && cursor.ValueKind == JsonValueKind.Number)
            {
                outcome.Cursor = cursor.GetInt64();
                conn.InsertOrReplace(new SyncStateEntry
                {
                    Key = SyncService.CursorKey,
                    Value = outcome.Cursor.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }

            return outcome;
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
