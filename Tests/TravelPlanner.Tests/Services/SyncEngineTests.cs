using System.Text.Json;
using System.Text.Json.Nodes;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Tests
{
    /// <summary>A Supabase stand-in: records calls, answers RPCs with a handler, keeps "uploaded" files in memory.</summary>
    public sealed class FakeSupabaseClient : ISupabaseClient
    {
        public List<(string Function, JsonObject Arguments)> Calls { get; } = new();

        public Dictionary<string, byte[]> Storage { get; } = new();

        public Func<string, JsonObject, string> Handler { get; set; } = (_, _) => "[]";

        public Task<JsonElement> RpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default)
        {
            // keep a copy: the caller's nodes may be reused
            Calls.Add((function, (JsonObject)JsonNode.Parse(arguments.ToJsonString())!));
            using var document = JsonDocument.Parse(Handler(function, arguments));
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task<JsonElement> PublicRpcAsync(string function, JsonObject arguments, CancellationToken cancellationToken = default) =>
            RpcAsync(function, arguments, cancellationToken);

        /// <summary>Edge Functions go through the same Handler, as "fn:&lt;name&gt;".</summary>
        public Task<JsonElement> InvokeFunctionAsync(string name, JsonObject body, CancellationToken cancellationToken = default) =>
            RpcAsync("fn:" + name, body, cancellationToken);

        public async Task UploadAsync(string bucket, string path, Stream content, string? contentType, CancellationToken cancellationToken = default)
        {
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, cancellationToken);
            Storage[$"{bucket}/{path}"] = memory.ToArray();
        }

        public async Task DownloadAsync(string bucket, string path, Stream target, CancellationToken cancellationToken = default)
        {
            if (!Storage.TryGetValue($"{bucket}/{path}", out var bytes))
                throw new SyncException(SyncErrorKind.Rejected, "not found", status: 404);
            await target.WriteAsync(bytes, cancellationToken);
        }

        public Task<IReadOnlyList<string>> ListAsync(string bucket, string prefix, CancellationToken cancellationToken = default)
        {
            var folder = $"{bucket}/{prefix.Trim('/')}/";
            IReadOnlyList<string> paths = Storage.Keys
                .Where(k => k.StartsWith(folder, StringComparison.Ordinal) && !k[folder.Length..].Contains('/'))
                .Select(k => k[(bucket.Length + 1)..])
                .ToList();
            return Task.FromResult(paths);
        }

        public Task DeleteAsync(string bucket, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            Deleted.AddRange(paths);
            foreach (var path in paths)
                Storage.Remove($"{bucket}/{path}");
            return Task.CompletedTask;
        }

        public List<string> Deleted { get; } = new();

        public JsonArray SentChanges(int call = 0) =>
            Calls.Where(c => c.Function == "apply_changes").ElementAt(call).Arguments["p_changes"]!.AsArray();

        /// <summary>Answers every change of an apply_changes call with the given status.</summary>
        public static string AnswerAll(JsonObject arguments, string status, string? reason = null, long version = 1, long seq = 100)
        {
            var results = new JsonArray();
            var i = 0;
            foreach (var change in arguments["p_changes"]!.AsArray())
            {
                results.Add(new JsonObject
                {
                    ["id"] = change!["id"]!.GetValue<string>(),
                    ["status"] = status,
                    ["reason"] = reason,
                    ["version"] = version,
                    ["change_seq"] = seq + i++,
                });
            }
            return results.ToJsonString();
        }

        public static string EmptyPull(long cursor = 0, params string[] memberTrips)
        {
            var memberships = new JsonArray();
            foreach (var trip in memberTrips)
                memberships.Add(new JsonObject { ["trip_id"] = trip, ["role"] = "owner", ["change_seq"] = 1 });
            return new JsonObject { ["memberships"] = memberships, ["cursor"] = cursor }.ToJsonString();
        }
    }

    public class SyncEngineTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-sync-engine-" + Guid.NewGuid().ToString("N"));
        private readonly FakeSupabaseClient _client = new();
        private readonly Mock<IAuthService> _auth = new();
        private readonly Mock<INotificationService> _notifications = new();
        private AttachmentService _attachments = null!;
        private TripService _trips = null!;
        private ExpenseService _expenses = null!;
        private SplitService _split = null!;
        private SyncService _sync = null!;

        public Task InitializeAsync()
        {
            _auth.SetupGet(a => a.IsSignedIn).Returns(true);
            _attachments = new AttachmentService(_db.Service, _root, _client);
            _expenses = new ExpenseService(_db.Service);
            _trips = new TripService(_db.Service, _expenses, _notifications.Object, _attachments);
            _split = new SplitService(_db.Service);
            _sync = new SyncService(_auth.Object, new Mock<IAccountService>().Object, _db.Service, _client, _attachments,
                                    _notifications.Object, Fakes.Connectivity());
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _db.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private async Task<Trip> NewTripAsync()
        {
            var trip = new Trip { Name = "Roma", City = "Roma", Country = "İtalya", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22), CurrencyCode = "EUR" };
            await _trips.SaveAsync(trip);
            return trip;
        }

        private async Task<int> OutboxCountAsync() => await (await _db.Connection).Table<PendingChange>().CountAsync();

        [Fact]
        public async Task Push_SendsNewRowsWhole_ThenClearsTheOutbox_AndRemembersTheVersion()
        {
            var trip = await NewTripAsync();
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : FakeSupabaseClient.EmptyPull(0, trip.SyncId);

            var result = await _sync.SyncNowAsync();

            var change = _client.SentChanges()[0]!.AsObject();
            Assert.Equal("Trip", change["entity"]!.GetValue<string>());
            Assert.Equal("upsert", change["op"]!.GetValue<string>());
            Assert.True(change["full"]!.GetValue<bool>());
            Assert.Equal(trip.SyncId, change["id"]!.GetValue<string>());
            var fields = change["fields"]!.AsObject();
            Assert.Equal("Roma", fields["name"]!.GetValue<string>());
            Assert.Equal("2026-08-20", fields["start_date"]!.GetValue<string>());
            Assert.Equal("EUR", fields["currency_code"]!.GetValue<string>());
            Assert.False(fields.ContainsKey("id"));
            Assert.False(fields.ContainsKey("sync_id"));
            Assert.False(fields.ContainsKey("version"));

            Assert.Equal(1, result.Pushed);
            Assert.Equal(0, await OutboxCountAsync());
            var stored = await _trips.GetByIdAsync(trip.Id);
            Assert.Equal(1, stored!.Version);
            Assert.Equal(100, stored.ChangeSeq);
            Assert.Equal(SyncStatus.UpToDate, _sync.Status);
        }

        [Fact]
        public async Task Push_SendsOnlyChangedFields_AndRelationsAsSyncIds()
        {
            var trip = await NewTripAsync();
            var ali = await _split.SaveParticipantAsync(trip.Id, "Ali");
            var taxi = new Expense { TripId = trip.Id, Amount = 10m, Date = new DateTime(2026, 8, 21) };
            await _expenses.SaveAsync(taxi);
            await (await _db.Connection).ExecuteAsync("DELETE FROM PendingChange"); // pretend all of it is uploaded

            await _split.SaveSplitAsync(taxi, ali.Id, Array.Empty<int>());
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : FakeSupabaseClient.EmptyPull(0, trip.SyncId);

            await _sync.SyncNowAsync();

            var change = _client.SentChanges()[0]!.AsObject();
            Assert.Equal("Expense", change["entity"]!.GetValue<string>());
            Assert.False(change["full"]!.GetValue<bool>());
            Assert.Equal(trip.SyncId, change["trip_id"]!.GetValue<string>());
            var fields = change["fields"]!.AsObject();
            Assert.Single(fields);
            Assert.Equal(ali.SyncId, fields["paid_by_participant_id"]!.GetValue<string>());
        }

        [Fact]
        public async Task FinalRejection_DropsTheChange_AndRereadsEverything()
        {
            var trip = await NewTripAsync();
            _client.Handler = (fn, args) => fn == "apply_changes"
                ? FakeSupabaseClient.AnswerAll(args, "rejected", "forbidden")
                : FakeSupabaseClient.EmptyPull(50, trip.SyncId);

            var result = await _sync.SyncNowAsync();

            Assert.Equal(1, result.Rejected);
            Assert.Equal(0, await OutboxCountAsync());
            var pull = _client.Calls.Single(c => c.Function == "pull_changes");
            Assert.Equal(0, pull.Arguments["p_since"]!.GetValue<long>());
        }

        [Fact]
        public async Task TemporaryFailure_StaysQueued_WithAnAttempt()
        {
            await NewTripAsync();
            _client.Handler = (fn, args) => fn == "apply_changes"
                ? FakeSupabaseClient.AnswerAll(args, "rejected", "invalid")
                : FakeSupabaseClient.EmptyPull();

            await _sync.SyncNowAsync();

            var pending = Assert.Single(await (await _db.Connection).Table<PendingChange>().ToListAsync());
            Assert.Equal(1, pending.Attempts);
            Assert.Equal(SyncStatus.Pending, _sync.Status);
        }

        [Fact]
        public async Task Pull_InsertsUpdatesAndDeletes_FromTheServer()
        {
            const string tripId = "11111111-1111-1111-1111-111111111111";
            const string partId = "22222222-2222-2222-2222-222222222222";
            const string expId = "33333333-3333-3333-3333-333333333333";
            const string actId = "66666666-6666-6666-6666-666666666666";

            var first = $$"""
            {
              "cursor": 40,
              "memberships": [ { "trip_id": "{{tripId}}", "role": "editor", "change_seq": 5 } ],
              "trips": [ { "id": "{{tripId}}", "name": "Paris", "country": "Fransa", "city": "Paris", "start_date": "2026-09-01", "end_date": "2026-09-03",
                           "planned_budget_minor": 100000, "description": null, "currency_code": "EUR", "version": 2, "change_seq": 10,
                           "created_by": "{{AuthFakes.UserId}}", "updated_at": "2026-10-10T08:00:00+00:00", "deleted_at": null } ],
              "participants": [ { "id": "{{partId}}", "trip_id": "{{tripId}}", "name": "Ayşe", "user_id": null, "created_date": "2026-08-01T10:00:00", "version": 1, "change_seq": 11, "deleted_at": null } ],
              "expenses": [ { "id": "{{expId}}", "trip_id": "{{tripId}}", "category": 2, "amount_minor": 1250, "original_amount_minor": 1250,
                              "original_currency_code": "EUR", "exchange_rate": 1, "date": "2026-09-02", "paid_by_participant_id": "{{partId}}",
                              "description": "Müze", "version": 1, "change_seq": 12, "deleted_at": null } ],
              "activities": [ { "id": "{{actId}}", "trip_id": "{{tripId}}", "date": "2026-09-02", "start_time": "09:30:00", "end_time": null,
                                "title": "Louvre", "note": null, "place_id": null, "sort_order": 0, "version": 1, "change_seq": 13, "deleted_at": null } ]
            }
            """;
            _client.Handler = (_, _) => first;

            var result = await _sync.SyncNowAsync();

            Assert.Equal(4, result.Pulled);
            var trip = Assert.Single(await _trips.GetAllAsync());
            Assert.Equal("Paris", trip.Name);
            Assert.Equal(new DateTime(2026, 9, 1), trip.StartDate);
            Assert.Equal(2, trip.Version);
            var expense = Assert.Single(await _expenses.GetByTripIdAsync(trip.Id));
            Assert.Equal(12.50m, expense.Amount);
            var participant = Assert.Single(await _split.GetParticipantsAsync(trip.Id));
            Assert.Equal(participant.Id, expense.PaidByParticipantId);
            var activity = Assert.Single(await new ActivityService(_db.Service).GetByTripIdAsync(trip.Id));
            Assert.Equal(new TimeSpan(9, 30, 0), activity.StartTime);
            Assert.Equal(0, await OutboxCountAsync()); // remote rows are not queued back
            _notifications.Verify(n => n.ScheduleTripNotificationsAsync(It.Is<Trip>(t => t.Id == trip.Id)), Times.Once);

            var membership = Assert.Single(await (await _db.Connection).Table<TripMembership>().ToListAsync());
            Assert.Equal("editor", membership.Role);

            // A local edit waiting to go up keeps its value against an older remote one.
            expense.Description = "Müze bileti";
            await _expenses.SaveAsync(expense);

            var second = $$"""
            {
              "cursor": 60,
              "memberships": [ { "trip_id": "{{tripId}}", "role": "editor", "change_seq": 5 } ],
              "expenses": [ { "id": "{{expId}}", "trip_id": "{{tripId}}", "category": 2, "amount_minor": 2000, "original_amount_minor": 2000,
                              "original_currency_code": "EUR", "exchange_rate": 1, "date": "2026-09-02", "paid_by_participant_id": "{{partId}}",
                              "description": "Başkasının açıklaması", "version": 2, "change_seq": 50, "deleted_at": null } ],
              "activities": [ { "id": "{{actId}}", "trip_id": "{{tripId}}", "date": "2026-09-02", "title": "Louvre", "sort_order": 0,
                                "version": 2, "change_seq": 51, "deleted_at": "2026-10-10T09:00:00+00:00" } ]
            }
            """;
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "rejected", "invalid") : second;

            await _sync.SyncNowAsync();

            var merged = await _expenses.GetByIdAsync(expense.Id);
            Assert.Equal(20m, merged!.Amount);                     // their amount
            Assert.Equal("Müze bileti", merged.Description);       // our pending description
            Assert.Empty(await new ActivityService(_db.Service).GetByTripIdAsync(trip.Id)); // deleted remotely

            // Removed from the trip: it disappears locally.
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "rejected", "invalid") : FakeSupabaseClient.EmptyPull(70);
            await _sync.SyncNowAsync();
            Assert.Empty(await _trips.GetAllAsync());
        }

        /// <summary>A server expense row (SEC-01 tests); <paramref name="overrides"/> replace single columns.</summary>
        private static JsonObject RemoteExpense(string id, string tripId, long changeSeq, params (string Column, JsonNode? Value)[] overrides)
        {
            var row = new JsonObject
            {
                ["id"] = id, ["trip_id"] = tripId, ["category"] = 2, ["amount_minor"] = 1250, ["original_amount_minor"] = 1250,
                ["exchange_rate"] = 1, ["date"] = "2026-09-02", ["description"] = "Müze",
                ["version"] = 1, ["change_seq"] = changeSeq, ["deleted_at"] = null,
            };
            foreach (var (column, value) in overrides)
                row[column] = value;
            return row;
        }

        [Fact]
        public async Task Pull_SkipsOnlyBrokenRows_AppliesTheRest_AndMovesTheCursor()
        {
            // SEC-01: an editor wrote these straight through the REST API (e.g. before the server had its CHECKs).
            const string tripId = "11111111-1111-1111-1111-111111111111";
            const string goodExpense = "a5555555-5555-5555-5555-555555555555";
            var expenses = new JsonArray
            {
                RemoteExpense("a1111111-1111-1111-1111-111111111111", tripId, 11, ("amount_minor", long.MaxValue)),       // breaks a limit
                RemoteExpense("a2222222-2222-2222-2222-222222222222", tripId, 12, ("exchange_rate", 1e300)),              // breaks a limit
                RemoteExpense("a3333333-3333-3333-3333-333333333333", tripId, 13, ("date", "20000-01-01")),               // ParseExact throws
                RemoteExpense("a4444444-4444-4444-4444-444444444444", tripId, 14, ("category", 99_999_999_999L)),          // GetInt32 overflows
                RemoteExpense("a6666666-6666-6666-6666-666666666666", tripId, 15, ("category", 7)),                       // no such category
                RemoteExpense(goodExpense, tripId, 16),
            };
            var first = $$"""
            {
              "cursor": 40,
              "memberships": [ { "trip_id": "{{tripId}}", "role": "editor", "change_seq": 5 } ],
              "trips": [ { "id": "{{tripId}}", "name": "Paris", "country": "Fransa", "city": "Paris", "start_date": "2026-09-01", "end_date": "2026-09-03",
                           "planned_budget_minor": 100000, "currency_code": "EUR", "version": 1, "change_seq": 10, "deleted_at": null } ],
              "expenses": {{expenses.ToJsonString()}},
              "activities": [
                { "id": "b1111111-1111-1111-1111-111111111111", "trip_id": "{{tripId}}", "date": "2026-09-02", "start_time": "nope", "title": "Bozuk saat",
                  "sort_order": 0, "version": 1, "change_seq": 17, "deleted_at": null },
                { "id": "b2222222-2222-2222-2222-222222222222", "trip_id": "{{tripId}}", "date": "20000-01-01", "title": "Bozuk tarih",
                  "sort_order": 0, "version": 1, "change_seq": 18, "deleted_at": null },
                { "id": "b3333333-3333-3333-3333-333333333333", "trip_id": "{{tripId}}", "date": "2026-09-02", "start_time": "09:30:00", "title": "Louvre",
                  "sort_order": 0, "version": 1, "change_seq": 19, "deleted_at": null }
              ],
              "todos": [ { "id": "c1111111-1111-1111-1111-111111111111", "trip_id": "{{tripId}}", "title": "Pasaport", "is_completed": false,
                           "created_date": "2026-08-01T10:00:00", "version": 1, "change_seq": 20, "deleted_at": null } ]
            }
            """;
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : first;

            var result = await _sync.SyncNowAsync();

            // the good rows are in ...
            Assert.Equal(4, result.Pulled); // trip, one expense, one activity, one to-do
            var trip = Assert.Single(await _trips.GetAllAsync());
            Assert.Equal("Paris", trip.Name);
            var expense = Assert.Single(await _expenses.GetByTripIdAsync(trip.Id));
            Assert.Equal(goodExpense, expense.SyncId);
            Assert.Equal(12.50m, expense.Amount);
            Assert.Equal("Louvre", Assert.Single(await new ActivityService(_db.Service).GetByTripIdAsync(trip.Id)).Title);
            var conn = await _db.Connection;
            Assert.Equal(1, await conn.Table<TodoItem>().CountAsync());
            // ... the broken ones left nothing behind, and the cursor moved past all of them
            Assert.Equal(1, await conn.Table<Expense>().CountAsync());
            Assert.Equal(1, await conn.Table<TripActivity>().CountAsync());
            Assert.Equal("40", (await conn.FindAsync<SyncStateEntry>(SyncService.CursorKey))!.Value);
            Assert.Equal(SyncStatus.UpToDate, _sync.Status);

            // A later broken edit of a row we already have: our copy stays as it was, the rest still applies.
            var second = new JsonObject
            {
                ["cursor"] = 60,
                ["memberships"] = new JsonArray(new JsonObject { ["trip_id"] = tripId, ["role"] = "editor", ["change_seq"] = 5 }),
                ["expenses"] = new JsonArray(RemoteExpense(goodExpense, tripId, 50, ("amount_minor", long.MaxValue), ("version", 2))),
                ["activities"] = new JsonArray(new JsonObject
                {
                    ["id"] = "b3333333-3333-3333-3333-333333333333", ["trip_id"] = tripId, ["date"] = "2026-09-02", ["start_time"] = "10:00:00",
                    ["title"] = "Louvre (10:00)", ["sort_order"] = 0, ["version"] = 2, ["change_seq"] = 51, ["deleted_at"] = null,
                }),
            }.ToJsonString();
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : second;

            await _sync.SyncNowAsync();

            var pull = _client.Calls.Last(c => c.Function == "pull_changes");
            Assert.Equal(40, pull.Arguments["p_since"]!.GetValue<long>());       // the broken rows didn't hold the cursor back
            var kept = await _expenses.GetByIdAsync(expense.Id);
            Assert.Equal(12.50m, kept!.Amount);
            Assert.Equal(1, kept.Version);
            Assert.Equal("Louvre (10:00)", Assert.Single(await new ActivityService(_db.Service).GetByTripIdAsync(trip.Id)).Title);
            Assert.Equal("60", (await conn.FindAsync<SyncStateEntry>(SyncService.CursorKey))!.Value);
        }

        [Fact]
        public async Task RemoteApplier_ReportsEachBrokenRow_AndKeepsGoing()
        {
            using var document = JsonDocument.Parse("""
                { "cursor": 7, "packing_templates": [
                    { "id": "d1111111-1111-1111-1111-111111111111", "name": "Kamp", "items": "Çadır", "created_date": "not a date", "version": 1, "change_seq": 3 },
                    42,
                    { "id": "d2222222-2222-2222-2222-222222222222", "name": "Plaj", "items": "Havlu", "created_date": "2026-01-01T00:00:00", "version": 1, "change_seq": 4 } ] }
                """);
            var root = document.RootElement.Clone();
            var db = await _db.Connection;

            RemoteApplier.Outcome outcome = null!;
            await db.RunInTransactionAsync(conn => outcome = RemoteApplier.Apply(conn, root));

            Assert.Equal(1, outcome.Changed);
            var invalid = Assert.Single(outcome.Invalid);
            Assert.StartsWith("PackingTemplate d1111111-1111-1111-1111-111111111111: FormatException", invalid);
            Assert.Equal(7, outcome.Cursor);
            Assert.Equal("Plaj", Assert.Single(await db.Table<PackingTemplate>().ToListAsync()).Name);
        }

        [Fact]
        public async Task Pull_SkipsTheEchoOfOurOwnPush()
        {
            var trip = await NewTripAsync();
            _client.Handler = (fn, args) => fn == "apply_changes"
                ? FakeSupabaseClient.AnswerAll(args, "applied", seq: 100)
                : $$"""
                  { "cursor": 100, "memberships": [ { "trip_id": "{{trip.SyncId}}", "role": "owner" } ],
                    "trips": [ { "id": "{{trip.SyncId}}", "name": "ESKİ", "country": "", "city": "", "start_date": "2026-08-20", "end_date": "2026-08-22",
                                 "planned_budget_minor": 0, "version": 1, "change_seq": 100, "deleted_at": null } ] }
                  """;

            await _sync.SyncNowAsync();

            Assert.Equal("Roma", (await _trips.GetByIdAsync(trip.Id))!.Name);
        }

        [Fact]
        public async Task Attachments_AreUploaded_AndDownloadedOnAnotherDevice()
        {
            var trip = await NewTripAsync();
            var file = await _attachments.AddAsync(trip.Id, new MemoryStream(new byte[] { 4, 5, 6 }), "bilet.pdf", "application/pdf");
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : FakeSupabaseClient.EmptyPull(0, trip.SyncId);

            var result = await _sync.SyncNowAsync();

            Assert.Equal(1, result.FilesUploaded);
            var remotePath = $"trips/{trip.SyncId}/{file.SyncId}.pdf";
            Assert.Equal(new byte[] { 4, 5, 6 }, _client.Storage[$"trip-files/{remotePath}"]);
            var stored = Assert.Single(await _attachments.GetByTripIdAsync(trip.Id));
            Assert.Equal(remotePath, stored.RemotePath);
            // the second push told the server where the file is
            var storageChange = _client.SentChanges(1)[0]!.AsObject();
            Assert.Equal(remotePath, storageChange["fields"]!["storage_path"]!.GetValue<string>());

            // "another device": the row is here, the file isn't
            File.Delete(stored.FullPath);
            Assert.True(await _attachments.EnsureLocalFileAsync(stored));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(stored.FullPath));
        }

        [Fact]
        public async Task Guest_DoesNotSync()
        {
            _auth.SetupGet(a => a.IsSignedIn).Returns(false);
            await NewTripAsync();

            await _sync.SyncNowAsync();

            Assert.Empty(_client.Calls);
            Assert.Equal(SyncStatus.Disabled, _sync.Status);
        }

        [Fact]
        public async Task Offline_KeepsTheOutbox()
        {
            var offline = new SyncService(_auth.Object, new Mock<IAccountService>().Object, _db.Service, _client, _attachments,
                                          _notifications.Object, Fakes.Connectivity(online: false));
            await NewTripAsync();

            await offline.SyncNowAsync();

            Assert.Empty(_client.Calls);
            Assert.Equal(SyncStatus.Offline, offline.Status);
            Assert.Equal(1, offline.PendingCount);
        }

        [Fact]
        public async Task RequestRefresh_SkipsRightAfterARound_ThenSyncsAgain()
        {
            var now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
            _sync.UtcNow = () => now;
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : FakeSupabaseClient.EmptyPull();
            await _sync.SyncNowAsync();
            var calls = _client.Calls.Count;

            _sync.RequestRefresh(); // a page opened right after the round: nothing new to ask for yet
            await Task.Delay(200);
            Assert.Equal(calls, _client.Calls.Count);

            now = now.Add(SyncService.RefreshGap).AddSeconds(1);
            _sync.RequestRefresh();
            for (var i = 0; i < 50 && _client.Calls.Count == calls; i++)
                await Task.Delay(50);

            Assert.Contains(_client.Calls.Skip(calls), c => c.Function == "pull_changes");
        }

        [Fact]
        public void FieldNames_AreSnakeCase()
        {
            Assert.Equal("paid_by_participant_id", SyncFieldMapper.SnakeCase("PaidByParticipantId"));
            Assert.Equal("amount_minor", SyncFieldMapper.SnakeCase("AmountMinor"));
            Assert.Contains(SyncFieldMapper.FieldsFor(SyncSchema.For<TripAttachment>()), f => f.Column == "storage_path");
            Assert.DoesNotContain(SyncFieldMapper.FieldsFor(SyncSchema.For<TripAttachment>()), f => f.Column == "relative_path");
            Assert.DoesNotContain(SyncFieldMapper.FieldsFor(SyncSchema.For<Expense>()), f => f.Column == "trip_id");
        }
    }
}
