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

    /// <summary>A fresh invite code (8 letters/digits) and when it stops working.</summary>
    public sealed record TripInvite(string Code, TripRole Role, DateTime ExpiresAtUtc);

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
        /// <summary>Makes an invite code for the trip (uploads the trip first if it isn't in the cloud yet). Owner only.</summary>
        Task<TripInvite> CreateInviteAsync(int tripId, TripRole role);

        /// <summary>Joins the trip behind <paramref name="code"/> and syncs it onto this device.</summary>
        Task<JoinedTrip> AcceptInviteAsync(string code);

        Task<IReadOnlyList<TripMember>> GetMembersAsync(int tripId);

        /// <summary>Owner: makes a member an editor or a viewer.</summary>
        Task SetRoleAsync(int tripId, string userId, TripRole role);

        /// <summary>Owner: takes a member off the trip.</summary>
        Task RemoveMemberAsync(int tripId, string userId);

        /// <summary>Editor/viewer: leaves the trip; it disappears from this device after the sync.</summary>
        Task LeaveTripAsync(int tripId);
    }

    public class SharingService : ISharingService
    {
        /// <summary>How long an invite code works.</summary>
        public const int InviteValidHours = 24 * 7;

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

        public async Task<TripInvite> CreateInviteAsync(int tripId, TripRole role)
        {
            if (role == TripRole.Owner)
                throw new ArgumentOutOfRangeException(nameof(role), "An invite is for an editor or a viewer.");

            var tripSyncId = await RequireUploadedTripAsync(tripId);
            var rows = await CallAsync("create_invite", new JsonObject
            {
                ["p_trip_id"] = tripSyncId,
                ["p_role"] = TripRoles.ToServer(role),
                ["p_valid_hours"] = InviteValidHours,
                ["p_max_uses"] = null,
            });

            var row = FirstRow(rows) ?? throw new SharingException(SharingErrorKind.Unknown, "create_invite returned nothing.");
            return new TripInvite(
                SyncFieldMapper.GetString(row, "code") ?? throw new SharingException(SharingErrorKind.Unknown, "No code."),
                role,
                ParseUtc(SyncFieldMapper.GetString(row, "expires_at")) ?? DateTime.UtcNow.AddHours(InviteValidHours));
        }

        public async Task<JoinedTrip> AcceptInviteAsync(string code)
        {
            var normalized = NormalizeCode(code);
            if (normalized.Length != CodeLength)
                throw new SharingException(SharingErrorKind.InvalidInvite);

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

            // Names for "son düzenleyen", also offline later.
            await _access.RememberUsersAsync(members.Select(m => new KnownUser
            {
                UserId = m.UserId,
                DisplayName = Helpers.InputLimits.Truncate(m.DisplayName, 80),
                Email = Helpers.InputLimits.Truncate(m.Email, 254),
            }));

            return members;
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
                "P0002" when inviteCall => SharingErrorKind.InvalidInvite,
                "42501" or "28000" => SharingErrorKind.Forbidden,
                "P0002" => SharingErrorKind.Forbidden, // "no such member (or it is the owner)"
                _ => ex.Status == 403 ? SharingErrorKind.Forbidden : SharingErrorKind.Unknown,
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
