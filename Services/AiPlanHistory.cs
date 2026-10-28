using System.Text.Json;
using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>One saved plan, read back.</summary>
    public sealed record AiPlanHistoryEntry(int Id, string JobId, DateTime CreatedUtc, AiPlanResult Result, AiPlanAnswers? Answers, bool IsApplied);

    /// <summary>
    /// TP-108: the AI plans of each trip, kept on the phone (the newest <see cref="AiPlanHistory.MaxPerTrip"/>), so a
    /// plan isn't lost after "Soruları değiştir" and can be added to the trip later. Opening one uses no plan.
    /// </summary>
    public interface IAiPlanHistory
    {
        /// <summary>Saves a ready plan of the trip (local id); the same job is saved once. Nothing when the trip is gone.</summary>
        Task SaveAsync(int tripId, string jobId, AiPlanResult result, AiPlanAnswers? answers, DateTime createdUtc);

        /// <summary>The trip's plans, newest first.</summary>
        Task<IReadOnlyList<AiPlanHistoryEntry>> ForTripAsync(int tripId);

        /// <summary>Something of this plan (by its job id) was added to the trip.</summary>
        Task MarkAppliedAsync(string jobId);

        /// <summary>Removes the plans of trips that are neither in the trip list nor in the trash.</summary>
        Task<int> PurgeOrphansAsync();
    }

    public class AiPlanHistory : IAiPlanHistory
    {
        public const int MaxPerTrip = 5;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IDatabaseService _database;

        // the job service (plan ready) and the planner page (plan shown) save the same plan at about the same time
        private readonly SemaphoreSlim _saving = new(1, 1);

        public AiPlanHistory(IDatabaseService database) => _database = database;

        public async Task SaveAsync(int tripId, string jobId, AiPlanResult result, AiPlanAnswers? answers, DateTime createdUtc)
        {
            await _saving.WaitAsync();
            try
            {
                await SaveOnceAsync(tripId, jobId, result, answers, createdUtc);
            }
            finally
            {
                _saving.Release();
            }
        }

        private async Task SaveOnceAsync(int tripId, string jobId, AiPlanResult result, AiPlanAnswers? answers, DateTime createdUtc)
        {
            var conn = await _database.Connection;
            var trip = await conn.FindAsync<Trip>(tripId);
            if (trip is null || string.IsNullOrEmpty(trip.SyncId))
                return;
            if (!string.IsNullOrEmpty(jobId) && await conn.Table<AiPlanRecord>().Where(r => r.JobId == jobId).CountAsync() > 0)
                return;

            await conn.InsertAsync(new AiPlanRecord
            {
                TripSyncId = trip.SyncId,
                JobId = jobId,
                CreatedUtc = createdUtc,
                ResultJson = JsonSerializer.Serialize(result, Json),
                AnswersJson = answers is null ? null : JsonSerializer.Serialize(answers, Json),
            });

            // only the newest few of a trip
            var syncId = trip.SyncId;
            var old = (await conn.Table<AiPlanRecord>().Where(r => r.TripSyncId == syncId).ToListAsync())
                .OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id).Skip(MaxPerTrip).ToList();
            foreach (var record in old)
                await conn.DeleteAsync(record);

            // and none of trips that are gone for good
            await PurgeOrphansAsync();
        }

        public async Task<IReadOnlyList<AiPlanHistoryEntry>> ForTripAsync(int tripId)
        {
            var conn = await _database.Connection;
            var trip = await conn.FindAsync<Trip>(tripId);
            if (trip is null || string.IsNullOrEmpty(trip.SyncId))
                return Array.Empty<AiPlanHistoryEntry>();

            var syncId = trip.SyncId;
            var records = await conn.Table<AiPlanRecord>().Where(r => r.TripSyncId == syncId).ToListAsync();
            var entries = new List<AiPlanHistoryEntry>();
            foreach (var record in records.OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id))
            {
                if (Read(record) is { } entry)
                    entries.Add(entry);
            }
            return entries;
        }

        public async Task MarkAppliedAsync(string jobId)
        {
            var conn = await _database.Connection;
            foreach (var record in await conn.Table<AiPlanRecord>().Where(r => r.JobId == jobId && r.AppliedUtc == null).ToListAsync())
            {
                record.AppliedUtc = DateTime.UtcNow;
                await conn.UpdateAsync(record);
            }
        }

        public async Task<int> PurgeOrphansAsync()
        {
            var conn = await _database.Connection;
            var trips = (await conn.QueryScalarsAsync<string>("SELECT SyncId FROM Trip")).ToHashSet();
            var trashed = (await conn.QueryScalarsAsync<string>("SELECT TripSyncId FROM Trash WHERE TripSyncId IS NOT NULL")).ToHashSet();
            var removed = 0;
            foreach (var record in await conn.Table<AiPlanRecord>().ToListAsync())
            {
                if (trips.Contains(record.TripSyncId) || trashed.Contains(record.TripSyncId))
                    continue;
                removed += await conn.DeleteAsync(record);
            }
            return removed;
        }

        private static AiPlanHistoryEntry? Read(AiPlanRecord record)
        {
            try
            {
                var result = JsonSerializer.Deserialize<AiPlanResult>(record.ResultJson, Json);
                if (result?.Plan is null)
                    return null;
                var answers = string.IsNullOrEmpty(record.AnswersJson) ? null : JsonSerializer.Deserialize<AiPlanAnswers>(record.AnswersJson, Json);
                return new AiPlanHistoryEntry(record.Id, record.JobId, record.CreatedUtc, result, answers, record.AppliedUtc is not null);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Saved plan {record.Id} unreadable: {ex.Message}");
                return null;
            }
        }
    }
}
