using System.Globalization;
using System.Text;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Services
{
    public class TripReportService : ITripReportService
    {
        private readonly ITripService _tripService;
        private readonly IExpenseService _expenseService;
        private readonly IActivityService _activityService;
        private readonly IPlaceService _placeService;
        private readonly ITodoService _todoService;
        private readonly ISplitService _splitService;
        private readonly ITripPdfRenderer _pdfRenderer;

        public TripReportService(
            ITripService tripService,
            IExpenseService expenseService,
            IActivityService activityService,
            IPlaceService placeService,
            ITodoService todoService,
            ISplitService splitService,
            ITripPdfRenderer pdfRenderer)
        {
            _tripService = tripService;
            _expenseService = expenseService;
            _activityService = activityService;
            _placeService = placeService;
            _todoService = todoService;
            _splitService = splitService;
            _pdfRenderer = pdfRenderer;
        }

        public async Task<TripReport?> BuildAsync(int tripId)
        {
            var trip = await _tripService.GetByIdAsync(tripId);
            if (trip is null)
                return null;

            var places = await _placeService.GetByTripIdAsync(tripId);
            var placeNames = places.ToDictionary(p => p.Id, p => p.Name);

            var activities = await _activityService.GetByTripIdAsync(tripId);
            foreach (var activity in activities)
                activity.PlaceName = activity.PlaceId is int id && placeNames.TryGetValue(id, out var name) ? name : null;

            var split = await _splitService.GetSummaryAsync(tripId);

            return new TripReport
            {
                Trip = trip,
                Plan = activities
                    .GroupBy(a => a.Date.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => (g.Key, (IReadOnlyList<TripActivity>)g.OrderBy(a => a.SortOrder).ToList()))
                    .ToList(),
                Categories = await _expenseService.GetCategorySummariesAsync(tripId),
                TotalExpenses = await _expenseService.GetTotalExpensesAsync(tripId),
                Places = places,
                Todos = await _todoService.GetByTripIdAsync(tripId),
                Split = split.Balances.Count > 0 ? split : null,
            };
        }

        public string ToText(TripReport report)
        {
            var trip = report.Trip;
            var currency = trip.EffectiveCurrencyCode;
            var culture = CultureInfo.CurrentCulture;
            var text = new StringBuilder();

            text.AppendLine($"✈️ {trip.Name}");
            text.AppendLine($"📍 {Place(trip)}");
            text.AppendLine($"📅 {trip.CompactDateRangeDisplay} · {string.Format(AppResources.Common_DaysFormat, trip.DurationInDays)}");
            if (!string.IsNullOrWhiteSpace(trip.Description))
                text.AppendLine(trip.Description.Trim());

            if (report.Plan.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"🗓️ {AppResources.Section_Itinerary}");
                foreach (var (day, activities) in report.Plan)
                {
                    var number = (day - trip.StartDate.Date).Days + 1;
                    text.AppendLine($"{string.Format(AppResources.Itinerary_DayFormat, number)} · {day.ToString("ddd d MMMM", culture)}");
                    foreach (var activity in activities)
                        text.AppendLine($"  • {ActivityLine(activity)}");
                }
            }

            if (report.Places.Count > 0)
            {
                text.AppendLine();
                text.AppendLine($"📌 {AppResources.Section_Places}");
                foreach (var place in report.Places)
                    text.AppendLine($"  {(place.IsVisited ? "✅" : "▫️")} {place.Name}");
            }

            text.AppendLine();
            text.AppendLine($"💰 {AppResources.Report_Budget}");
            text.AppendLine($"  {AppResources.Budget_Planned}: {trip.FormattedBudget}");
            text.AppendLine($"  {AppResources.Budget_Spent}: {MoneyFormatter.FormatIn(report.TotalExpenses, currency)}");
            foreach (var category in report.Categories.Where(c => c.Total > 0))
                text.AppendLine($"  {ExpenseCategoryDisplayConverter.ToDisplayText(category.Category.ToString())}: {MoneyFormatter.FormatIn(category.Total, currency)}");

            if (report.Split is { Settlements.Count: > 0 } split)
            {
                text.AppendLine();
                text.AppendLine($"🤝 {AppResources.Split_WhoOwesWhom}");
                foreach (var settlement in split.Settlements)
                    text.AppendLine($"  {SettlementLine(settlement, currency)}");
            }

            text.AppendLine();
            text.Append(AppResources.Report_Footer);
            return text.ToString();
        }

        public Task<string> CreatePdfAsync(TripReport report) => _pdfRenderer.RenderAsync(report, this);

        // ---- Shared line formats (text and PDF use the same wording) ----

        public static string Place(Trip trip) =>
            string.IsNullOrWhiteSpace(trip.Country) ? trip.City : $"{trip.City}, {trip.Country}";

        public static string ActivityLine(TripActivity activity)
        {
            var line = activity.HasTime ? $"{activity.TimeDisplay}  {activity.Title}" : activity.Title;
            if (activity.HasPlace)
                line += $" ({activity.PlaceName})";
            return line;
        }

        public static string SettlementLine(Settlement settlement, string currencyCode) =>
            string.Format(AppResources.Split_SettlementFormat, settlement.From.Name, settlement.To.Name,
                MoneyFormatter.FormatIn(MoneyConverter.FromMinor(settlement.AmountMinor), currencyCode));
    }

    /// <summary>Renders a <see cref="TripReport"/> to a PDF file. Kept behind an interface so the PDF library stays out of the unit tests.</summary>
    public interface ITripPdfRenderer
    {
        Task<string> RenderAsync(TripReport report, TripReportService texts);
    }
}
