using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>The signed-in user's role in a trip (G6). A guest, and anyone on a trip that was never shared, is the owner.</summary>
    public enum TripRole
    {
        Owner,
        Editor,
        Viewer
    }

    public static class TripRoles
    {
        public const string Owner = "owner";
        public const string Editor = "editor";
        public const string Viewer = "viewer";

        public static TripRole? Parse(string? role) => role switch
        {
            Owner => TripRole.Owner,
            Editor => TripRole.Editor,
            Viewer => TripRole.Viewer,
            _ => null,
        };

        public static string ToServer(TripRole role) => role switch
        {
            TripRole.Owner => Owner,
            TripRole.Editor => Editor,
            _ => Viewer,
        };
    }

    /// <summary>What the user may do with one trip.</summary>
    public sealed record TripAccessInfo(TripRole Role, string? TripSyncId, bool IsSignedIn, bool IsUploaded)
    {
        /// <summary>Add, edit and delete the trip's content (owner, editor).</summary>
        public bool CanEdit => Role != TripRole.Viewer;

        /// <summary>Invite, change roles, remove members, delete the trip.</summary>
        public bool IsOwner => Role == TripRole.Owner;
    }

    /// <summary>Who changed something in a trip last, and when.</summary>
    public sealed record TripLastChange(string UserId, DateTime AtUtc);

    public enum TripAccessDenied
    {
        /// <summary>A viewer tried to change something.</summary>
        ReadOnly,
        /// <summary>Only the trip's owner may do this (delete the trip).</summary>
        OwnerOnly
    }

    /// <summary>Thrown by ChangeTracker when the user's role doesn't allow a write (G6). ViewModelBase shows a friendly message.</summary>
    public sealed class TripAccessException : Exception
    {
        public TripAccessException(TripAccessDenied reason)
            : base(reason == TripAccessDenied.ReadOnly ? "Read-only trip." : "Only the trip owner can do this.")
        {
            Reason = reason;
        }

        public TripAccessDenied Reason { get; }
    }

    /// <summary>Roles and "who changed it" for the trips in the open account database (G6).</summary>
    public interface ITripAccessService
    {
        Task<TripAccessInfo> GetAccessAsync(int tripId);

        /// <summary>The latest change anyone made in the trip (trip or any of its rows); null when unknown.</summary>
        Task<TripLastChange?> GetLastChangeAsync(int tripId);

        /// <summary>A name for an account: the trip mate's name or e-mail; null when unknown.</summary>
        Task<string?> GetUserNameAsync(string? userId);

        /// <summary>Remembers trip mates' names (from the members list) for offline display.</summary>
        Task RememberUsersAsync(IEnumerable<KnownUser> users);
    }

    /// <summary>
    /// Lets every ViewModel find out whether the open trip is read-only without a constructor change
    /// (see ViewModelBase.RefreshAccessAsync). Set once at start-up; null in unit tests (= full access).
    /// </summary>
    public static class TripAccess
    {
        public static ITripAccessService? Current { get; set; }
    }

    public class TripAccessService : ITripAccessService
    {
        private readonly IDatabaseService _database;

        public TripAccessService(IDatabaseService database)
        {
            _database = database;
        }

        public async Task<TripAccessInfo> GetAccessAsync(int tripId)
        {
            var db = await _database.Connection;
            var trip = await db.FindAsync<Trip>(tripId);
            var signedIn = ChangeTracker.CurrentUserId is not null;
            if (trip is null || !signedIn)
                return new TripAccessInfo(TripRole.Owner, trip?.SyncId, signedIn, trip is { ChangeSeq: > 0 });

            var role = await db.ExecuteScalarAsync<string>("SELECT Role FROM TripMembership WHERE TripSyncId = ?", trip.SyncId);
            return new TripAccessInfo(TripRoles.Parse(role) ?? TripRole.Owner, trip.SyncId, true, trip.ChangeSeq > 0);
        }

        public async Task<TripLastChange?> GetLastChangeAsync(int tripId)
        {
            var db = await _database.Connection;
            var rows = await db.QueryAsync<LastChangeRow>(
                "SELECT UpdatedBy, UpdatedAtUtc FROM (" +
                " SELECT UpdatedBy, UpdatedAtUtc FROM Trip WHERE Id = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Expense WHERE TripId = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Todo WHERE TripId = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Place WHERE TripId = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Activity WHERE TripId = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Attachment WHERE TripId = ?" +
                " UNION ALL SELECT UpdatedBy, UpdatedAtUtc FROM Participant WHERE TripId = ?" +
                ") WHERE UpdatedBy IS NOT NULL AND UpdatedBy <> '' ORDER BY UpdatedAtUtc DESC LIMIT 1",
                tripId, tripId, tripId, tripId, tripId, tripId, tripId);

            var row = rows.FirstOrDefault();
            return row?.UpdatedBy is { Length: > 0 } userId
                ? new TripLastChange(userId, DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc))
                : null;
        }

        public async Task<string?> GetUserNameAsync(string? userId)
        {
            if (string.IsNullOrEmpty(userId))
                return null;

            var db = await _database.Connection;
            var user = await db.FindAsync<KnownUser>(userId);
            return !string.IsNullOrWhiteSpace(user?.DisplayName) ? user!.DisplayName : user?.Email;
        }

        public async Task RememberUsersAsync(IEnumerable<KnownUser> users)
        {
            var db = await _database.Connection;
            foreach (var user in users.Where(u => !string.IsNullOrEmpty(u.UserId)))
                await db.InsertOrReplaceAsync(user);
        }

        /// <summary>Row shape of GetLastChangeAsync's query - not a table.</summary>
        public sealed class LastChangeRow
        {
            public string? UpdatedBy { get; set; }

            public DateTime UpdatedAtUtc { get; set; }
        }
    }
}
