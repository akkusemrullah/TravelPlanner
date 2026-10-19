namespace TravelPlanner.Services
{
    /// <summary>
    /// One calendar for a trip's start and end dates, as booking apps do: the first tap picks the start, the
    /// second the end. Android uses the Material range picker; elsewhere <see cref="IsSupported"/> is false and
    /// the form keeps its two separate date pickers.
    /// </summary>
    public interface IDateRangePicker
    {
        bool IsSupported { get; }

        /// <summary>Opens the calendar on <paramref name="start"/>–<paramref name="end"/>. Returns null when cancelled.</summary>
        Task<(DateTime Start, DateTime End)?> PickAsync(DateTime start, DateTime end, string title);
    }
}
