using CommunityToolkit.Mvvm.Messaging;

namespace TravelPlanner.Helpers
{
    /// <summary>Sent after the sync engine wrote other people's changes into the local database (G4).</summary>
    public sealed class RemoteDataChangedMessage
    {
    }

    /// <summary>
    /// Lets a visible page reload when changes arrive from the cloud. Pages subscribe in OnAppearing and
    /// unsubscribe in OnDisappearing; App publishes after each sync that changed something.
    /// </summary>
    public static class RemoteChanges
    {
        public static void Subscribe(object recipient, Action reload)
        {
            if (WeakReferenceMessenger.Default.IsRegistered<RemoteDataChangedMessage>(recipient))
                return;

            WeakReferenceMessenger.Default.Register<RemoteDataChangedMessage>(recipient,
                (_, _) => MainThread.BeginInvokeOnMainThread(reload));
        }

        public static void Unsubscribe(object recipient) =>
            WeakReferenceMessenger.Default.Unregister<RemoteDataChangedMessage>(recipient);

        public static void Publish() => WeakReferenceMessenger.Default.Send(new RemoteDataChangedMessage());

        /// <summary>Set by App: asks the sync engine for a (throttled) round. Null in unit tests and for guests' start-up.</summary>
        public static Action? FreshDataRequested { get; set; }

        /// <summary>A page is loading its data (opened, or pulled to refresh) - check the cloud for newer changes.</summary>
        public static void RequestFresh()
        {
            try
            {
                FreshDataRequested?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Sync] Refresh request failed: {ex.Message}");
            }
        }
    }
}
