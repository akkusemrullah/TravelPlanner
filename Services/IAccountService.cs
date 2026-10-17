namespace TravelPlanner.Services
{
    /// <summary>Why <see cref="IAccountService.CheckAccountAsync"/> ended the session (G7).</summary>
    public enum AccountEndReason
    {
        /// <summary>Nothing happened: the account is fine, or it couldn't be checked (offline).</summary>
        None,
        /// <summary>The account was deleted on the server (e.g. by the admin in Supabase).</summary>
        Deleted,
        /// <summary>The account was banned; signing in again is refused while the ban lasts.</summary>
        Banned,
        /// <summary>The server no longer accepts the session (signed out elsewhere, password changed …).</summary>
        SessionExpired
    }

    /// <summary>
    /// Which local data the app works on (G3). A guest uses the original database and attachment folder; every
    /// account gets its own database + files under AppData/users/&lt;uid&gt;/, so two accounts on one device (or an
    /// account and a guest) never mix. On the first sign-in on a device the guest's trips can move into the account.
    /// </summary>
    public interface IAccountService
    {
        bool IsSignedIn { get; }

        /// <summary>uid whose data is open; null = the guest's.</summary>
        string? ActiveUserId { get; }

        /// <summary>E-mail of the signed-in account (null for a guest).</summary>
        string? Email { get; }

        /// <summary>Name from the account (Google name or the one given at sign-up).</summary>
        string? DisplayName { get; }

        /// <summary>Raised (on any thread) after the local data has been switched to another account or to the guest.</summary>
        event EventHandler? AccountChanged;

        /// <summary>App start: restores the stored session and opens that account's data. Works offline.</summary>
        Task InitializeAsync();

        /// <summary>
        /// Call after a successful sign-in: offers to move this device's guest trips into the account (first time
        /// only), then switches the app to the account's data.
        /// </summary>
        Task ActivateSignedInAccountAsync();

        /// <summary>Signs out and switches back to the guest data. The account's data stays on the device.</summary>
        Task SignOutAsync();

        /// <summary>
        /// Deletes the account for good (G7): the files and trips it owns in the cloud, its place in trips shared with
        /// it, its profile and login, then this device's copy of the account's data. The app continues as a guest.
        /// Needs a connection - a <see cref="Sync.SyncException"/> (e.g. <see cref="Sync.SyncErrorKind.Network"/>) means
        /// nothing was deleted on the server.
        /// </summary>
        Task DeleteAccountAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Asks the server whether the signed-in account still exists and isn't banned (app start and return to the
        /// foreground). If not, signs out and switches to the guest data (a deleted account's local copy is removed
        /// too) and returns why, so the app can tell the user. Offline it changes nothing and returns None.
        /// </summary>
        Task<AccountEndReason> CheckAccountAsync(CancellationToken cancellationToken = default);
    }
}
