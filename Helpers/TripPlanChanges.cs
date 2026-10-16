namespace TravelPlanner.Helpers
{
    /// <summary>
    /// Raised when a trip's plan or to-do list changed on this device (an activity or to-do saved, deleted or
    /// reordered). App listens and re-plans that trip's reminders, which mention the day's activities and the open
    /// to-dos. A static hook so the data services stay free of notification code (and unit tests of it).
    /// </summary>
    public static class TripPlanChanges
    {
        public static event Action<int>? Changed;

        public static void Notify(int tripId)
        {
            if (tripId <= 0)
                return;

            try
            {
                Changed?.Invoke(tripId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Reminders] Plan change handler failed: {ex.Message}");
            }
        }
    }
}
