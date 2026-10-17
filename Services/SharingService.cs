using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>A person on a shared trip (G6).</summary>
    public sealed record TripMember(string UserId, TripRole Role, string? DisplayName, string? Email, DateTime JoinedAtUtc, bool IsMe)
    {
        /// <summary>Name to show: the profile name, else the e-mail.</summary>
        public string Name => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : Email ?? "?";
    }

    /// <summary>
    /// A fresh invite code (8 letters/digits), when it stops working and how many people may use it (TP-4:
    /// 1 unless a group code was asked for). Whoever joins is a viewer.
    /// </summary>
    public sealed record TripInvite(string Code, TripRole Role, DateTime ExpiresAtUtc, int MaxUses = 1)
    {
        public bool IsGroupInvite => MaxUses > 1;
    }

    /// <summary>TP-4: someone joined one of the user's own trips (for the "Ayşe joined Roma" notice).</summary>
    public sealed record MemberJoin(string TripSyncId, string? TripName, string UserId, string? DisplayName, DateTime JoinedAtUtc);

    /// <summary>The trip an invite code led to.</summary>
    public sealed record JoinedTrip(string TripSyncId, TripRole Role, string? TripName, int? LocalTripId);

    public enum SharingErrorKind
    {
        /// <summary>No connection - sharing needs the server.</summary>
        Offline,
        NotSignedIn,
        /// <summary>The trip hasn't reached the cloud yet (can't sync right now).</summary>
        NotUploaded,
        /// <summary>The code is wrong, expired, used up or revoked.</summary>
        InvalidInvite,
        /// <summary>TP-4: too many wrong codes - the account has to wait (an hour) before trying again.</summary>
        TooManyAttempts,
        /// <summary>Not allowed (not the owner / not a member any more).</summary>
        Forbidden,
        Unknown
    }

    public sealed class SharingException : Exception
    {
        public SharingException(SharingErrorKind kind, string? detail = null, Exception? inner = null)
            : base(detail ?? kind.ToString(), inner)
        {
            Kind = kind;
        }

        public SharingErrorKind Kind { get; }
    }

    /// <summary>
    /// Sharing a trip (G6): invites, members, roles, leaving. Every call needs a connection - these are the
    /// "online only" actions; the server (supabase/migrations/…04_sharing_rpc.sql) checks who may do what.
    /// </summary>
    public interface ISharingService
    {
        /// <summary>
        /// Makes an invite code for the trip (uploads the trip first if it isn't in the cloud yet). Owner only.
        /// TP-4: the code is single-use and valid for <see cref="SharingService.InviteValidHours"/> hours; with
        /// <paramref name="forGroup"/> up to <see cref="SharingService.GroupInviteMaxUses"/> people can use it.
        /// Everyone joins as a viewer - the owner makes them an editor afterwards (<see cref="SetRoleAsync"/>).
        /// </summary>
        Task<TripInvite> CreateInviteAsync(int tripId, bool forGroup = false);

        /// <summary>Joins the trip behind <paramref name="code"/> and syncs it onto this device.</summary>
        Task<JoinedTrip> AcceptInviteAsync(string code);

        /// <summary>The trip's members. TP-4: e-mails only come back for the owner (and the user's own).</summary>
        Task<IReadOnlyList<TripMember>> GetMembersAsync(int tripId);

        /// <summary>TP-4: people who joined the user's own trips after <paramref name="sinceUtc"/> (null: the last few days).</summary>
        Task<IReadOnlyList<MemberJoin>> GetRecentJoinsAsync(DateTime? sinceUtc, CancellationToken cancellationToken = default);

        /// <summary>Owner: makes a member an editor or a viewer.</summary>
        Task SetRoleAsync(int tripId, string userId, TripRole role);

        /// <summary>Owner: takes a member off the trip.</summary>
        Task RemoveMemberAsync(int tripId, string userId);

        /// <summary>Editor/viewer: leaves the trip; it disappears from this device after the sync.</summary>
        Task LeaveTripAsync(int tripId);
    }

    public class SharingService : ISharingService
    {
        /// <summary>How long an invite code works (TP-4; the server allows no more).</summary>
        public const int InviteValidHours = 48;

        /// <summary>How many people one group code can bring in (TP-4; the server allows no more).</summary>
        public const int GroupInviteMaxUses = 10;

        public const int CodeLength = 8;

        private readonly ISupabaseClient _client;
        private readonly IDatabaseService _database;
        private readonly ISyncService _sync;
        private readonly ITripAccessService _access;
        private readonly IConnectivity _connectivity;

        public SharingService(ISupabaseClient client, IDatabaseService database, ISyncService sync, ITripAccessService access, IConnectivity connectivity)
        {
            _client = client;
            _database = database;
            _sync = sync;
            _access = access;
            _connectivity = connectivity;
        }

        /// <summary>"abcd 2345" → "ABCD2345": spaces/dashes dropped, upper case.</summary>
        public static string NormalizeCode(string? code) =>
            new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

        public async Task<TripInvite> CreateInviteAsync(int tripId, bool forGroup = false)
        {
            var maxUses = forGroup ? GroupInviteMaxUses : 1;
            var tripSyncId = await RequireUploadedTripAsync(tripId);
            var rows = await CallAsync("create_invite", new JsonObject
            {
                ["p_trip_id"] = tripSyncId,
                ["p_role"] = TripRoles.Viewer,
                ["p_valid_hours"] = InviteValidHours,
                ["p_max_uses"] = maxUses,
            });

            var row = FirstRow(rows) ?? throw new SharingException(SharingErrorKind.Unknown, "create_invite returned nothing.");
            return new TripInvite(
                SyncFieldMapper.GetString(row, "code") ?? throw new SharingException(SharingErrorKind.Unknown, "No code."),
                TripRole.Viewer,
                ParseUtc(SyncFieldMapper.GetString(row, "expires_at")) ?? DateTime.UtcNow.AddHours(InviteValidHours),
                maxUses);
        }

        public async Task<JoinedTrip> AcceptInviteAsync(string code)
        {
            var normalized = NormalizeCode(code);
            if (normalized.Length != CodeLength)
                throw new SharingException(SharingErrorKind.InvalidInvite);

            // TP-4: a wrong / expired / used-up code comes back as no rows (the server counts the wrong try).
            var rows = await CallAsync("accept_invite", new JsonObject { ["p_code"] = normalized }, inviteCall: true);
            var row = FirstRow(rows) ?? throw new SharingException(SharingErrorKind.InvalidInvite);
            var tripSyncId = SyncFieldMapper.GetString(row, "trip_id") ?? throw new SharingException(SharingErrorKind.InvalidInvite);

            // Bring the trip (and everything in it) onto this device right away.
            await _sync.SyncNowAsync();

            var db = await _database.Connection;
            var localId = await db.ExecuteScalarAsync<long>("SELECT Id FROM Trip WHERE SyncId = ?", tripSyncId);
            return new JoinedTrip(
                tripSyncId,
                TripRoles.Parse(SyncFieldMapper.GetString(row, "role")) ?? TripRole.Viewer,
                SyncFieldMapper.GetString(row, "trip_name"),
                localId > 0 ? (int)localId : null);
        }

        public async Task<IReadOnlyList<TripMember>> GetMembersAsync(int tripId)
        {
            var tripSyncId = await TripSyncIdAsync(tripId);
            var access = await _access.GetAccessAsync(tripId);
            if (!access.IsUploaded)
            {
                // Never uploaded = not shared yet: just the user.
                return Array.Empty<TripMember>();
            }

            var rows = await CallAsync("get_trip_members", new JsonObject { ["p_trip_id"] = tripSyncId });
            var me = ChangeTracker.CurrentUserId;
            var members = new List<TripMember>();
            if (rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var userId = SyncFieldMapper.GetString(row, "user_id");
                    if (string.IsNullOrEmpty(userId))
                        continue;

                    members.Add(new TripMember(
                        userId,
                        TripRoles.Parse(SyncFieldMapper.GetString(row, "role")) ?? TripRole.Viewer,
                        SyncFieldMapper.GetString(row, "display_name"),
                        SyncFieldMapper.GetString(row, "email"),
                        ParseUtc(SyncFieldMapper.GetString(row, "joined_at")) ?? DateTime.UtcNow,
                        string.Equals(userId, me, StringComparison.OrdinalIgnoreCase)));
                }
            }

            // Names for "son düzenleyen", also offline later. (TP-4: for editors/viewers the e-mail is empty now,
            // which also clears e-mails remembered by older versions.)
            await _access.RememberUsersAsync(members.Select(m => new KnownUser
            {
                UserId = m.UserId,
                DisplayName = Helpers.InputLimits.Truncate(m.DisplayName, 80),
                Email = Helpers.InputLimits.Truncate(m.Email, 254),
            }));

            return members;
        }

        public async Task<IReadOnlyList<MemberJoin>> GetRecentJoinsAsync(DateTime? sinceUtc, CancellationToken cancellationToken = default)
        {
            EnsureOnline();
            JsonElement rows;
            try
            {
                rows = await _client.RpcAsync("get_recent_joins", new JsonObject
                {
                    ["p_since"] = sinceUtc is { } since
                        ? DateTime.SpecifyKind(since, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                        : null,
                }, cancellationToken);
            }
            catch (SyncException ex)
            {
                throw new SharingException(MapError(ex, inviteCall: false), ex.Message, ex);
            }

            var joins = new List<MemberJoin>();
            if (rows.ValueKind != JsonValueKind.Array)
                return joins;

            foreach (var row in rows.EnumerateArray())
            {
                var tripId = SyncFieldMapper.GetString(row, "trip_id");
                var userId = SyncFieldMapper.GetString(row, "user_id");
                var joinedAt = ParseUtc(SyncFieldMapper.GetString(row, "joined_at"));
                if (string.IsNullOrEmpty(tripId) || string.IsNullOrEmpty(userId) || joinedAt is null)
                    continue;

                joins.Add(new MemberJoin(tripId, SyncFieldMapper.GetString(row, "trip_name"), userId,
                                         SyncFieldMapper.GetString(row, "display_name"), joinedAt.Value));
            }

            return joins;
        }

        public async Task SetRoleAsync(int tripId, string userId, TripRole role)
        {
            if (role == TripRole.Owner)
                throw new ArgumentOutOfRangeException(nameof(role), "Ownership can't be handed over here.");

            await CallAsync("set_member_role", new JsonObject
            {
                ["p_trip_id"] = await TripSyncIdAsync(tripId),
                ["p_user_id"] = userId,
                ["p_role"] = TripRoles.ToServer(role),
            });
        }

        public async Task RemoveMemberAsync(int tripId, string userId) =>
            await CallAsync("remove_member", new JsonObject
            {
                ["p_trip_id"] = await TripSyncIdAsync(tripId),
                ["p_user_id"] = userId,
            });

        public async Task LeaveTripAsync(int tripId)
        {
            await CallAsync("leave_trip", new JsonObject { ["p_trip_id"] = await TripSyncIdAsync(tripId) });

            // The next pull no longer lists the trip, so it is removed from this device.
            await _sync.SyncNowAsync();
        }

        // ------------------------------------------------------------------ helpers

        private async Task<string> TripSyncIdAsync(int tripId)
        {
            var db = await _database.Connection;
            var syncId = await db.ExecuteScalarAsync<string>("SELECT SyncId FROM Trip WHERE Id = ?", tripId);
            return string.IsNullOrEmpty(syncId) ? throw new SharingException(SharingErrorKind.Unknown, "Trip not found.") : syncId;
        }

        /// <summary>A trip must be in the cloud before it can be shared: sync once if it isn't yet.</summary>
        private async Task<string> RequireUploadedTripAsync(int tripId)
        {
            EnsureOnline();
            var tripSyncId = await TripSyncIdAsync(tripId);
            if ((await _access.GetAccessAsync(tripId)).IsUploaded)
                return tripSyncId;

            await _sync.SyncNowAsync();
            if (!(await _access.GetAccessAsync(tripId)).IsUploaded)
                throw new SharingException(SharingErrorKind.NotUploaded);

            return tripSyncId;
        }

        private void EnsureOnline()
        {
            if (ChangeTracker.CurrentUserId is null)
                throw new SharingException(SharingErrorKind.NotSignedIn);
            if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                throw new SharingException(SharingErrorKind.Offline);
        }

        private async Task<JsonElement> CallAsync(string function, JsonObject arguments, bool inviteCall = false)
        {
            EnsureOnline();
            try
            {
                return await _client.RpcAsync(function, arguments);
            }
            catch (SyncException ex)
            {
                throw new SharingException(MapError(ex, inviteCall), ex.Message, ex);
            }
        }

        internal static SharingErrorKind MapError(SyncException ex, bool inviteCall) => ex.Kind switch
        {
            SyncErrorKind.Network => SharingErrorKind.Offline,
            SyncErrorKind.NotSignedIn => SharingErrorKind.NotSignedIn,
            _ => ex.ServerCode switch
            {
                "PT429" when inviteCall => SharingErrorKind.TooManyAttempts,
                "P0002" when inviteCall => SharingErrorKind.InvalidInvite,
                "42501" or "28000" => SharingErrorKind.Forbidden,
                "P0002" => SharingErrorKind.Forbidden, // "no such member (or it is the owner)"
                _ => ex.Status switch
                {
                    403 => SharingErrorKind.Forbidden,
                    429 when inviteCall => SharingErrorKind.TooManyAttempts,
                    _ => SharingErrorKind.Unknown,
                },
            },
        };

        private static JsonElement? FirstRow(JsonElement rows) =>
            rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] :
            rows.ValueKind == JsonValueKind.Object ? rows : null;

        private static DateTime? ParseUtc(string? text) =>
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                ? value.UtcDateTime
                : null;
    }
}
