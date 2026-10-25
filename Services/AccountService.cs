using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public class AccountService : IAccountService
    {
        /// <summary>AppData/users/&lt;uid&gt;/ holds an account's database and attachments.</summary>
        public const string UsersFolderName = "users";

        private readonly IAuthService _auth;
        private readonly IDatabaseService _database;
        private readonly IAttachmentService _attachments;
        private readonly ITripService _trips;
        private readonly INotificationService _notifications;
        private readonly ISettingsService _settings;
        private readonly IDialogService _dialogs;
        private readonly string _appDataDirectory;
        private readonly string _guestDatabasePath;
        private readonly ISupabaseClient? _cloud;
        private readonly Func<ISyncService>? _sync;
        private readonly IDeviceIdentity? _device;
        private readonly SemaphoreSlim _switchLock = new(1, 1);
        private bool _initialized;

        /// <param name="appDataDirectory">FileSystem.AppDataDirectory (tests: a temp folder).</param>
        /// <param name="guestDatabasePath">The original database (AppConstants.DatabasePath) - the guest's data.</param>
        /// <param name="cloud">Supabase RPC/Storage client - needed only to delete the account (G7).</param>
        /// <param name="sync">TP-9: the sync engine, whose running round ends before the open data is switched
        /// (resolved when needed - the sync engine itself depends on this service).</param>
        public AccountService(
            IAuthService auth,
            IDatabaseService database,
            IAttachmentService attachments,
            ITripService trips,
            INotificationService notifications,
            ISettingsService settings,
            IDialogService dialogs,
            string appDataDirectory,
            string guestDatabasePath,
            ISupabaseClient? cloud = null,
            Func<ISyncService>? sync = null,
            IDeviceIdentity? device = null)
        {
            _device = device;
            _cloud = cloud;
            _sync = sync;
            _auth = auth;
            _database = database;
            _attachments = attachments;
            _trips = trips;
            _notifications = notifications;
            _settings = settings;
            _dialogs = dialogs;
            _appDataDirectory = appDataDirectory;
            _guestDatabasePath = guestDatabasePath;
        }

        /// <summary>uid whose data is open; null = the guest's.</summary>
        public string? ActiveUserId { get; private set; }

        public bool IsSignedIn => _auth.IsSignedIn;

        public string? Email => _auth.CurrentSession?.Email;

        public string? DisplayName => _auth.CurrentSession?.DisplayName;

        public event EventHandler? AccountChanged;

        public string UserDirectory(string userId)
        {
            // The uid becomes a folder name - accept nothing but a GUID.
            if (!Guid.TryParse(userId, out var id))
                throw new ArgumentException("Not a user id.", nameof(userId));
            return Path.Combine(_appDataDirectory, UsersFolderName, id.ToString("D"));
        }

        public string UserDatabasePath(string userId) => Path.Combine(UserDirectory(userId), AppConstants.DatabaseFilename);

        public async Task InitializeAsync()
        {
            if (!_initialized)
            {
                _initialized = true;
                _auth.SessionChanged += OnSessionChanged;
            }

            await _auth.InitializeAsync();

            await _switchLock.WaitAsync();
            try
            {
                if (_auth.CurrentSession is { } session)
                    await OpenAccountDataAsync(session.UserId);
                else
                    await OpenGuestDataAsync();
            }
            finally
            {
                _switchLock.Release();
            }
        }

        public async Task ActivateSignedInAccountAsync()
        {
            var session = _auth.CurrentSession ?? throw new InvalidOperationException("Not signed in.");

            await _switchLock.WaitAsync();
            try
            {
                if (ActiveUserId == session.UserId)
                    return;

                await EndSyncRoundAsync();
                await CancelRemindersAsync(); // the outgoing data's reminders (before anything moves)

                var userDatabase = UserDatabasePath(session.UserId);
                if (ActiveUserId is null && !File.Exists(userDatabase))
                {
                    // First sign-in of this account on this device: offer the guest's trips to the account.
                    var (trips, templates) = await CountGuestDataAsync();
                    if (trips + templates > 0 &&
                        await _dialogs.ConfirmAsync(
                            AppResources.Account_MoveGuestTitle,
                            string.Format(AppResources.Account_MoveGuestFormat, trips),
                            AppResources.Account_MoveGuestAccept,
                            AppResources.Account_MoveGuestDecline))
                    {
                        await MoveGuestDataAsync(session.UserId);
                    }
                }

                await OpenAccountDataAsync(session.UserId);
                await RescheduleRemindersAsync();

                if (string.IsNullOrWhiteSpace(_settings.UserName) && !string.IsNullOrWhiteSpace(session.DisplayName))
                    _settings.UserName = InputLimits.Truncate(session.DisplayName.Trim(), InputLimits.MaxUserNameLength)!;
            }
            finally
            {
                _switchLock.Release();
            }

            NoteDeviceAsync().FireAndForget(); // TP-55: signed in on this device
            AccountChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// TP-55: tells the server this account is used on this device (its salted hash, never the raw id), so the
        /// admin panel can show other accounts opened on the same phone. Never throws.
        /// </summary>
        private async Task NoteDeviceAsync(CancellationToken cancellationToken = default)
        {
            if (_cloud is null || _device is null || !_auth.IsSignedIn)
                return;
            try
            {
                await _cloud.RpcAsync("note_device", new JsonObject { ["p_device"] = _device.Hash }, cancellationToken);
            }
            catch (Exception ex) when (ex is SyncException or OperationCanceledException)
            {
                // offline, or the database without migration 37: next start tries again
                System.Diagnostics.Debug.WriteLine($"[Account] Device not noted: {ex.Message}");
            }
        }

        public async Task SignOutAsync(bool deleteLocalData = false)
        {
            var userId = _auth.CurrentSession?.UserId;
            await _auth.SignOutAsync();
            await SwitchToGuestAsync();

            // TP-16: only once the guest data is open - the account's database is closed by then
            if (deleteLocalData && userId is not null)
                DeleteUserFolder(userId);
        }

        public async Task DeleteAccountAsync(CancellationToken cancellationToken = default)
        {
            var session = _auth.CurrentSession ?? throw new InvalidOperationException("Not signed in.");
            var cloud = _cloud ?? throw new InvalidOperationException("No cloud client.");

            // 1. Files of the trips this account owns, right away. Only a head start: delete_account queues every file
            //    of those trips too, and the server's nightly clean-up deletes whatever is left (TP-7, migration 17).
            //    So a Storage hiccup here doesn't stop the account from being deleted.
            try
            {
                var files = new List<string>();
                foreach (var tripSyncId in await OwnedTripSyncIdsAsync())
                    files.AddRange(await cloud.ListAsync(SyncService.Bucket, $"trips/{tripSyncId}", cancellationToken));
                if (files.Count > 0)
                    await cloud.DeleteAsync(SyncService.Bucket, files, cancellationToken);
            }
            catch (SyncException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Account] Files left to the server's clean-up: {ex.Message}");
            }

            // 2. Server: owned trips (with everything in them), memberships, invites, profile, templates, the login.
            await cloud.RpcAsync("delete_account", new JsonObject(), cancellationToken);

            // 3. This device: forget the session, continue as the guest, remove the account's local copy.
            await _auth.SignOutAsync();
            await SwitchToGuestAsync();
            DeleteUserFolder(session.UserId);
        }

        public async Task<AccountEndReason> CheckAccountAsync(CancellationToken cancellationToken = default)
        {
            var session = _auth.CurrentSession;
            if (session is null || _cloud is null)
                return AccountEndReason.None;

            string state;
            try
            {
                // TP-14: with the session's own token - account_status only answers an account about itself. A token
                // issued before a deletion or ban still works for up to an hour; after that it can't be refreshed.
                var result = await _cloud.RpcAsync("account_status", new JsonObject { ["p_user_id"] = session.UserId }, cancellationToken);
                state = result.ValueKind == JsonValueKind.String ? result.GetString() ?? "active" : "active";
            }
            catch (SyncException ex) when (ex.Kind == SyncErrorKind.NotSignedIn)
            {
                // No token could be had, or the server refused it (401): the session check below tells "ended" from
                // "just offline".
                System.Diagnostics.Debug.WriteLine($"[Account] Status check without a session: {ex.Message}");
                state = "active";
            }
            catch (SyncException ex)
            {
                // Offline, server busy, or the database not updated yet (migration 8): check again next time.
                System.Diagnostics.Debug.WriteLine($"[Account] Status check skipped: {ex.Message}");
                return AccountEndReason.None;
            }

            var reason = state switch
            {
                "deleted" => AccountEndReason.Deleted,
                "banned" => AccountEndReason.Banned,
                // migration 30: signed out on every device from the admin panel; this token is older than that
                "signed_out" => AccountEndReason.SessionExpired,
                _ => AccountEndReason.None,
            };

            if (reason == AccountEndReason.None)
            {
                // The account is fine - but is this device's session still accepted? (A failed refresh ends it.)
                try
                {
                    await _auth.GetAccessTokenAsync(cancellationToken);
                    await NoteDeviceAsync(cancellationToken); // TP-55: on every start and return to the app
                    return AccountEndReason.None;
                }
                catch (AuthException ex) when (ex.Kind is AuthErrorKind.SessionExpired or AuthErrorKind.Banned)
                {
                    reason = ex.Kind == AuthErrorKind.Banned ? AccountEndReason.Banned : AccountEndReason.SessionExpired;
                }
            }

            if (_auth.CurrentSession?.UserId == session.UserId)
                await _auth.SignOutAsync();
            await SwitchToGuestAsync();

            // A banned account keeps its local copy (the ban may be lifted); a deleted one has nothing to come back to.
            if (reason == AccountEndReason.Deleted)
                DeleteUserFolder(session.UserId);

            return reason;
        }

        /// <summary>Trips of the open account in which it is the owner (from the memberships the last sync brought).</summary>
        private async Task<List<string>> OwnedTripSyncIdsAsync()
        {
            var db = await _database.Connection;
            var memberships = await db.Table<TripMembership>().ToListAsync();
            return memberships
                .Where(m => m.Role == "owner" && Guid.TryParse(m.TripSyncId, out _))
                .Select(m => m.TripSyncId)
                .ToList();
        }

        private void DeleteUserFolder(string userId)
        {
            try
            {
                var directory = UserDirectory(userId);
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                // Nothing depends on the folder any more (signed out, or the account is gone); a leftover only takes space.
                System.Diagnostics.Debug.WriteLine($"[Account] Could not remove the account's local data: {ex.Message}");
            }
        }

        // The session can also end on its own (refresh token revoked, account deleted elsewhere).
        private void OnSessionChanged(object? sender, EventArgs e)
        {
            if (_auth.CurrentSession is null)
                SwitchToGuestAsync().FireAndForget();
        }

        private async Task SwitchToGuestAsync()
        {
            await _switchLock.WaitAsync();
            try
            {
                if (ActiveUserId is null)
                    return;

                await EndSyncRoundAsync();
                await CancelRemindersAsync();
                await OpenGuestDataAsync();
                await RescheduleRemindersAsync();
            }
            finally
            {
                _switchLock.Release();
            }

            AccountChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// TP-9: a sync round still running for the outgoing account is stopped (a long upload too) and waited for,
        /// so it neither sends the outgoing data with the next account's token nor writes into the next one's data.
        /// </summary>
        private async Task EndSyncRoundAsync()
        {
            if (_sync is null)
                return;
            try
            {
                await _sync().CancelCurrentRoundAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Account] Stopping the sync round failed: {ex}");
            }
        }

        private async Task OpenAccountDataAsync(string userId)
        {
            var directory = UserDirectory(userId);
            Directory.CreateDirectory(directory);
            ChangeTracker.CurrentUserId = userId;
            _settings.UseAccount(userId); // each account has its own name; the guest's is "Travel Planner" until set
            _attachments.UseRootDirectory(directory);
            await _database.SwitchDatabaseAsync(Path.Combine(directory, AppConstants.DatabaseFilename));
            ActiveUserId = userId;
        }

        private async Task OpenGuestDataAsync()
        {
            ChangeTracker.CurrentUserId = null;
            _settings.UseAccount(null);
            _attachments.UseRootDirectory(_appDataDirectory);
            await _database.SwitchDatabaseAsync(_guestDatabasePath);
            ActiveUserId = null;
        }

        private async Task<(int Trips, int Templates)> CountGuestDataAsync()
        {
            var db = await _database.Connection;
            var trips = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Trip");
            var templates = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PackingTemplate");
            return (trips, templates);
        }

        /// <summary>
        /// Moves the guest database and attachment folder into the account's folder. Their outbox already lists
        /// every row, so the first sync uploads all of it to the account. The guest starts empty afterwards.
        /// </summary>
        private async Task MoveGuestDataAsync(string userId)
        {
            var directory = UserDirectory(userId);
            Directory.CreateDirectory(directory);
            var targetDatabase = Path.Combine(directory, AppConstants.DatabaseFilename);

            await _database.CloseAsync();

            foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
            {
                var source = _guestDatabasePath + suffix;
                if (File.Exists(source))
                    File.Move(source, targetDatabase + suffix, overwrite: true);
            }

            var guestFiles = Path.Combine(_appDataDirectory, AttachmentService.FolderName);
            var accountFiles = Path.Combine(directory, AttachmentService.FolderName);
            if (Directory.Exists(guestFiles))
            {
                if (!Directory.Exists(accountFiles))
                {
                    Directory.Move(guestFiles, accountFiles);
                }
                else
                {
                    MoveDirectoryContents(guestFiles, accountFiles);
                    Directory.Delete(guestFiles, recursive: true);
                }
            }
        }

        private static void MoveDirectoryContents(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Move(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            foreach (var directory in Directory.GetDirectories(source))
                MoveDirectoryContents(directory, Path.Combine(target, Path.GetFileName(directory)));
        }

        // Reminders are keyed by the local trip id, which repeats across databases: the outgoing data's reminders
        // go, the incoming data's are scheduled. A notification hiccup never blocks the switch.
        private async Task CancelRemindersAsync()
        {
            try
            {
                foreach (var trip in await _trips.GetAllAsync())
                    _notifications.CancelTripNotifications(trip.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Account] Cancelling reminders failed: {ex}");
            }
        }

        private async Task RescheduleRemindersAsync()
        {
            try
            {
                foreach (var trip in await _trips.GetAllAsync())
                {
                    _notifications.CancelTripNotifications(trip.Id);
                    await _notifications.ScheduleTripNotificationsAsync(trip);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Account] Scheduling reminders failed: {ex}");
            }
        }
    }
}
