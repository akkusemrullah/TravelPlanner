using TravelPlanner.Helpers;
using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>
    /// SEC-01: one set of rules for every row that reaches the local database from OUTSIDE the app's own forms -
    /// a restored backup (<see cref="BackupService.Sanitize"/>) and a row pulled from the server
    /// (<see cref="Sync.RemoteApplier"/>). Both sources can carry anything (a hand-edited file, an editor writing to
    /// the REST API directly), while the rest of the app trusts what is stored: totals and currency conversions
    /// overflow on huge amounts, reminder date math falls off DateTime's range, unknown enum values break the UI.
    /// The limits are those of <see cref="InputLimits"/> (and the server's CHECK constraints, migration 11).
    /// </summary>
    public static class EntityValidator
    {
        /// <summary>Largest position of an activity within its day (server: activities_sort_order_range).</summary>
        public const int MaxSortOrder = 100_000;

        /// <summary>
        /// Checks <paramref name="entity"/> and fixes what can safely be fixed in place: text trimmed to its column
        /// limit, day values without a time part, a broken or out-of-order activity time dropped, an unknown trip
        /// currency replaced by the default (like trips created before multi-currency support).
        /// Returns why the row must NOT be stored ("invalid amount" …), or null when it is fine.
        /// </summary>
        public static string? Validate(object entity)
        {
            switch (entity)
            {
                case Trip trip:
                    InputLimits.EnforceTextLimits(trip);
                    trip.StartDate = trip.StartDate.Date;
                    trip.EndDate = trip.EndDate.Date;
                    if (!InputLimits.IsValidDate(trip.StartDate) || !InputLimits.IsValidDate(trip.EndDate) || trip.EndDate < trip.StartDate)
                        return "invalid dates";
                    if (!InputLimits.IsValidMinor(trip.PlannedBudgetMinor))
                        return "invalid budget";
                    if (trip.CurrencyCode is not null && !MoneyFormatter.IsSupported(trip.CurrencyCode))
                        trip.CurrencyCode = null; // falls back to the default currency, like pre-TP-403 trips
                    return null;

                case Expense expense:
                    InputLimits.EnforceTextLimits(expense);
                    if (!InputLimits.IsValidMinor(expense.AmountMinor) || !InputLimits.IsValidMinor(expense.OriginalAmountMinor))
                        return "invalid amount";
                    if (!InputLimits.IsValidRate(expense.ExchangeRate))
                        return "invalid exchange rate";
                    if (!InputLimits.IsValidDate(expense.Date))
                        return "invalid date";
                    if (!Enum.IsDefined(expense.Category))
                        return "invalid category";
                    if (expense.OriginalCurrencyCode is not null && !MoneyFormatter.IsSupported(expense.OriginalCurrencyCode))
                        return "unknown currency";
                    if (expense.AmountCurrencyCode is not null && !MoneyFormatter.IsSupported(expense.AmountCurrencyCode))
                        expense.AmountCurrencyCode = null; // TP-11: just a label - fall back to the trip's currency
                    return null;

                case Place place:
                    InputLimits.EnforceTextLimits(place);
                    return PlaceService.IsValidCoordinate(place.Latitude, place.Longitude) ? null : "invalid coordinates";

                case TripActivity activity:
                    InputLimits.EnforceTextLimits(activity);
                    activity.Date = activity.Date.Date;
                    if (!InputLimits.IsValidDate(activity.Date))
                        return "invalid date";
                    if (activity.StartTime is { } start && (start < TimeSpan.Zero || start >= TimeSpan.FromDays(1)))
                        activity.StartTime = null;
                    if (activity.EndTime is { } end &&
                        (activity.StartTime is not { } begin || end <= begin || end >= TimeSpan.FromDays(1)))
                        activity.EndTime = null;
                    activity.SortOrder = Math.Clamp(activity.SortOrder, 0, MaxSortOrder);
                    return null;

                case TripAttachment attachment:
                    InputLimits.EnforceTextLimits(attachment);
                    if (!Enum.IsDefined(attachment.Kind))
                        return "invalid kind";
                    if (attachment.SizeBytes < 0)
                        return "invalid size";
                    // TP-5: only photo/PDF types are kept (the app opens files by their extension anyway), and only a
                    // cloud path of the shape "trips/<uuid>/<uuid>.<ext>" (AttachmentService checks the ids before a download)
                    attachment.ContentType = AttachmentService.CloudContentType(null, attachment.ContentType);
                    if (!string.IsNullOrEmpty(attachment.RemotePath) && !AttachmentService.IsWellFormedRemotePath(attachment.RemotePath))
                        attachment.RemotePath = null;
                    return null;

                case PackingTemplate template:
                    InputLimits.EnforceTextLimits(template);
                    template.Items ??= string.Empty;
                    return null;

                default: // to-dos, participants, expense shares: only text limits
                    InputLimits.EnforceTextLimits(entity);
                    return null;
            }
        }
    }
}
