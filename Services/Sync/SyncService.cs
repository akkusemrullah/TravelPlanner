using System.Text.Json;
using System.Text.Json.Nodes;
using SQLite;
using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    public class SyncService : ISyncService
    {
        /// <summary>Storage bucket for attachment files (supabase/migrations/…05_storage.sql).</summary>
        public const string Bucket = "trip-files";

        internal const string CursorKey = "pull_cursor";
        internal const string LastSyncKey = "last_sync_utc";
        internal const string LastFullPullKey = "last_full_pull_utc";

        /// <summary>TP-8: small calls finish well inside the server's time limit for one apply_changes call.</summary>
        internal const int BatchSize = 50;

        /// <summary>
        /// TP-8: everything is read again (p_since = 0) once in this period - a safety net in case the cursor ever
        /// moved past a row (the server prevents that since migration 18, but a missed row would otherwise stay
        /// missing until it changes again).
        /// </summary>
        internal static readonly TimeSpan FullPullInterval = TimeSpan.FromDays(1);
        internal const int MaxAttemptsBeforeBackoff = 5;
        internal static readonly TimeSpan BackoffDelay = TimeSpan.FromMinutes(30);
        internal static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(1);
        internal static readonly TimeSpan Period = TimeSpan.FromSeconds(30);

        /// <summary>RequestRefresh does nothing if a round finished less than this long ago.</summary>
        internal static readonly TimeSpan RefreshGap = TimeSpan.FromSeconds(5);

        /// <summary>Server answers that mean "this change will never be accepted" - drop it and re-read the server's copy.</summary>
        private static readonly HashSet<string> FinalRejections = new(StringComparer.Ordinal)
        {
            "forbidden", "row_deleted", "trip_deleted", "trip_mismatch", "unknown_entity"
        };

        /// <summary>
        /// TP-3: the server refused the change because a usage limit is reached (e.g. expenses per trip). It is kept
        /// and retried after <see cref="BackoffDelay"/> - by then the user may have deleted something, or it's a new day.
        /// </summary>
        internal const string QuotaReason = "quota";

        /// <summary>TP-3: attachment uploads Storage refused (SyncId → when), not retried before <see cref="BackoffDelay"/>.</summary>
        private readonly Dictionary<string, DateTime> _uploadRefusedAtUtc = new(StringComparer.Ordinal);

        private readonly IAuthService _auth;
        private readonly IAccountService _account;
        private readonly IDatabaseService _database;
        private readonly ISupabaseClient _client;
        private readonly IAttachmentService _attachments;
        private readonly ITripCoverService? _covers;
        private readonly INotificationService _notifications;
        private readonly IConnectivity _connectivity;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private CancellationTokenSource? _debounce;
        private CancellationTokenSource? _timer;

        /// <summary>TP-9: the running round's token source - cancelled when the session or the open account changes.</summary>
        private CancellationTokenSource? _round;

        /// <summary>TP-9: the account the running round works for (signed in and whose data is open).</summary>
        private string? _roundUserId;
        private bool _subscribed;
        private int _roundsRunning;
        private DateTime _lastRoundEndedUtc = DateTime.MinValue;

        public SyncService(
            IAuthService auth,
            IAccountService account,
            IDatabaseService database,
            ISupabaseClient client,
            IAttachmentService attachments,
            INotificationService notifications,
            IConnectivity connectivity,
            ITripCoverService? covers = null)
        {
            _covers = covers;
            _auth = auth;
            _account = account;
            _database = database;
            _client = client;
            _attachments = attachments;
            _notifications = notifications;
            _connectivity = connectivity;
        }

        /// <summary>Clock (replaceable in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public SyncStatus Status { get; private set; } = SyncStatus.Disabled;

        public int PendingCount { get; private set; }

        public DateTime? LastSyncedAtUtc { get; private set; }

        public event EventHandler? StatusChanged;

        public event EventHandler? RemoteChangesApplied;

        public event EventHandler<IReadOnlyList<RejectedChange>>? ChangesRejected;

        public event EventHandler<int>? QuotaExceeded;

        public event EventHandler<IReadOnlyList<string>>? SharedTripsRemoved;

        // ------------------------------------------------------------------ triggers

        public void Start()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                ChangeTracker.LocalChangeQueued += OnLocalChangeQueued;
                _connectivity.ConnectivityChanged += OnConnectivityChanged;
                _account.AccountChanged += OnAccountChanged;
                _auth.SessionChanged += OnSessionChanged;
            }

            var timer = new CancellationTokenSource();
            Interlocked.Exchange(ref _timer, timer)?.Cancel();
            RunTimerAsync(timer.Token).FireAndForget();

            RequestSync(TimeSpan.Zero);
        }

        public void Stop() => Interlocked.Exchange(ref _timer, null)?.Cancel();

        private async Task RunTimerAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(Period);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                RequestSync(TimeSpan.Zero);
        }

        private void OnLocalChangeQueued() => RequestSync();

        private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
                RequestSync(TimeSpan.FromSeconds(1));
            else
                SetStatus(_auth.IsSignedIn ? SyncStatus.Offline : SyncStatus.Disabled);
        }

        // TP-9: signed out or in as someone else - the running round must not send another request
        private void OnSessionChanged(object? sender, EventArgs e) => CancelRound();

        private void CancelRound()
        {
            try
            {
                Volatile.Read(ref _round)?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the round has just ended
            }
        }

        public async Task CancelCurrentRoundAsync()
        {
            CancelRound();
            await _gate.WaitAsync().ConfigureAwait(false);
            _gate.Release();
        }

        private void OnAccountChanged(object? sender, EventArgs e)
        {
            LastSyncedAtUtc = null;
            PendingCount = 0;
            RequestSync(TimeSpan.Zero);
        }

        public void RequestSync(TimeSpan? delay = null)
        {
            var debounce = new CancellationTokenSource();
            Interlocked.Exchange(ref _debounce, debounce)?.Cancel();
            DelayedSyncAsync(delay ?? DefaultDelay, debounce.Token).FireAndForget();
        }

        public void RequestRefresh()
        {
            if (!_auth.IsSignedIn || Volatile.Read(ref _roundsRunning) > 0)
                return;
            if (UtcNow() - _lastRoundEndedUtc < RefreshGap)
                return;

            RequestSync(TimeSpan.Zero);
        }

        private async Task DelayedSyncAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            // Off the UI thread: requests come from page loads, saves and the timer (all on the UI thread), and a
            // round's JSON and SQLite work would otherwise run between its awaits right there - during a page
            // transition that is a visible stutter. Listeners dispatch to the UI themselves (App, SettingsViewModel).
            await Task.Run(() => SyncNowAsync(CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ one round

        public async Task<SyncResult> SyncNowAsync(CancellationToken cancellationToken = default)
        {
            var empty = new SyncResult(0, 0, 0, 0, 0, PendingCount);

            if (!_auth.IsSignedIn)
            {
                SetStatus(SyncStatus.Disabled);
                return empty;
            }

            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                await RefreshPendingCountAsync();
                SetStatus(SyncStatus.Offline);
                return empty;
            }

            Interlocked.Increment(ref _roundsRunning);
            try
            {
                await _gate.WaitAsync(cancellationToken);
            }
            catch
            {
                Interlocked.Decrement(ref _roundsRunning);
                throw;
            }
            var round = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Volatile.Write(ref _round, round);
            try
            {
                // TP-9: the round belongs to the account that is signed in and whose data is open. While a switch is
                // half done (signed in as B, A's data still open) nothing is sent - AccountChanged starts the next round.
                _roundUserId = _auth.CurrentSession?.UserId;
                if (_roundUserId != _account.ActiveUserId)
                {
                    SetStatus(SyncStatus.Pending);
                    return empty;
                }

                var token = round.Token;
                SetStatus(SyncStatus.Syncing);
                var db = await _database.Connection;

                var push = await PushAsync(db, token);
                var files = await UploadFilesAsync(db, token);
                // trip covers picked on this device: the files first, then the trip's cover_path (second push below)
                var uploaded = files.Uploaded + (_covers is null ? 0 : await _covers.UploadPendingAsync(token));
                if (uploaded > 0)
                    push = push.Add(await PushAsync(db, token));

                // TP-6: refused changes are rolled back already - refused new rows are gone, and their files and
                // reminders go too. The user is told now, even if the pull that brings the server's copies fails.
                CleanUpRemoved(push.FilesToDelete, push.RemovedTripIds);
                if (push.RejectedChanges.Count > 0)
                {
                    RemoteChangesApplied?.Invoke(this, EventArgs.Empty);
                    ChangesRejected?.Invoke(this, push.RejectedChanges);
                }

                var pulled = await PullAsync(db, push.NeedsFullPull || await IsFullPullDueAsync(db), token);

                PendingCount = await db.Table<PendingChange>().CountAsync();
                LastSyncedAtUtc = UtcNow();
                await db.InsertOrReplaceAsync(new SyncStateEntry { Key = LastSyncKey, Value = LastSyncedAtUtc.Value.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) });
                SetStatus(PendingCount == 0 ? SyncStatus.UpToDate : SyncStatus.Pending);

                if (push.NewlyOverQuota + files.NewlyRefused > 0)
                    QuotaExceeded?.Invoke(this, push.NewlyOverQuota + files.NewlyRefused);

                return new SyncResult(push.Pushed, push.Rejected, push.Conflicts, uploaded, pulled, PendingCount);
            }
            catch (SyncException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] {ex.Kind}: {ex.Message}");
                SetStatus(ex.Kind switch
                {
                    // TP-3: too many sync calls this minute - nothing is wrong, the next round goes on
                    _ when ex.Status == 429 => SyncStatus.Pending,
                    SyncErrorKind.Network => SyncStatus.Offline,
                    SyncErrorKind.NotSignedIn => _auth.IsSignedIn ? SyncStatus.Offline : SyncStatus.Disabled,
                    _ => SyncStatus.Error,
                });
                return empty;
            }
            catch (OperationCanceledException)
            {
                SetStatus(SyncStatus.Pending);
                return empty;
            }
            catch (Exception ex)
            {
                // e.g. the account was switched mid-round and its connection closed - the next round starts clean
                System.Diagnostics.Debug.WriteLine($"[Sync] Round failed: {ex}");
                SetStatus(SyncStatus.Error);
                return empty;
            }
            finally
            {
                Interlocked.CompareExchange(ref _round, null, round);
                round.Dispose();
                _lastRoundEndedUtc = UtcNow();
                Interlocked.Decrement(ref _roundsRunning);
                _gate.Release();
            }
        }

        /// <summary>
        /// TP-9: before each request (and before writing what one brought): the session and the open data still
        /// belong to the round's account. A request of the outgoing account would otherwise go out with the next
        /// one's token (the client fetches it per request) and land in that account.
        /// </summary>
        private void EnsureSameAccount(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_auth.CurrentSession?.UserId != _roundUserId || _account.ActiveUserId != _roundUserId)
                throw new OperationCanceledException("The account changed during the sync round.");
        }

        private async Task RefreshPendingCountAsync()
        {
            try
            {
                var db = await _database.Connection;
                PendingCount = await db.Table<PendingChange>().CountAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Pending count: {ex.Message}");
            }
        }

        private void SetStatus(SyncStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ push (outbox → apply_changes)

        /// <param name="OverQuota">Changes the server refused over a usage limit (TP-3); they stay queued.</param>
        /// <param name="NewlyOverQuota">Of those, the ones refused for that reason for the first time (the user is told once).</param>
        internal sealed record PushOutcome(int Pushed, int Rejected, int Conflicts, int Failed, bool NeedsFullPull,
                                           int OverQuota = 0, int NewlyOverQuota = 0)
        {
            public static PushOutcome Empty { get; } = new(0, 0, 0, 0, false);

            /// <summary>TP-6: the changes refused for good (what the user is told about).</summary>
            public IReadOnlyList<RejectedChange> RejectedChanges { get; init; } = Array.Empty<RejectedChange>();

            /// <summary>TP-6: attachment files (relative paths) of refused new rows that were removed.</summary>
            public IReadOnlyList<string> FilesToDelete { get; init; } = Array.Empty<string>();

            /// <summary>TP-6: refused new trips that were removed.</summary>
            public IReadOnlyList<int> RemovedTripIds { get; init; } = Array.Empty<int>();

            public PushOutcome Add(PushOutcome other) => new(
                Pushed + other.Pushed, Rejected + other.Rejected, Conflicts + other.Conflicts, Failed + other.Failed,
                NeedsFullPull || other.NeedsFullPull, OverQuota + other.OverQuota, NewlyOverQuota + other.NewlyOverQuota)
            {
                RejectedChanges = RejectedChanges.Concat(other.RejectedChanges).ToList(),
                FilesToDelete = FilesToDelete.Concat(other.FilesToDelete).ToList(),
                RemovedTripIds = RemovedTripIds.Concat(other.RemovedTripIds).ToList(),
            };
        }

        private sealed record BatchItem(PendingChange Pending, JsonObject? Change);

        internal async Task<PushOutcome> PushAsync(SQLiteAsyncConnection db, CancellationToken cancellationToken)
        {
            var total = PushOutcome.Empty;

            for (var round = 0; round < 50; round++)
            {
                var now = UtcNow();
                List<BatchItem> batch = new();
                await db.RunInTransactionAsync(conn => batch = BuildBatch(conn, now));
                if (batch.Count == 0)
                    break;

                var toSend = batch.Where(b => b.Change is not null).ToList();
                var response = default(JsonElement);
                if (toSend.Count > 0)
                {
                    var changes = new JsonArray();
                    foreach (var item in toSend)
                        changes.Add(item.Change);
                    EnsureSameAccount(cancellationToken);
                    response = await _client.RpcAsync("apply_changes", new JsonObject { ["p_changes"] = changes }, cancellationToken);
                }

                var outcome = PushOutcome.Empty;
                await db.RunInTransactionAsync(conn => outcome = ProcessResults(conn, batch, toSend, response, now));
                total = total.Add(outcome);

                if (batch.Count < BatchSize)
                    break;
            }

            return total;
        }

        private static List<BatchItem> BuildBatch(SQLiteConnection conn, DateTime now)
        {
            var pendings = conn.Query<PendingChange>(
                "SELECT * FROM PendingChange WHERE Attempts < ? OR LastAttemptAtUtc IS NULL OR LastAttemptAtUtc < ? ORDER BY Sequence LIMIT ?",
                MaxAttemptsBeforeBackoff, now - BackoffDelay, BatchSize);

            var batch = new List<BatchItem>(pendings.Count);
            foreach (var pending in pendings)
            {
                if (!SyncSchema.TryForEntityType(pending.EntityType, out var info))
                {
                    batch.Add(new BatchItem(pending, null)); // unknown type - drop
                    continue;
                }

                var change = new JsonObject
                {
                    ["entity"] = pending.EntityType,
                    ["id"] = pending.EntitySyncId,
                    ["base_version"] = pending.BaseVersion,
                };

                if (pending.Operation == ChangeOperation.Delete)
                {
                    change["op"] = "delete";
                    change["trip_id"] = pending.TripSyncId;
                    batch.Add(new BatchItem(pending, change));
                    continue;
                }

                var map = conn.GetMapping(info.ClrType);
                if (conn.Query(map, $"SELECT * FROM \"{map.TableName}\" WHERE SyncId = ?", pending.EntitySyncId).FirstOrDefault() is not ISyncEntity entity)
                {
                    batch.Add(new BatchItem(pending, null)); // row gone (its delete is queued separately)
                    continue;
                }

                var only = pending.ChangedFieldList;
                var fields = SyncFieldMapper.ToFields(conn, info, entity, only);
                if (only is not null && fields.Count == 0)
                {
                    batch.Add(new BatchItem(pending, null)); // only device-local columns changed
                    continue;
                }

                change["op"] = "upsert";
                change["full"] = only is null;
                change["trip_id"] = info.ResolveTripSyncId(conn, entity) ?? pending.TripSyncId;
                change["fields"] = fields;
                batch.Add(new BatchItem(pending, change));
            }

            return batch;
        }

        private static PushOutcome ProcessResults(SQLiteConnection conn, List<BatchItem> batch, List<BatchItem> sent, JsonElement response, DateTime now)
        {
            foreach (var dropped in batch.Where(b => b.Change is null))
                DeletePendingIfUnchanged(conn, dropped.Pending);

            int pushed = 0, rejected = 0, conflicts = 0, failed = 0, overQuota = 0, newlyOverQuota = 0;
            var needsFullPull = false;
            var rejections = new List<RejectedChange>();
            var removed = new RemoteApplier.Outcome();
            var results = response.ValueKind == JsonValueKind.Array ? response : default;

            for (var i = 0; i < sent.Count; i++)
            {
                var pending = sent[i].Pending;
                var result = results.ValueKind == JsonValueKind.Array && i < results.GetArrayLength() ? results[i] : default;
                var status = SyncFieldMapper.GetString(result, "status");
                var reason = SyncFieldMapper.GetString(result, "reason");

                if (status is "applied" or "deleted" or "ignored")
                {
                    pushed++;
                    if (result.TryGetProperty("conflict", out var conflict) && conflict.ValueKind == JsonValueKind.True)
                        conflicts++;

                    DeletePendingIfUnchanged(conn, pending);

                    if (status == "applied" && pending.Operation == ChangeOperation.Upsert &&
                        result.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number &&
                        result.TryGetProperty("change_seq", out var seq) && seq.ValueKind == JsonValueKind.Number &&
                        SyncSchema.TryForEntityType(pending.EntityType, out var info))
                    {
                        // Our own write: remember its version and change_seq so the pull doesn't apply it back.
                        conn.Execute($"UPDATE \"{info.EntityType}\" SET Version = ?, ChangeSeq = ? WHERE SyncId = ? AND ChangeSeq <= ?",
                                     version.GetInt64(), seq.GetInt64(), pending.EntitySyncId, seq.GetInt64());
                    }
                }
                else if (status == "rejected" && reason is not null && FinalRejections.Contains(reason))
                {
                    rejected++;
                    needsFullPull = true; // the server's copy wins - read it back
                    conn.Execute("DELETE FROM PendingChange WHERE Sequence = ?", pending.Sequence);
                    rejections.Add(RollBack(conn, pending, reason, removed));
                    System.Diagnostics.Debug.WriteLine($"[Sync] {pending.EntityType} {pending.EntitySyncId} rejected: {reason}");
                }
                else if (status == "rejected" && reason == QuotaReason)
                {
                    // TP-3: keep the change, wait BackoffDelay before trying again (BuildBatch skips it until then)
                    overQuota++;
                    if (pending.LastError?.StartsWith(QuotaReason, StringComparison.Ordinal) != true)
                        newlyOverQuota++;
                    var error = SyncFieldMapper.GetString(result, "error") ?? QuotaReason;
                    if (!error.StartsWith(QuotaReason, StringComparison.Ordinal))
                        error = $"{QuotaReason}: {error}";
                    conn.Execute("UPDATE PendingChange SET Attempts = MAX(Attempts + 1, ?), LastError = ?, LastAttemptAtUtc = ? WHERE Sequence = ?",
                                 MaxAttemptsBeforeBackoff, Helpers.InputLimits.Truncate(error, 500), now, pending.Sequence);
                    System.Diagnostics.Debug.WriteLine($"[Sync] {pending.EntityType} {pending.EntitySyncId} over a usage limit: {error}");
                }
                else
                {
                    // "invalid" (e.g. its parent isn't uploaded yet), "trip_missing", or no answer: retry later
                    failed++;
                    var error = SyncFieldMapper.GetString(result, "error") ?? reason ?? status ?? "no result";
                    conn.Execute("UPDATE PendingChange SET Attempts = Attempts + 1, LastError = ?, LastAttemptAtUtc = ? WHERE Sequence = ?",
                                 Helpers.InputLimits.Truncate(error, 500), now, pending.Sequence);
                }
            }

            return new PushOutcome(pushed, rejected, conflicts, failed, needsFullPull, overQuota, newlyOverQuota)
            {
                RejectedChanges = rejections,
                FilesToDelete = removed.FilesToDelete,
                RemovedTripIds = removed.RemovedTripIds.ToList(),
            };
        }

        /// <summary>
        /// TP-6: undoes a change the server refused for good, so this device doesn't keep a version nobody else sees.
        /// A row the server has is marked <see cref="RemoteApplier.StaleChangeSeq"/> - the full pull that follows
        /// overwrites it with the server's copy even though that copy's change_seq is not newer. A new row that never
        /// reached the server (ChangeSeq 0) has no copy to come back, so it is removed with what hangs on it. A refused
        /// delete needs nothing here: the row is gone locally and the full pull brings the server's copy back.
        /// </summary>
        private static RejectedChange RollBack(SQLiteConnection conn, PendingChange pending, string reason, RemoteApplier.Outcome removed)
        {
            if (!SyncSchema.TryForEntityType(pending.EntityType, out var info))
                return new RejectedChange(pending.EntityType, null, reason, RemovedLocally: false);

            if (pending.Operation == ChangeOperation.Delete)
            {
                var deletedTitle = conn.ExecuteScalar<string>(
                    "SELECT Title FROM Trash WHERE EntityType = ? AND EntitySyncId = ? ORDER BY DeletedAtUtc DESC LIMIT 1",
                    info.EntityType, pending.EntitySyncId);
                return new RejectedChange(info.EntityType, deletedTitle, reason, RemovedLocally: false);
            }

            var map = conn.GetMapping(info.ClrType);
            if (conn.Query(map, $"SELECT * FROM \"{map.TableName}\" WHERE SyncId = ?", pending.EntitySyncId).FirstOrDefault() is not ISyncEntity local)
                return new RejectedChange(info.EntityType, null, reason, RemovedLocally: false);

            var title = info.ResolveTitle(local);
            if (local.ChangeSeq == 0)
            {
                RemoteApplier.RemoveLocal(conn, local, removed);
                return new RejectedChange(info.EntityType, title, reason, RemovedLocally: true);
            }

            conn.Execute($"UPDATE \"{map.TableName}\" SET ChangeSeq = ? WHERE SyncId = ?", RemoteApplier.StaleChangeSeq, local.SyncId);
            return new RejectedChange(info.EntityType, title, reason, RemovedLocally: false);
        }

        /// <summary>Removes an outbox entry unless it was changed again while being sent (then it goes next round).</summary>
        private static void DeletePendingIfUnchanged(SQLiteConnection conn, PendingChange pending) =>
            conn.Execute("DELETE FROM PendingChange WHERE Sequence = ? AND UpdatedAtUtc = ?", pending.Sequence, pending.UpdatedAtUtc);

        // ------------------------------------------------------------------ files (G5)

        /// <param name="Uploaded">Files uploaded this round.</param>
        /// <param name="NewlyRefused">Files Storage refused this round that weren't refused before (TP-3: the user is told once).</param>
        internal sealed record UploadOutcome(int Uploaded, int NewlyRefused);

        /// <summary>
        /// Uploads attachment files whose row is already on the server but whose file isn't, then records where
        /// the file went (RemotePath → storage_path), which the next push sends to the other devices.
        /// </summary>
        internal async Task<UploadOutcome> UploadFilesAsync(SQLiteAsyncConnection db, CancellationToken cancellationToken)
        {
            var candidates = await db.QueryAsync<TripAttachment>(
                "SELECT * FROM Attachment WHERE (RemotePath IS NULL OR RemotePath = '') AND ChangeSeq <> 0");

            int uploaded = 0, newlyRefused = 0;
            foreach (var attachment in candidates)
            {
                var fullPath = _attachments.GetFullPath(attachment);
                if (!File.Exists(fullPath))
                    continue;

                // TP-3: the bucket only takes photos and PDFs - an older attachment of another type stays on this device
                var contentType = AttachmentService.CloudContentType(attachment.RelativePath, attachment.ContentType);
                if (contentType is null)
                    continue;

                // TP-3: refused a moment ago (e.g. the trip's storage is full) - don't send the whole file again yet
                if (_uploadRefusedAtUtc.TryGetValue(attachment.SyncId, out var refusedAt) && UtcNow() - refusedAt < BackoffDelay)
                    continue;

                var tripSyncId = await db.ExecuteScalarAsync<string>("SELECT SyncId FROM Trip WHERE Id = ?", attachment.TripId);
                if (string.IsNullOrEmpty(tripSyncId))
                    continue;

                var remotePath = RemotePathFor(tripSyncId, attachment);
                EnsureSameAccount(cancellationToken);
                try
                {
                    await using var stream = File.OpenRead(fullPath);
                    await _client.UploadAsync(Bucket, remotePath, stream, contentType, cancellationToken);
                }
                catch (SyncException ex) when (ex.Kind == SyncErrorKind.Rejected)
                {
                    // the trip's or the owner's storage is full (TP-3), or a viewer's device - leave it local, retry later
                    System.Diagnostics.Debug.WriteLine($"[Sync] Upload of {attachment.SyncId} refused: {ex.Message}");
                    if (_uploadRefusedAtUtc.TryAdd(attachment.SyncId, UtcNow()))
                        newlyRefused++;
                    else
                        _uploadRefusedAtUtc[attachment.SyncId] = UtcNow();
                    continue;
                }

                // TP-9: a long upload may end after a switch - its path is not recorded under the next account's name
                EnsureSameAccount(cancellationToken);
                _uploadRefusedAtUtc.Remove(attachment.SyncId);
                attachment.RemotePath = remotePath;
                try
                {
                    await db.UpdateTrackedAsync(attachment);
                }
                catch (TripAccessException)
                {
                    continue; // became a viewer meanwhile - the owner's devices will record the path
                }
                uploaded++;
            }

            return new UploadOutcome(uploaded, newlyRefused);
        }

        /// <summary>
        /// "trips/&lt;trip SyncId&gt;/&lt;SyncId&gt;.ext" - TP-5: the only shape the server accepts as storage_path (lower-case ids, a
        /// photo/PDF extension; for a file without one the extension its content type stands for).
        /// </summary>
        internal static string RemotePathFor(string tripSyncId, TripAttachment attachment)
        {
            var extension = AttachmentService.CloudExtension(attachment.RelativePath, attachment.ContentType) ?? string.Empty;
            return $"trips/{tripSyncId.ToLowerInvariant()}/{attachment.SyncId.ToLowerInvariant()}{extension}";
        }

        // ------------------------------------------------------------------ pull (pull_changes → local)

        internal async Task<int> PullAsync(SQLiteAsyncConnection db, bool fromStart, CancellationToken cancellationToken)
        {
            var since = fromStart ? 0L : await ReadCursorAsync(db);
            EnsureSameAccount(cancellationToken);
            var response = await _client.RpcAsync("pull_changes", new JsonObject { ["p_since"] = since }, cancellationToken);

            EnsureSameAccount(cancellationToken);
            RemoteApplier.Outcome outcome = new();
            await db.RunInTransactionAsync(conn => outcome = RemoteApplier.Apply(conn, response));
            if (fromStart)
                await db.InsertOrReplaceAsync(new SyncStateEntry { Key = LastFullPullKey, Value = UtcNow().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) });

            // Outside SQLite: files of rows/trips that disappeared, and reminders of trips that changed.
            CleanUpRemoved(outcome.FilesToDelete, outcome.RemovedTripIds);

            foreach (var tripId in outcome.ChangedTripIds.Except(outcome.RemovedTripIds))
            {
                var trip = await db.FindAsync<Trip>(tripId);
                if (trip is null)
                    continue;
                TryNotify(() => _notifications.CancelTripNotifications(tripId));
                try
                {
                    await _notifications.ScheduleTripNotificationsAsync(trip);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Sync] Reminder for trip {tripId}: {ex.Message}");
                }
            }

            if (outcome.Changed > 0)
                RemoteChangesApplied?.Invoke(this, EventArgs.Empty);

            if (outcome.LostSharedTrips.Count > 0)
                SharedTripsRemoved?.Invoke(this, outcome.LostSharedTrips.ToList());

            return outcome.Changed;
        }

        /// <summary>Deletes the files of removed attachment rows and trips, and the removed trips' reminders.</summary>
        private void CleanUpRemoved(IEnumerable<string> filesToDelete, IEnumerable<int> removedTripIds)
        {
            foreach (var relativePath in filesToDelete)
                TryDelete(Path.Combine(_attachments.RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            foreach (var tripId in removedTripIds)
            {
                _attachments.DeleteFilesForTrip(tripId);
                TryNotify(() => _notifications.CancelTripNotifications(tripId));
            }
        }

        /// <summary>TP-8: no full pull yet on this device, or the last one is older than <see cref="FullPullInterval"/>.</summary>
        private async Task<bool> IsFullPullDueAsync(SQLiteAsyncConnection db)
        {
            var entry = await db.FindAsync<SyncStateEntry>(LastFullPullKey);
            if (!long.TryParse(entry?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var ticks) ||
                ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                return true;
            var last = new DateTime(ticks, DateTimeKind.Utc);
            var now = UtcNow();
            return now - last >= FullPullInterval || last > now; // a clock set back doesn't postpone it for good
        }

        private static async Task<long> ReadCursorAsync(SQLiteAsyncConnection db)
        {
            var entry = await db.FindAsync<SyncStateEntry>(CursorKey);
            return long.TryParse(entry?.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cursor)
                ? cursor
                : 0L;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Could not delete {path}: {ex.Message}");
            }
        }

        private static void TryNotify(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Notification: {ex.Message}");
            }
        }
    }
}
