using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Shared "are you sure?" dialog for deleting a trip (used by both TripListViewModel and
    /// TripDetailViewModel). Spells out how many related records go with it, since deleting a trip
    /// cascades to its to-dos, expenses, places and plan activities (TP-103). Everything goes into one trash batch,
    /// restorable for 30 days (G1) - the message says so.
    /// </summary>
    internal static class TripDeleteConfirmation
    {
        /// <summary>
        /// G6: only the owner deletes a trip, and a trip that is already in the cloud (others may share it) only
        /// while online. Explains why when not allowed. Used by the trip list and the trip page alike, so
        /// both paths follow the same rules. Without an access service (unit tests, guest start-up) everything is allowed.
        /// </summary>
        public static async Task<bool> CheckAllowedAsync(IDialogService dialogs, Trip trip, IConnectivity? connectivity)
        {
            switch (await GetBlockAsync(trip, connectivity))
            {
                case TripDeleteBlock.NotOwner:
                    await dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Access_OwnerOnly);
                    return false;
                case TripDeleteBlock.Offline:
                    await dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Sharing_ErrorOffline);
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>Why <paramref name="trip"/> can't be deleted right now (same rules as above, without a dialog - for deleting several trips).</summary>
        public static async Task<TripDeleteBlock> GetBlockAsync(Trip trip, IConnectivity? connectivity)
        {
            TripAccessInfo? access = null;
            if (TripAccess.Current is { } service)
            {
                try
                {
                    access = await service.GetAccessAsync(trip.Id);
                }
                catch (Exception ex)
                {
                    // The delete itself is still checked by ChangeTracker - never block on a failed lookup.
                    System.Diagnostics.Debug.WriteLine($"[TripDelete] Access lookup failed: {ex.Message}");
                }
            }

            if (access is { IsOwner: false })
                return TripDeleteBlock.NotOwner;

            if (access is { IsUploaded: true } && connectivity is not null && connectivity.NetworkAccess != NetworkAccess.Internet)
                return TripDeleteBlock.Offline;

            return TripDeleteBlock.None;
        }

        public static async Task<bool> AskAsync(IDialogService dialogs, ITripService tripService, Trip trip)
        {
            var (todos, expenses, places, activities) = await tripService.GetRelatedCountsAsync(trip.Id);

            var related = new List<string>();
            if (todos > 0) related.Add(string.Format(AppResources.TripDelete_TodosFormat, todos));
            if (expenses > 0) related.Add(string.Format(AppResources.TripDelete_ExpensesFormat, expenses));
            if (places > 0) related.Add(string.Format(AppResources.TripDelete_PlacesFormat, places));
            if (activities > 0) related.Add(string.Format(AppResources.TripDelete_ActivitiesFormat, activities));

            var message = related.Count == 0
                ? string.Format(AppResources.TripDelete_Message, trip.Name)
                : string.Format(AppResources.TripDelete_MessageWithRelated, trip.Name, string.Join(", ", related));

            return await dialogs.ConfirmAsync(AppResources.TripDelete_Title, message);
        }
    }

    /// <summary>Why a trip can't be deleted right now (see <see cref="TripDeleteConfirmation.GetBlockAsync"/>).</summary>
    internal enum TripDeleteBlock
    {
        None,

        /// <summary>Shared with the user - only the owner deletes a trip.</summary>
        NotOwner,

        /// <summary>The trip is in the cloud and the device is offline.</summary>
        Offline,
    }
}
