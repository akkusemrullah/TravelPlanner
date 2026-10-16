namespace TravelPlanner.Services
{
    /// <summary>
    /// Sends a notification when a trip's spending crosses 80% and 100% of its planned budget (TP-404).
    /// Each threshold fires once; if spending drops back below it (an expense deleted, the budget raised),
    /// crossing it again notifies again.
    /// </summary>
    public interface IBudgetAlertService
    {
        /// <summary>Re-evaluates the trip's budget usage. Call after an expense or the budget changed.</summary>
        Task CheckAsync(int tripId);
    }
}
