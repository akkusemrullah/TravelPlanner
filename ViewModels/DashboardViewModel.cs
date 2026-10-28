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
    /// Backs DashboardPage - the app's home screen (TP-106). With a coming or ongoing trip: a hero with the countdown
    /// ("Roma'ya 12 gün kaldı"), the trip's boarding-pass card with quick links, the trip calendar and the travel
    /// record (trips / countries / cities). Without one: the hero asks where to go next (city box → new trip form),
    /// then ready plans from Keşfet and what the app can do (the "3 adımda yola hazır" panel was dropped).
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        /// <summary>How many trips "Seyahat takvimin" lists.</summary>
        public const int CalendarRows = 3;

        /// <summary>How many Keşfet plans the empty Home shows (one large, two small).</summary>
        public const int PlanCards = 3;

        private readonly ITripService _tripService;
        private readonly ISettingsService _settings;
        private readonly IAppConfigService? _appConfig;
        private readonly IAppInfo? _appInfo;
        private readonly IPreferences? _preferences;
        private readonly IJoinTripFlow? _joinFlow;
        private readonly Services.Sync.ISyncService? _sync;
        private readonly IAccountService? _account;
        private readonly ITodoService? _todos;
        private readonly IActivityService? _activities;
        private readonly IExploreService? _explore;
        private readonly IGeocodingService? _geocoding;

        // Bumped on every keystroke in the city box, so a slow search can't show results for older text.
        private int _citySearchGeneration;
        private bool _isClearingCity;

        internal const string DismissedNoticeKey = "dashboard.dismissed_notice";

        /// <summary>TP-91: the guest closed the "sign in to back up" box.</summary>
        internal const string SignInHintClosedKey = "dashboard.signin_hint_closed";

        /// <summary>The notice asks to update the app (tapping it opens the store; it can't be closed).</summary>
        private bool _isUpdateNotice;

        // The trip behind the card - the card's button and quick links need its Id.
        private Trip? _featuredTrip;

        /// <summary>"Merhaba Yusuf," above the countdown - the first name from Settings.</summary>
        [ObservableProperty]
        private string greetingText = string.Empty;

        /// <summary>"Y" in the avatar (signed in with a name); empty shows the person icon.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasAvatarText), nameof(ShowsAvatarIcon))]
        private string avatarText = string.Empty;

        public bool HasAvatarText => AvatarText.Length > 0;

        public bool ShowsAvatarIcon => !HasAvatarText;

        // ---- Hero title: "Roma'ya " + "12 gün" (light blue) + " kaldı" ----

        [ObservableProperty]
        private string heroBefore = AppResources.Home_EmptyTitle;

        [ObservableProperty]
        private string heroHighlight = string.Empty;

        [ObservableProperty]
        private string heroAfter = string.Empty;

        /// <summary>The city typed into the hero's box (no coming trip).</summary>
        [ObservableProperty]
        private string cityQuery = string.Empty;

        /// <summary>Cities matching <see cref="CityQuery"/>; a trip starts only from one of these.</summary>
        public ObservableCollection<CitySuggestion> CitySuggestions { get; } = new RangeObservableCollection<CitySuggestion>();

        [ObservableProperty]
        private bool isCitySuggestionsVisible;

        /// <summary>"Listeden bir şehir seç." - the arrow was tapped on typed text instead of a suggestion.</summary>
        [ObservableProperty]
        private bool showsCityHint;

        [ObservableProperty]
        private bool hasNoTrips;

        // Kept alongside HasNoTrips (rather than an inverting converter) so the IsVisible bindings stay direct.
        [ObservableProperty]
        private bool hasAnyTrips;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowsCitySearch))]
        private bool hasFeaturedTrip;

        /// <summary>No coming trip: the hero asks where to go and shows the city box.</summary>
        public bool ShowsCitySearch => !HasFeaturedTrip;

        /// <summary>Boarding-pass card of the featured trip.</summary>
        [ObservableProperty]
        private TripTicket? featuredTicket;

        [ObservableProperty]
        private int totalTripsCount;

        [ObservableProperty]
        private int visitedCountriesCount;

        [ObservableProperty]
        private int visitedCitiesCount;

        /// <summary>The admin panel's notice (or "a new version is out") at the top of the page.</summary>
        [ObservableProperty]
        private string noticeText = string.Empty;

        [ObservableProperty]
        private bool hasNotice;

        [ObservableProperty]
        private bool canCloseNotice;

        /// <summary>Keşfet plans for the empty Home: the large card and the two small ones under it.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPlans))]
        private HomePlanCard? firstPlan;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMorePlans))]
        private HomePlanCard? secondPlan;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMorePlans))]
        private HomePlanCard? thirdPlan;

        public bool HasPlans => FirstPlan is not null;

        public bool HasMorePlans => SecondPlan is not null;

        /// <summary>Quick links under the card ("Plan 5/7", "Görevler 9/14", "Harita"...).</summary>
        public ObservableCollection<HomeChip> Chips { get; } = new RangeObservableCollection<HomeChip>();

        /// <summary>"Seyahat takvimin": the coming trips first, then the latest past ones.</summary>
        public ObservableCollection<HomeTripRow> CalendarTrips { get; } = new RangeObservableCollection<HomeTripRow>();

        public DashboardViewModel(ITripService tripService, ISettingsService settings, IDialogService dialogs,
                                  IAppConfigService? appConfig = null, IAppInfo? appInfo = null, IPreferences? preferences = null,
                                  IJoinTripFlow? joinFlow = null, Services.Sync.ISyncService? sync = null, IAccountService? account = null,
                                  ITodoService? todos = null, IActivityService? activities = null, IExploreService? explore = null,
                                  IGeocodingService? geocoding = null)
            : base(dialogs)
        {
            _sync = sync;
            _account = account;
            _joinFlow = joinFlow;
            _tripService = tripService;
            _settings = settings;
            _appConfig = appConfig;
            _appInfo = appInfo;
            _preferences = preferences;
            _todos = todos;
            _activities = activities;
            _explore = explore;
            _geocoding = geocoding;

            RefreshHeader();
        }

        // ---- TP-91: the app opens without the sign-in page; a guest sees a small box on Home instead ----

        /// <summary>Signed out and the box wasn't closed. Signing in rebuilds the shell, so this is read once.</summary>
        public bool ShowSignInHint => _account is { IsSignedIn: false } && _preferences?.Get(SignInHintClosedKey, false) != true;

        [RelayCommand]
        private Task OpenSignInAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Auth));

        [RelayCommand]
        private void CloseSignInHint()
        {
            _preferences?.Set(SignInHintClosedKey, true);
            OnPropertyChanged(nameof(ShowSignInHint));
        }

        // ---- TP-78: small cloud badge in the header while offline or changes wait to be sent ----

        /// <summary>Shown while signed in and offline, failing, or with changes waiting to be sent.</summary>
        public bool ShowSyncBadge => _sync is { } sync &&
            (sync.PendingCount > 0 || sync.Status is Services.Sync.SyncStatus.Offline or Services.Sync.SyncStatus.Error);

        /// <summary>How many changes wait ("3"), empty when none.</summary>
        public string SyncBadgeText => _sync is { PendingCount: > 0 } sync
            ? sync.PendingCount.ToString(CultureInfo.CurrentCulture)
            : string.Empty;

        /// <summary>DashboardPage calls this in OnAppearing / OnDisappearing so the badge stays live.</summary>
        public void AttachSyncStatus(bool attach)
        {
            if (_sync is null)
                return;
            _sync.StatusChanged -= OnSyncStatusChanged;
            if (attach)
            {
                _sync.StatusChanged += OnSyncStatusChanged;
                RaiseSyncBadge();
            }
        }

        private void OnSyncStatusChanged(object? sender, EventArgs e) => MainThread.BeginInvokeOnMainThread(RaiseSyncBadge);

        internal void RaiseSyncBadge()
        {
            OnPropertyChanged(nameof(ShowSyncBadge));
            OnPropertyChanged(nameof(SyncBadgeText));
        }

        [RelayCommand]
        private Task ShowSyncStatusAsync() => _sync is null
            ? Task.CompletedTask
            : Dialogs.ShowMessageAsync(AppResources.Common_Info, SettingsViewModel.DescribeSync(_sync));

        /// <summary>Greeting and avatar letter. Re-run on every load so a name changed in Settings shows up here at once.</summary>
        private void RefreshHeader()
        {
            // "Ad Soyad" is stored; the greeting only uses the first name ("Merhaba Emrullah,").
            var name = PersonName.FirstName(_settings.UserName);
            GreetingText = string.IsNullOrWhiteSpace(name)
                ? AppResources.Home_GreetingNoName
                : string.Format(AppResources.Home_GreetingFormat, name);

            // The same initials as the Profil avatar: first letters of the first and the last name ("EA").
            AvatarText = _account is { IsSignedIn: false } ? string.Empty : PersonName.Initials(_settings.UserName);
        }

        // Called from DashboardPage.OnAppearing so the numbers are fresh every time the user lands back
        // here (e.g. after adding a trip elsewhere).
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
            var today = DateTime.Today;
            TotalTripsCount = trips.Count;
            HasNoTrips = trips.Count == 0;
            HasAnyTrips = !HasNoTrips;

            // "Gezilen ülke" = countries of trips that have started (TP-59: an ongoing trip counts too),
            // not ones merely planned.
            VisitedCountriesCount = CountVisitedCountries(trips, today);
            VisitedCitiesCount = CountVisitedCities(trips, today);

            _featuredTrip = PickFeatured(trips);
            HasFeaturedTrip = _featuredTrip is not null;
            FeaturedTicket = _featuredTrip is null ? null : new TripTicket(_featuredTrip);
            (HeroBefore, HeroHighlight, HeroAfter) = HeroTitle(_featuredTrip, today);

            CalendarTrips.ReplaceAll(CalendarOrder(trips).Take(CalendarRows)
                .Select(t => new HomeTripRow(t, isNext: t == _featuredTrip, today)));

            Chips.ReplaceAll(_featuredTrip is null ? Array.Empty<HomeChip>() : await BuildChipsAsync(_featuredTrip));

            if (HasNoTrips)
                await LoadPlansAsync();
        }, AppResources.Dashboard_LoadError);

        /// <summary>The card's trip: an ongoing one wins over the nearest coming one; none when every trip is over.</summary>
        internal static Trip? PickFeatured(IEnumerable<Trip> trips)
        {
            var list = trips as IReadOnlyCollection<Trip> ?? trips.ToList();
            return list.FirstOrDefault(t => t.IsOngoing) ?? list.Where(t => t.IsUpcoming).OrderBy(t => t.StartDate).FirstOrDefault();
        }

        /// <summary>Ongoing and coming trips by date, then past ones newest first.</summary>
        internal static IEnumerable<Trip> CalendarOrder(IEnumerable<Trip> trips) => trips
            .OrderBy(t => t.IsOngoing ? 0 : t.IsUpcoming ? 1 : 2)
            .ThenBy(t => t.IsOngoing || t.IsUpcoming ? t.StartDate : DateTime.MaxValue)
            .ThenByDescending(t => t.StartDate);

        /// <summary>
        /// The hero's big line, cut around the highlighted part: "Roma'ya " · "12 gün" · " kaldı", "Roma'da " · "3. gün";
        /// without a trip "Bir sonraki durağın neresi?".
        /// </summary>
        internal static (string Before, string Highlight, string After) HeroTitle(Trip? trip, DateTime today)
        {
            if (trip is null)
                return (AppResources.Home_EmptyTitle, string.Empty, string.Empty);

            var city = string.IsNullOrWhiteSpace(trip.City) ? trip.Name.Trim() : trip.City.Trim();
            var start = trip.StartDate.Date;
            var culture = CultureInfo.CurrentCulture;
            return today.Date < start
                ? Cut(AppResources.Home_CountdownFormat, PlaceSuffix.Dative(city),
                      string.Format(culture, AppResources.Explore_DaysFormat, (start - today.Date).Days))
                : Cut(AppResources.Home_OngoingFormat, PlaceSuffix.Locative(city),
                      string.Format(culture, AppResources.Home_DayNumberFormat, (today.Date - start).Days + 1));
        }

        private static (string, string, string) Cut(string format, string place, string highlight)
        {
            const string marker = "\u0001";
            var text = string.Format(CultureInfo.CurrentCulture, format, place, marker);
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            return at < 0 ? (text, string.Empty, string.Empty) : (text[..at], highlight, text[(at + marker.Length)..]);
        }

        /// <summary>"Plan 5/7" (days with something planned / days), "Görevler 9/14", then map, weather and expenses.</summary>
        private async Task<IReadOnlyList<HomeChip>> BuildChipsAsync(Trip trip)
        {
            var days = (trip.EndDate.Date - trip.StartDate.Date).Days + 1;
            var plan = string.Empty;
            var tasks = string.Empty;
            if (_activities is not null)
            {
                var planned = (await _activities.GetByTripIdAsync(trip.Id)).Select(a => a.Date.Date).Distinct().Count();
                plan = $"{Math.Min(planned, days)}/{days}";
            }
            if (_todos is not null && await _todos.GetByTripIdAsync(trip.Id) is { Count: > 0 } todos)
                tasks = $"{todos.Count(t => t.IsCompleted)}/{todos.Count}";

            return new[]
            {
                new HomeChip("Ic.CalendarDays", AppResources.Home_ChipPlan, plan, Routes.Itinerary),
                new HomeChip("Ic.ListChecks", AppResources.Home_ChipTasks, tasks, Routes.TodoList),
                new HomeChip("Ic.Map", AppResources.Dashboard_MapShort, string.Empty, Routes.Map),
                new HomeChip("Ic.CloudSun", AppResources.Dashboard_WeatherShort, string.Empty, Routes.Weather),
                new HomeChip("Ic.Wallet", AppResources.Nav_Expenses, string.Empty, Routes.ExpenseList),
            };
        }

        /// <summary>Keşfet's first plans (featured first) - the cached page at once, then the fresh one. Offline: none.</summary>
        private async Task LoadPlansAsync()
        {
            if (_explore is null)
                return;
            var filter = new ExploreFilter();
            if (_explore.GetCachedFirstPage(filter) is { } cached)
                ShowPlans(cached.Items);
            try
            {
                ShowPlans((await _explore.ListAsync(filter, null)).Items);
            }
            catch (Exception ex)
            {
                // only a suggestion: Home works without it
                System.Diagnostics.Debug.WriteLine($"[Dashboard] Explore: {ex.Message}");
            }
        }

        internal void ShowPlans(IEnumerable<ExploreTripCard> cards)
        {
            var picked = cards.OrderByDescending(c => c.IsFeatured).Take(PlanCards).Select(c => new HomePlanCard(c)).ToList();
            FirstPlan = picked.ElementAtOrDefault(0);
            SecondPlan = picked.ElementAtOrDefault(1);
            ThirdPlan = picked.ElementAtOrDefault(2);
        }

        /// <summary>"Gezilen ülke": countries of trips that have started; "Türkiye" and "Türkiye Cumhuriyeti" are one country.</summary>
        internal static int CountVisitedCountries(IEnumerable<Trip> trips, DateTime today) => trips
            .Where(t => t.StartDate.Date <= today.Date && !string.IsNullOrWhiteSpace(t.Country))
            .Select(t => CountryNames.Key(t.Country))
            .Distinct(StringComparer.Ordinal)
            .Count();

        /// <summary>
        /// "Gezilen şehir" - like the countries: cities of trips that have started. The same city name in two countries
        /// (Paris, France / Paris, Texas) counts twice; case and spaces don't matter.
        /// </summary>
        internal static int CountVisitedCities(IEnumerable<Trip> trips, DateTime today) => trips
            .Where(t => t.StartDate.Date <= today.Date && !string.IsNullOrWhiteSpace(t.City))
            .Select(t => CountryNames.Key(t.Country) + "|" + t.City.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Count();

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

        /// <summary>TP-48: "Seyahate katıl" next to "Seyahat oluştur"; opens the joined trip.</summary>
        [RelayCommand]
        private Task JoinTripAsync() => RunSafeAsync(async () =>
        {
            if (_joinFlow is null) return;
            if (await _joinFlow.RunAsync() is int tripId)
                await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={tripId}");
        }, AppResources.Members_ActionError);

        [RelayCommand]
        private void GoToTripList() => TabNavigation.Select(Routes.TripList);

        [RelayCommand]
        private void OpenProfile() => TabNavigation.Select(Routes.Settings);

        [RelayCommand]
        private void OpenExplore() => TabNavigation.Select(Routes.Explore);

        [RelayCommand]
        private async Task GoToTripDetailAsync(HomeTripRow? row)
        {
            if (row is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={row.Id}");
        }

        partial void OnCityQueryChanged(string value)
        {
            ShowsCityHint = false;
            var generation = ++_citySearchGeneration;
            if (_isClearingCity || _geocoding is null || string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
            {
                CitySuggestions.Clear();
                IsCitySuggestionsVisible = false;
                return;
            }

            SearchCitiesDebouncedAsync(value.Trim(), generation).FireAndForget();
        }

        private async Task SearchCitiesDebouncedAsync(string query, int generation)
        {
            await Task.Delay(350); // debounce - wait for a pause in typing
            if (generation != _citySearchGeneration || _geocoding is null)
                return;

            var results = await _geocoding.SearchCitiesAsync(query);
            if (generation != _citySearchGeneration)
                return;

            CitySuggestions.ReplaceAll(results);
            IsCitySuggestionsVisible = CitySuggestions.Count > 0;
        }

        /// <summary>
        /// The hero's arrow / Go key. A trip starts only from a suggestion: with exactly one suggestion that one is
        /// taken, an empty box opens the blank form, anything else asks to pick from the list.
        /// </summary>
        [RelayCommand]
        private Task SearchCityAsync()
        {
            if (string.IsNullOrWhiteSpace(CityQuery))
                return Shell.Current.GoToAsync(Routes.TripForm);
            if (CitySuggestions.Count == 1)
                return SelectCityAsync(CitySuggestions[0]);

            ShowsCityHint = true;
            return Task.CompletedTask;
        }

        /// <summary>A suggestion was tapped: the new trip form with that city and its country filled in.</summary>
        [RelayCommand]
        private Task SelectCityAsync(CitySuggestion? city)
        {
            if (city is null)
                return Task.CompletedTask;

            var route = TripFormRoute(city);
            _isClearingCity = true;
            CityQuery = string.Empty;
            _isClearingCity = false;
            return Shell.Current.GoToAsync(route);
        }

        internal static string TripFormRoute(CitySuggestion city) =>
            $"{Routes.TripForm}?City={Uri.EscapeDataString(city.Name)}&Country={Uri.EscapeDataString(CountryNames.Tidy(city.Country))}";

        [RelayCommand]
        private Task OpenPlanAsync(HomePlanCard? plan) =>
            plan is null ? Task.CompletedTask : Shell.Current.GoToAsync($"{Routes.ExploreDetail}?Id={plan.Id}");

        [RelayCommand]
        private async Task OpenFeaturedDetailsAsync()
        {
            if (_featuredTrip is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={_featuredTrip.Id}");
        }

        [RelayCommand]
        private async Task OpenChipAsync(HomeChip? chip)
        {
            if (_featuredTrip is null || chip is null) return;
            await Shell.Current.GoToAsync($"{chip.Route}?TripId={_featuredTrip.Id}");
        }
    }
}
