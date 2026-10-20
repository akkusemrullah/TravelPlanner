using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Backs DashboardPage - the app's home screen, implementing the "Travel Planner Ana Sayfa" design
    /// from the Frame Design System handoff (see Views/DashboardPage.xaml for the layout notes). Surfaces
    /// the single most relevant trip (ongoing, else nearest upcoming) as a featured card, quick stats
    /// across every trip, and a short preview list of recent/upcoming trips.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        private readonly ITripService _tripService;
        private readonly IExpenseService _expenseService;
        private readonly ISettingsService _settings;
        private readonly IExchangeRateService _exchangeRates;
        private readonly IAppConfigService? _appConfig;
        private readonly IAppInfo? _appInfo;
        private readonly IPreferences? _preferences;
        private readonly ITripCoverService? _covers;

        internal const string DismissedNoticeKey = "dashboard.dismissed_notice";

        /// <summary>The notice asks to update the app (tapping it opens the store; it can't be closed).</summary>
        private bool _isUpdateNotice;

        // The Trip backing the featured card - kept alongside the display-only properties below so the
        // Hava/Harita/Detaylar commands have an Id to navigate with without re-deriving "which trip".
        private Trip? _featuredTrip;

        /// <summary>"👋 Merhaba {name}" - the name comes from Settings (TP-305) instead of a hard-coded constant.</summary>
        [ObservableProperty]
        private string greetingText = string.Empty;

        [ObservableProperty]
        private string todayDisplay = string.Empty;

        [ObservableProperty]
        private bool hasNoTrips;

        // Kept alongside HasNoTrips (rather than a XAML inverter converter) purely so the stats-row and
        // "Seyahatlerim" list's IsVisible bindings stay simple, direct property bindings.
        [ObservableProperty]
        private bool hasAnyTrips;

        [ObservableProperty]
        private bool hasFeaturedTrip;

        [ObservableProperty]
        private string featuredBadgeText = string.Empty;

        [ObservableProperty]
        private string featuredName = string.Empty;

        [ObservableProperty]
        private string featuredPlace = string.Empty;

        [ObservableProperty]
        private string featuredDates = string.Empty;

        [ObservableProperty]
        private string featuredBudget = string.Empty;

        /// <summary>Boarding-pass style featured trip card (TripTicket).</summary>
        [ObservableProperty]
        private TripTicket? featuredTicket;

        [ObservableProperty]
        private string featuredDaysLabel = string.Empty;

        [ObservableProperty]
        private string featuredDaysValue = string.Empty;

        [ObservableProperty]
        private int totalTripsCount;

        [ObservableProperty]
        private int visitedCountriesCount;

        [ObservableProperty]
        private int visitedCitiesCount;

        [ObservableProperty]
        private string totalExpensesFormatted = MoneyFormatter.Format(0m, 0);

        /// <summary>The admin panel's notice (or "a new version is out") at the top of the page.</summary>
        [ObservableProperty]
        private string noticeText = string.Empty;

        [ObservableProperty]
        private bool hasNotice;

        [ObservableProperty]
        private bool canCloseNotice;

        public ObservableCollection<TripSummaryItem> RecentTrips { get; } = new RangeObservableCollection<TripSummaryItem>();

        public DashboardViewModel(ITripService tripService, IExpenseService expenseService, ISettingsService settings, IExchangeRateService exchangeRates, IDialogService dialogs,
                                  IAppConfigService? appConfig = null, IAppInfo? appInfo = null, IPreferences? preferences = null,
                                  ITripCoverService? covers = null)
            : base(dialogs)
        {
            _tripService = tripService;
            _expenseService = expenseService;
            _settings = settings;
            _exchangeRates = exchangeRates;
            _appConfig = appConfig;
            _appInfo = appInfo;
            _preferences = preferences;
            _covers = covers;

            RefreshHeader();
        }

        /// <summary>Greeting + "Bugün 15 Ağustos" line, in the app language. Re-run on every load so a
        /// name changed in Settings shows up as soon as the user comes back here.</summary>
        private void RefreshHeader()
        {
            // "Ad Soyad" is stored; the greeting only uses the first name ("Merhaba Emrullah").
            var name = PersonName.FirstName(_settings.UserName);
            GreetingText = string.IsNullOrWhiteSpace(name)
                ? AppResources.Dashboard_GreetingNoName
                : string.Format(AppResources.Dashboard_Greeting, name);

            TodayDisplay = string.Format(AppResources.Dashboard_TodayFormat, DateTime.Today.ToString("d MMMM", CultureInfo.CurrentCulture));
        }

        // Called from DashboardPage.OnAppearing so the numbers are fresh every time the user lands back
        // here (e.g. after adding a trip or logging an expense elsewhere).
        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            RefreshHeader();
            if (_appConfig is not null)
            {
                ShowNotice(_appConfig.Current);
                RefreshNoticeAsync().FireAndForget();
            }

            var trips = await _tripService.GetAllAsync();
            TotalTripsCount = trips.Count;
            HasNoTrips = trips.Count == 0;
            HasAnyTrips = !HasNoTrips;

            // "Gezilen ülke" = countries from trips that have actually happened, not ones merely
            // planned - so this only counts trips whose end date has already passed.
            VisitedCountriesCount = trips
                .Where(t => t.EndDate.Date < DateTime.Today && !string.IsNullOrWhiteSpace(t.Country))
                .Select(t => t.Country.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            VisitedCitiesCount = CountVisitedCities(trips, DateTime.Today);

            // TP-207: one SUM query (grouped by trip currency since TP-403) instead of one query per trip.
            var totalsByCurrency = await _expenseService.GetTotalExpensesByCurrencyAsync();
            TotalExpensesFormatted = await FormatCombinedTotalAsync(totalsByCurrency);

            // Featured card: an ongoing trip wins over the nearest upcoming one - "what's happening
            // right now" beats "what's next" when both exist.
            _featuredTrip = trips.FirstOrDefault(t => t.IsOngoing)
                ?? trips.Where(t => t.IsUpcoming).OrderBy(t => t.StartDate).FirstOrDefault();

            HasFeaturedTrip = _featuredTrip is not null;
            if (_featuredTrip is not null)
                ApplyFeaturedTrip(_featuredTrip);

            var recent = trips
                .OrderBy(t => t.IsOngoing ? 0 : t.IsUpcoming ? 1 : 2)
                .ThenBy(t => t.IsOngoing || t.IsUpcoming ? t.StartDate : DateTime.MaxValue)
                .ThenByDescending(t => t.StartDate)
                .Take(5)
                .ToList();
            _covers?.ShowThumbnails(recent); // cover thumbnails, as on the trip list
            RecentTrips.ReplaceAll(recent.Select(trip => new TripSummaryItem(trip)));
        }, AppResources.Dashboard_LoadError);

        /// <summary>
        /// Trips can have different currencies (TP-403): convert every currency's total into the default one
        /// with the free Frankfurter rate. If a rate is unavailable (offline, never fetched), the totals are shown
        /// side by side instead ("₺12.500 + €340") rather than adding up amounts of different currencies.
        /// </summary>
        /// <summary>
        /// "Gezilen şehir" - like the countries: cities of trips that are over. The same city name in two countries
        /// (Paris, France / Paris, Texas) counts twice; case and spaces don't matter.
        /// </summary>
        internal static int CountVisitedCities(IEnumerable<Trip> trips, DateTime today) => trips
            .Where(t => t.EndDate.Date < today.Date && !string.IsNullOrWhiteSpace(t.City))
            .Select(t => (t.Country ?? string.Empty).Trim().ToUpperInvariant() + "|" + t.City.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();

        private async Task<string> FormatCombinedTotalAsync(Dictionary<string, decimal> totals)
        {
            var target = MoneyFormatter.CurrencyCode;
            if (totals.Count == 0)
                return MoneyFormatter.Format(0m, 0);

            var sum = 0m;
            var unconverted = new List<string>();

            foreach (var (code, amount) in totals)
            {
                var quote = await _exchangeRates.GetRateAsync(code, target);
                if (quote is null)
                    unconverted.Add(MoneyFormatter.FormatIn(amount, code, 0));
                else
                    sum += quote.Convert(amount);
            }

            if (unconverted.Count == 0)
                return MoneyFormatter.Format(sum, 0);

            if (sum > 0)
                unconverted.Insert(0, MoneyFormatter.Format(sum, 0));

            return string.Join(" + ", unconverted);
        }

        private void ApplyFeaturedTrip(Trip trip)
        {
            FeaturedBadgeText = trip.IsOngoing ? AppResources.Dashboard_BadgeOngoing : AppResources.Dashboard_BadgeUpcoming;
            FeaturedName = trip.Name;
            FeaturedPlace = string.IsNullOrWhiteSpace(trip.Country) ? trip.City : $"{trip.City}, {trip.Country}";
            FeaturedDates = trip.CompactDateRangeDisplay;
            FeaturedBudget = trip.FormattedBudget;
            FeaturedTicket = new TripTicket(trip);

            if (trip.IsOngoing)
            {
                // Mid-trip, "kalan süre" (time left until the trip starts) no longer applies - show how
                // many days remain until it ends instead.
                FeaturedDaysLabel = AppResources.Dashboard_EndsIn;
                FeaturedDaysValue = string.Format(AppResources.Common_DaysFormat, Math.Max((trip.EndDate.Date - DateTime.Today).Days, 0));
            }
            else
            {
                FeaturedDaysLabel = AppResources.Dashboard_StartsIn;
                FeaturedDaysValue = string.Format(AppResources.Common_DaysFormat, (trip.StartDate.Date - DateTime.Today).Days);
            }
        }

        private async Task RefreshNoticeAsync() => ShowNotice(await _appConfig!.RefreshAsync());

        internal void ShowNotice(AppConfig config)
        {
            _isUpdateNotice = config.NeedsUpdate(_appInfo?.VersionString);
            var text = _isUpdateNotice ? AppResources.Dashboard_UpdateNotice : config.Announcement;
            if (!_isUpdateNotice && text is not null && _preferences?.Get(DismissedNoticeKey, string.Empty) == text)
                text = null; // closed once: stays closed until the admin writes another notice
            NoticeText = text ?? string.Empty;
            HasNotice = text is not null;
            CanCloseNotice = HasNotice && !_isUpdateNotice;
        }

        [RelayCommand]
        private void CloseNotice()
        {
            if (!CanCloseNotice) return;
            _preferences?.Set(DismissedNoticeKey, NoticeText);
            HasNotice = false;
        }

        [RelayCommand]
        private Task OpenNoticeAsync() => RunSafeAsync(async () =>
        {
            if (!_isUpdateNotice || _appInfo is null) return;
            await Launcher.Default.TryOpenAsync(new Uri($"https://play.google.com/store/apps/details?id={_appInfo.PackageName}"));
        });

        [RelayCommand]
        private async Task GoToAddTripAsync() => await Shell.Current.GoToAsync(Routes.TripForm);

        [RelayCommand]
        private async Task GoToTripListAsync() => await Shell.Current.GoToAsync($"//{Routes.TripList}"); // tab (absolute route)

        [RelayCommand]
        private async Task GoToTripDetailAsync(TripSummaryItem? item)
        {
            if (item is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={item.Id}");
        }

        // ---- Featured card actions ----
        // Per design decision: these navigate straight to the existing Weather/Map/TripDetail pages
        // rather than reproducing the design's bottom-sheet quick-preview as a new modal control.

        [RelayCommand]
        private async Task OpenFeaturedWeatherAsync()
        {
            if (_featuredTrip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Weather}?TripId={_featuredTrip.Id}");
        }

        [RelayCommand]
        private async Task OpenFeaturedMapAsync()
        {
            if (_featuredTrip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Map}?TripId={_featuredTrip.Id}");
        }

        [RelayCommand]
        private async Task OpenFeaturedDetailsAsync()
        {
            if (_featuredTrip is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={_featuredTrip.Id}");
        }
    }
}
