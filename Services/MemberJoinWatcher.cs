using System.Globalization;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// TP-4: tells the owner when someone joins one of their trips, so a code that went further than intended is
    /// noticed right away and not only on the members page. Asks the server (get_recent_joins) after a sync.
    /// </summary>
    public interface IMemberJoinWatcher
    {
        /// <summary>
        /// Who joined the signed-in user's own trips since the last check (at most every <see cref="MemberJoinWatcher.MinInterval"/>).
        /// Empty for a guest, offline, or on the first check on this device (that one only sets the starting point).
        /// </summary>
        Task<IReadOnlyList<MemberJoin>> CheckAsync(CancellationToken cancellationToken = default);
    }

    public class MemberJoinWatcher : IMemberJoinWatcher
    {
        /// <summary>Syncs run often; the server is asked at most this often.</summary>
        public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

        /// <summary>Preference per account: ticks (UTC) of the newest join already reported.</summary>
        internal const string SeenKeyPrefix = "sharing.joins_seen.";

        private static readonly IReadOnlyList<MemberJoin> None = Array.Empty<MemberJoin>();

        private readonly ISharingService _sharing;
        private readonly IPreferences _preferences;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private DateTime _lastCheckUtc = DateTime.MinValue;
        private string? _lastUserId;

        public MemberJoinWatcher(ISharingService sharing, IPreferences preferences)
        {
            _sharing = sharing;
            _preferences = preferences;
        }

        /// <summary>The clock (replaced in tests).</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public async Task<IReadOnlyList<MemberJoin>> CheckAsync(CancellationToken cancellationToken = default)
        {
            var userId = ChangeTracker.CurrentUserId;
            if (userId is null)
                return None;

            // A check already running answers for this one too.
            if (!await _gate.WaitAsync(0, cancellationToken))
                return None;

            try
            {
                var now = UtcNow();
                if (userId == _lastUserId && now - _lastCheckUtc < MinInterval)
                    return None;

                _lastUserId = userId;
                _lastCheckUtc = now; // also after a failure, so a missing server function isn't asked on every sync

                var key = SeenKeyPrefix + userId;
                var seenTicks = _preferences.Get(key, 0L);
                DateTime? since = seenTicks > 0 ? new DateTime(seenTicks, DateTimeKind.Utc) : null;

                IReadOnlyList<MemberJoin> joins;
                try
                {
                    joins = await _sharing.GetRecentJoinsAsync(since, cancellationToken);
                }
                catch (SharingException ex)
                {
                    // Offline or a server hiccup: try again later.
                    System.Diagnostics.Debug.WriteLine($"[MemberJoinWatcher] {ex.Kind}: {ex.Message}");
                    return None;
                }

                var newest = joins.Count > 0 ? joins.Max(j => j.JoinedAtUtc) : since ?? now;
                if (newest.Ticks > seenTicks)
                    _preferences.Set(key, newest.Ticks);

                // First check on this device (or after an update): only remember where we are.
                return since is null ? None : joins;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// "Ayşe, "Roma" seyahatine katıldı." / "3 kişi "Roma" seyahatine katıldı." / "Seyahatlerine 4 yeni üye katıldı."
        /// - always with the hint that a stranger can be removed.
        /// </summary>
        public static (string Title, string Message) BuildMessage(IReadOnlyList<MemberJoin> joins)
        {
            string text;
            var trips = joins.Select(j => j.TripSyncId).Distinct().Count();
            if (joins.Count == 1)
            {
                var join = joins[0];
                var name = string.IsNullOrWhiteSpace(join.DisplayName) ? AppResources.Members_Someone : join.DisplayName!.Trim();
                text = string.Format(CultureInfo.CurrentCulture, AppResources.Members_JoinedOneFormat, name, join.TripName ?? string.Empty);
            }
            else if (trips == 1)
            {
                text = string.Format(CultureInfo.CurrentCulture, AppResources.Members_JoinedManyFormat, joins.Count, joins[0].TripName ?? string.Empty);
            }
            else
            {
                text = string.Format(CultureInfo.CurrentCulture, AppResources.Members_JoinedTripsFormat, joins.Count);
            }

            return (AppResources.Members_JoinedTitle, text + " " + AppResources.Members_JoinedHint);
        }
    }
}
