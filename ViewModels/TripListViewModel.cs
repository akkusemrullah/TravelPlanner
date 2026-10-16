using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    public partial class TripListViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly ITripService _tripService;
        private readonly IExchangeRateService _exchangeRates;
        private readonly IConnectivity? _connectivity;

        /// <summary>Every trip from the database - the source the search/filter/sort projects from (TP-406).</summary>
        private List<Trip> _allTrips = new();

        // False until the first load has returned - nothing (in particular the "no trips yet" card) is shown
        // before that, even though setting the default sort in the constructor already runs ApplyFilters.
        private bool _loaded;

        /// <summary>The trips currently shown (after search, status filter and sort).</summary>
        public ObservableCollection<Trip> Trips { get; } = new RangeObservableCollection<Trip>();

        /// <summary>"Seçerek sil" mode of the list (works on the trips shown - i.e. within the search/filter).</summary>
        public ListSelection<Trip> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>True when at least one trip matches the current search/filter (also shows the delete button).</summary>
        [ObservableProperty]
        private bool hasTrips;

        /// <summary>True when there are no trips at all (first-run empty state).</summary>
        [ObservableProperty]
        private bool hasNoTrips; // stays false until the first load has actually found no trips

        /// <summary>True when trips exist but none matches the search/filter.</summary>
        [ObservableProperty]
        private bool hasNoResults;

        /// <summary>The search/filter/sort bar is only shown once there is something to search.</summary>
        [ObservableProperty]
        private bool showFilterBar;

        [ObservableProperty]
        private string searchText = string.Empty;

        [ObservableProperty]
        private SettingsOption<TripSortOption>? selectedSort;

        public IReadOnlyList<TripStatusFilterOption> StatusFilters { get; } = new[]
        {
            new TripStatusFilterOption(null, AppResources.TripList_FilterAll) { IsSelected = true },
            new TripStatusFilterOption(TripStatus.Upcoming, AppResources.Status_Upcoming),
            new TripStatusFilterOption(TripStatus.Ongoing, AppResources.Status_Ongoing),
            new TripStatusFilterOption(TripStatus.Completed, AppResources.Status_Completed),
        };

        public IReadOnlyList<SettingsOption<TripSortOption>> SortOptions { get; } = new[]
        {
            new SettingsOption<TripSortOption>(TripSortOption.DateAscending, AppResources.TripList_SortDateAsc),
            new SettingsOption<TripSortOption>(TripSortOption.DateDescending, AppResources.TripList_SortDateDesc),
            new SettingsOption<TripSortOption>(TripSortOption.BudgetDescending, AppResources.TripList_SortBudgetDesc),
            new SettingsOption<TripSortOption>(TripSortOption.BudgetAscending, AppResources.TripList_SortBudgetAsc),
            new SettingsOption<TripSortOption>(TripSortOption.NameAscending, AppResources.TripList_SortName),
        };

        /// <param name="connectivity">G6: a shared trip is deleted only online (optional so older call sites keep working).</param>
        public TripListViewModel(ITripService tripService, IExchangeRateService exchangeRates, IDialogService dialogs, IConnectivity? connectivity = null)
            : base(dialogs)
        {
            _tripService = tripService;
            _exchangeRates = exchangeRates;
            _connectivity = connectivity;
            Selection = new ListSelection<Trip>(() => Trips);
            SelectedSort = SortOptions[0];
        }

        private TripStatus? SelectedStatus => StatusFilters.FirstOrDefault(f => f.IsSelected)?.Status;

        partial void OnSearchTextChanged(string value) => ApplyFilters();

        partial void OnSelectedSortChanged(SettingsOption<TripSortOption>? value) => ApplyFilters();

        [RelayCommand]
        private void SelectStatusFilter(TripStatusFilterOption? option)
        {
            if (option is null)
                return;

            foreach (var filter in StatusFilters)
                filter.IsSelected = ReferenceEquals(filter, option);

            ApplyFilters();
        }

        [RelayCommand]
        private void ClearFilters()
        {
            SearchText = string.Empty; // triggers ApplyFilters
            SelectStatusFilter(StatusFilters[0]);
        }

        [RelayCommand]
        private Task LoadTripsAsync() => RunLoadAsync(async () =>
        {
            _allTrips = await _tripService.GetAllAsync();
            _loaded = true;
            ApplyFilters();
        }, AppResources.TripList_LoadError);

        private void ApplyFilters()
        {
            if (!_loaded)
                return;

            var visible = TripListFilter.Apply(
                _allTrips,
                SearchText,
                SelectedStatus,
                SelectedSort?.Value ?? TripSortOption.DateAscending,
                BudgetInDefaultCurrency);

            Trips.ReplaceAll(visible);

            UpdateEmptyState();
            Selection.Sync();
        }

        // Trips can have different currencies (TP-403): compare budgets in the default currency using the last
        // known rate. Without a rate the raw amount is used - still a sensible order within one currency.
        private decimal BudgetInDefaultCurrency(Trip trip) =>
            _exchangeRates.TryGetCachedRate(trip.EffectiveCurrencyCode, MoneyFormatter.CurrencyCode, out var rate)
                ? trip.PlannedBudget * rate
                : trip.PlannedBudget;

        private void UpdateEmptyState()
        {
            HasTrips = Trips.Count > 0;
            HasNoTrips = _allTrips.Count == 0;
            HasNoResults = _allTrips.Count > 0 && Trips.Count == 0;
            ShowFilterBar = _allTrips.Count > 0;
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        [RelayCommand]
        private async Task GoToTripDetailAsync(Trip trip)
        {
            if (trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripDetail}?TripId={trip.Id}");
        }

        [RelayCommand]
        private async Task GoToAddTripAsync()
        {
            await Shell.Current.GoToAsync(Routes.TripForm);
        }

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on a trip: opens it, or ticks it in selection mode.</summary>
        [RelayCommand]
        private async Task TripTappedAsync(Trip? trip)
        {
            if (trip is null) return;

            if (Selection.IsActive)
                Selection.Toggle(trip);
            else
                await GoToTripDetailAsync(trip);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.TripDetail_DeleteError);

        /// <summary>
        /// Same rules as deleting one trip (G6): trips shared with the user and - offline - trips in the cloud are
        /// left alone; the confirmation says how many. One trip gets the usual message with its to-dos/expenses counted.
        /// </summary>
        private async Task DeleteManyAsync(IReadOnlyList<Trip> trips, string title)
        {
            if (trips.Count == 0)
                return;

            var allowed = new List<Trip>();
            var notOwner = 0;
            var offline = 0;
            foreach (var trip in trips)
            {
                switch (await TripDeleteConfirmation.GetBlockAsync(trip, _connectivity))
                {
                    case TripDeleteBlock.NotOwner: notOwner++; break;
                    case TripDeleteBlock.Offline: offline++; break;
                    default: allowed.Add(trip); break;
                }
            }

            if (allowed.Count == 0)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, notOwner > 0 ? AppResources.Access_OwnerOnly : AppResources.Sharing_ErrorOffline);
                return;
            }

            bool confirmed;
            if (allowed.Count == 1 && notOwner + offline == 0)
            {
                confirmed = await TripDeleteConfirmation.AskAsync(Dialogs, _tripService, allowed[0]);
            }
            else
            {
                var message = string.Format(AppResources.Selection_DeleteMessageFormat, string.Format(AppResources.Selection_TripsFormat, allowed.Count))
                              + " " + AppResources.Selection_TripsRelatedNote;
                if (notOwner > 0)
                    message += "\n\n" + string.Format(AppResources.Selection_TripsNotOwnerFormat, notOwner);
                if (offline > 0)
                    message += "\n\n" + string.Format(AppResources.Selection_TripsOfflineFormat, offline);
                confirmed = await Dialogs.ConfirmAsync(title, message);
            }

            if (!confirmed)
                return;

            try
            {
                await DeleteEachAsync(allowed, t => _tripService.DeleteAsync(t), t =>
                {
                    _allTrips.Remove(t);
                    Trips.Remove(t);
                });
            }
            finally
            {
                Selection.Cancel();
                UpdateEmptyState();
            }
        }
    }
}
