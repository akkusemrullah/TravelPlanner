using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// TP-48: "Davet koduyla katıl" in one place, shared by the home screen, the trip list and Settings: asks for the
    /// code, joins the trip and says what went wrong. The caller opens the trip.
    /// </summary>
    public interface IJoinTripFlow
    {
        /// <summary>The joined trip's local id, or null when nothing was joined or it hasn't synced in yet (the user was told).</summary>
        Task<int?> RunAsync();
    }

    public sealed class JoinTripFlow : IJoinTripFlow
    {
        private readonly ISharingService _sharing;
        private readonly IAccountService _account;
        private readonly IDialogService _dialogs;
        private readonly IConnectivity? _connectivity;

        public JoinTripFlow(ISharingService sharing, IAccountService account, IDialogService dialogs, IConnectivity? connectivity = null)
        {
            _sharing = sharing;
            _account = account;
            _dialogs = dialogs;
            _connectivity = connectivity;
        }

        public async Task<int?> RunAsync()
        {
            // told before typing the code, not after
            if (!_account.IsSignedIn)
                return await TellAsync(SharingErrorKind.NotSignedIn);
            if (_connectivity is not null && _connectivity.NetworkAccess != NetworkAccess.Internet)
                return await TellAsync(SharingErrorKind.Offline);

            var code = await _dialogs.PromptAsync(AppResources.Join_Title, AppResources.Join_Message,
                                                  accept: AppResources.Join_Accept, cancel: AppResources.Common_Cancel);
            if (string.IsNullOrWhiteSpace(code)) return null;

            JoinedTrip joined;
            try
            {
                joined = await _sharing.AcceptInviteAsync(code);
            }
            catch (SharingException ex)
            {
                return await TellAsync(ex.Kind);
            }

            if (joined.LocalTripId is int tripId)
                return tripId;

            // Joined, but the sync hasn't brought it in yet (e.g. connection dropped): it appears with the next sync.
            await _dialogs.ShowMessageAsync(AppResources.Common_Info,
                string.Format(AppResources.Join_PendingFormat, joined.TripName ?? string.Empty));
            return null;
        }

        private async Task<int?> TellAsync(SharingErrorKind kind)
        {
            await _dialogs.ShowMessageAsync(AppResources.Common_Info, SharingTexts.ErrorFor(kind));
            return null;
        }
    }
}
