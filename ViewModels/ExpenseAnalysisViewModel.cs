using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Helpers.Charts;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One row of the category legend under the distribution bar.</summary>
    public sealed class CategoryBreakdownItem
    {
        public CategoryBreakdownItem(ExpenseCategory category, decimal total, int count, double fraction, string currencyCode)
        {
            Category = category;
            Total = total;
            Count = count;
            Fraction = fraction;
            Label = ExpenseCategoryDisplayConverter.ToDisplayText(category.ToString());
            FormattedTotal = MoneyFormatter.FormatIn(total, currencyCode);
            PercentText = $"%{Math.Round(fraction * 100):0}";
            Color = ChartPalette.ForCategory(category);
        }

        public ExpenseCategory Category { get; }
        public decimal Total { get; }
        public int Count { get; }
        public double Fraction { get; }
        public string Label { get; }
        public string FormattedTotal { get; }
        public string PercentText { get; }
        public Color Color { get; }
        public Brush Brush => new SolidColorBrush(Color);
    }

    /// <summary>
    /// Spending analysis for a trip (TP-404): category distribution (from ExpenseService.GetCategorySummariesAsync)
    /// and a per-day bar chart against the planned daily budget.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class ExpenseAnalysisViewModel : ViewModelBase
    {
        private readonly ITripService _tripService;
        private readonly IExpenseService _expenseService;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private Trip? trip;

        [ObservableProperty]
        private bool hasData;

        [ObservableProperty]
        private bool hasNoData;

        [ObservableProperty]
        private string totalFormatted = string.Empty;

        [ObservableProperty]
        private string dailyAverageFormatted = string.Empty;

        [ObservableProperty]
        private string highestDayFormatted = string.Empty;

        /// <summary>"Günlük bütçe: ₺1.250" - empty when the trip has no budget.</summary>
        [ObservableProperty]
        private string dailyBudgetText = string.Empty;

        [ObservableProperty]
        private bool hasDailyBudget;

        /// <summary>Accessible summary of the daily chart for screen readers.</summary>
        [ObservableProperty]
        private string dailyChartDescription = string.Empty;

        public ObservableCollection<CategoryBreakdownItem> Categories { get; } = new RangeObservableCollection<CategoryBreakdownItem>();

        public CategoryBarDrawable CategoryChart { get; } = new();

        public DailySpendingDrawable DailyChart { get; } = new();

        /// <summary>Raised after new data is in the drawables, so the page can redraw its GraphicsViews.</summary>
        public event EventHandler? ChartsUpdated;

        public ExpenseAnalysisViewModel(ITripService tripService, IExpenseService expenseService, IDialogService dialogs)
            : base(dialogs)
        {
            _tripService = tripService;
            _expenseService = expenseService;
        }

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            Trip = await _tripService.GetByIdAsync(TripId);
            if (Trip is null) return;

            var currency = Trip.EffectiveCurrencyCode;
            var summaries = await _expenseService.GetCategorySummariesAsync(TripId);
            var daily = await _expenseService.GetDailyTotalsAsync(TripId);

            var total = summaries.Sum(s => s.Total);
            HasData = total > 0;
            HasNoData = !HasData;

            Categories.ReplaceAll(summaries.Where(s => s.Total > 0).Select(summary => new CategoryBreakdownItem(summary.Category, summary.Total, summary.Count, (double)(summary.Total / total), currency)));

            CategoryChart.Shares = Categories.Select(c => new CategoryShare(c.Category, c.Fraction)).ToList();

            var bars = BuildDailyBars(Trip, daily);
            var dailyBudget = Trip.PlannedBudget > 0 ? Math.Round(Trip.PlannedBudget / Math.Max(Trip.DurationInDays, 1), 2) : 0m;

            DailyChart.Bars = bars;
            DailyChart.DailyBudget = dailyBudget;
            DailyChart.FormatValue = v => MoneyFormatter.FormatIn(v, currency, 0);

            TotalFormatted = MoneyFormatter.FormatIn(total, currency);
            var spendingDays = bars.Count(b => b.Total > 0);
            DailyAverageFormatted = MoneyFormatter.FormatIn(spendingDays == 0 ? 0m : total / spendingDays, currency);

            var highest = bars.OrderByDescending(b => b.Total).FirstOrDefault();
            HighestDayFormatted = highest is null || highest.Total <= 0
                ? "–"
                : $"{MoneyFormatter.FormatIn(highest.Total, currency)} · {highest.Date:d MMM}";

            HasDailyBudget = dailyBudget > 0;
            DailyBudgetText = HasDailyBudget
                ? string.Format(AppResources.Analysis_DailyBudgetFormat, MoneyFormatter.FormatIn(dailyBudget, currency))
                : string.Empty;

            DailyChartDescription = string.Join(", ", bars.Where(b => b.Total > 0)
                .Select(b => $"{b.Date:d MMMM}: {MoneyFormatter.FormatIn(b.Total, currency)}"));

            ChartsUpdated?.Invoke(this, EventArgs.Empty);
        }, AppResources.Analysis_LoadError);

        /// <summary>
        /// One bar per trip day (zero for days without spending), widened to include any expense dated outside
        /// the trip; capped at <see cref="AppConstants.MaxItineraryDays"/> bars, always keeping the trip's first day.
        /// </summary>
        internal static List<DailyBar> BuildDailyBars(Trip trip, IReadOnlyList<DailyExpenseTotal> daily)
        {
            var first = trip.StartDate.Date;
            var last = trip.EndDate.Date < first ? first : trip.EndDate.Date;

            if (daily.Count > 0)
            {
                if (daily[0].Date.Date < first) first = daily[0].Date.Date;
                if (daily[^1].Date.Date > last) last = daily[^1].Date.Date;
            }

            if ((last - first).Days >= AppConstants.MaxItineraryDays)
            {
                // Too long to chart: keep the trip itself in view (a booking made months earlier must not push
                // the actual trip days off the chart) - the window ends at the last day, but never starts after
                // the trip's first day.
                var tripStart = trip.StartDate.Date;
                first = last.AddDays(-(AppConstants.MaxItineraryDays - 1));
                if (first > tripStart)
                    first = tripStart;
                last = first.AddDays(AppConstants.MaxItineraryDays - 1);
            }

            var byDay = daily.GroupBy(d => d.Date.Date).ToDictionary(g => g.Key, g => g.Sum(d => d.Total));
            var bars = new List<DailyBar>();
            for (var day = first; day <= last; day = day.AddDays(1))
                bars.Add(new DailyBar(day, byDay.TryGetValue(day, out var value) ? value : 0m));

            return bars;
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
