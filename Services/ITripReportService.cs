using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    /// <summary>A trip, gathered for sharing / printing (TP-502, TP-503). Not persisted.</summary>
    public sealed class TripReport
    {
        public required Trip Trip { get; init; }
        public IReadOnlyList<(DateTime Day, IReadOnlyList<TripActivity> Activities)> Plan { get; init; } = Array.Empty<(DateTime, IReadOnlyList<TripActivity>)>();
        public IReadOnlyList<ExpenseCategorySummary> Categories { get; init; } = Array.Empty<ExpenseCategorySummary>();
        public decimal TotalExpenses { get; init; }
        public IReadOnlyList<Place> Places { get; init; } = Array.Empty<Place>();
        public IReadOnlyList<TodoItem> Todos { get; init; } = Array.Empty<TodoItem>();
        public SplitSummary? Split { get; init; }
        public DateTime GeneratedAt { get; init; } = DateTime.Now;
    }

    /// <summary>Builds a trip's summary as plain text (to share in a message) or as a PDF file.</summary>
    public interface ITripReportService
    {
        Task<TripReport?> BuildAsync(int tripId);

        /// <summary>Plain-text summary for the system share sheet.</summary>
        string ToText(TripReport report);

        /// <summary>Writes the summary as a PDF into the cache folder and returns its path.</summary>
        Task<string> CreatePdfAsync(TripReport report);
    }
}
