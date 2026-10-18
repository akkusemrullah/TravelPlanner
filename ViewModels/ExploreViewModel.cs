using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.ViewModels
{
    /// <summary>One chip of the Keşfet filter sheet ("4-7 gün", "₺₺", "Doğa", "Tem", "En yeni"…).</summary>
    public partial class ExploreChoice : ObservableObject
    {
        public ExploreChoice(string group, string key, string label)
        {
            Group = group;
            Key = key;
            Label = label;
        }

        public string Group { get; }
        public string Key { get; }
        public string Label { get; }

        [ObservableProperty]
        private bool isSelected;
    }

    /// <summary>
    /// TP-33 "Keşfet" tab: the admin's sample trips as cards. Endless list (keyset pages of 20, one Add event per page,
    /// one "load more" at a time, no card twice, stops after the last page), search with a 400 ms pause (a newer search
    /// cancels the older request), a filter sheet whose choices are remembered on the device, and the first page shown
    /// from the cache at once (also offline) while a fresh one is fetched.
    /// </summary>
    public partial class ExploreViewModel : ViewModelBase
    {
        public const string GroupDuration = "duration";
        public const string GroupBudget = "budget";
        public const string GroupStyle = "style";
        public const string GroupMonth = "month";
        public const string GroupSort = "sort";

        internal const string FilterPreferenceKey = "explore.filter.v1";

        private readonly IExploreService _explore;
        private readonly IConnectivity _connectivity;
        private readonly IPreferences _preferences;

        private readonly HashSet<Guid> _ids = new();
        private CancellationTokenSource? _listCts;
        private CancellationTokenSource? _searchCts;
        private string? _nextCursor;
        private bool _started;
        private bool _interrupted;

        public ExploreViewModel(IExploreService explore, IConnectivity connectivity, IPreferences preferences, IDialogService dialogs)
            : base(dialogs)
        {
            _explore = explore;
            _connectivity = connectivity;
            _preferences = preferences;

            DurationChoices = new ObservableCollection<ExploreChoice>(new[]
            {
                new ExploreChoice(GroupDuration, "1-3", AppResources.Explore_Duration_Short),
                new ExploreChoice(GroupDuration, "4-7", AppResources.Explore_Duration_Mid),
                new ExploreChoice(GroupDuration, "8-90", AppResources.Explore_Duration_Long),
            });
            BudgetChoices = new ObservableCollection<ExploreChoice>(
                new[] { 1, 2, 3 }.Select(b => new ExploreChoice(GroupBudget, b.ToString(CultureInfo.InvariantCulture), new string('₺', b))));
            StyleChoices = new ObservableCollection<ExploreChoice>(
                ExploreTags.All.Select(t => new ExploreChoice(GroupStyle, t, ExploreTags.Label(t))));
            MonthChoices = new ObservableCollection<ExploreChoice>(Enumerable.Range(1, 12).Select(m => new ExploreChoice(
                GroupMonth, m.ToString(CultureInfo.InvariantCulture),
                CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m))));
            SortChoices = new ObservableCollection<ExploreChoice>(new[]
            {
                new ExploreChoice(GroupSort, ExploreSort.Recommended, AppResources.Explore_Sort_Recommended),
                new ExploreChoice(GroupSort, ExploreSort.Newest, AppResources.Explore_Sort_Newest),
                new ExploreChoice(GroupSort, ExploreSort.Shortest, AppResources.Explore_Sort_Shortest),
            });

            filter = LoadFilter();
        }

        public RangeObservableCollection<ExploreTripCard> Items { get; } = new();

        public ObservableCollection<ExploreChoice> DurationChoices { get; }
        public ObservableCollection<ExploreChoice> BudgetChoices { get; }
        public ObservableCollection<ExploreChoice> StyleChoices { get; }
        public ObservableCollection<ExploreChoice> MonthChoices { get; }
        public ObservableCollection<ExploreChoice> SortChoices { get; }

        /// <summary>Placeholder cards while the first page loads.</summary>
        public IReadOnlyList<int> SkeletonCards { get; } = new[] { 1, 2, 3, 4 };

        /// <summary>Pause after the last key stroke before searching. Shorter in tests.</summary>
        internal TimeSpan SearchDelay { get; set; } = TimeSpan.FromMilliseconds(400);

        /// <summary>What the list shows now (search, filters, sort).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ActiveFilterCount), nameof(HasActiveFilters), nameof(ActiveFilterText), nameof(CanClearFilters))]
        private ExploreFilter filter;

        [ObservableProperty]
        private string searchText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowSkeleton), nameof(HasNoResults), nameof(IsEmptyCatalog))]
        private bool isLoadingFirst;

        [ObservableProperty]
        private bool isLoadingMore;

        [ObservableProperty]
        private bool hasMore;

        /// <summary>Cards from the cache are on screen but the fresh list couldn't be fetched.</summary>
        [ObservableProperty]
        private bool showOfflineBanner;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoResults), nameof(IsEmptyCatalog))]
        private bool hasError;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoResults), nameof(IsEmptyCatalog))]
        private bool isOfflineEmpty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasNoResults), nameof(IsEmptyCatalog))]
        private bool hasLoaded;

        [ObservableProperty]
        private bool isFilterOpen;

        /// <summary>Country / city typed in the filter sheet (applied with "Uygula").</summary>
        [ObservableProperty]
        private string filterPlace = string.Empty;

        public int ActiveFilterCount => Filter.ActiveCount;
        public bool HasActiveFilters => ActiveFilterCount > 0;
        public string ActiveFilterText => ActiveFilterCount.ToString(CultureInfo.CurrentCulture);

        /// <summary>Something narrows the list: "Filtreleri temizle" is offered on an empty result.</summary>
        public bool CanClearFilters => HasActiveFilters || !string.IsNullOrWhiteSpace(Filter.Query);

        public bool ShowSkeleton => IsLoadingFirst && Items.Count == 0;

        private bool IsListEmpty => HasLoaded && !IsLoadingFirst && !HasError && !IsOfflineEmpty && Items.Count == 0;

        /// <summary>The search / filters match nothing.</summary>
        public bool HasNoResults => IsListEmpty && CanClearFilters;

        /// <summary>No sample trip is published yet.</summary>
        public bool IsEmptyCatalog => IsListEmpty && !CanClearFilters;

        private void NotifyListState()
        {
            OnPropertyChanged(nameof(ShowSkeleton));
            OnPropertyChanged(nameof(HasNoResults));
            OnPropertyChanged(nameof(IsEmptyCatalog));
        }

        // ---- loading ----

        /// <summary>Called when the tab appears: the first time (or after an interrupted load) it loads; afterwards the list stays as it was.</summary>
        public Task StartAsync()
        {
            if (_started && !_interrupted)
                return Task.CompletedTask;
            _started = true;
            _interrupted = false;
            return ReloadAsync(useCache: true);
        }

        /// <summary>Called when the tab disappears: whatever is still loading is cancelled (and reloaded next time).</summary>
        public void Pause()
        {
            _searchCts?.Cancel();
            if (_listCts is null)
                return;
            _interrupted = IsLoadingFirst || IsLoadingMore;
            _listCts.Cancel();
            _listCts = null;
            IsLoadingFirst = false;
            IsLoadingMore = false;
            IsRefreshing = false;
        }

        [RelayCommand]
        private Task RefreshAsync() => ReloadAsync(useCache: false);

        [RelayCommand]
        private Task RetryAsync() => ReloadAsync(useCache: true);

        private bool IsOnline => _connectivity.NetworkAccess == NetworkAccess.Internet;

        private async Task ReloadAsync(bool useCache)
        {
            _listCts?.Cancel();
            var cts = new CancellationTokenSource();
            _listCts = cts;
            var filter = Filter;

            HasError = false;
            IsOfflineEmpty = false;
            ShowOfflineBanner = false;
            IsLoadingMore = false; // a page of the old list never lands in the new one (LoadMoreAsync checks the token)

            if (useCache && _explore.GetCachedFirstPage(filter) is { } cached)
                ShowFirstPage(cached);
            else if (useCache)
                ShowFirstPage(new ExplorePage(Array.Empty<ExploreTripCard>(), null));

            IsLoadingFirst = true;
            NotifyListState();
            try
            {
                if (!IsOnline)
                {
                    if (Items.Count > 0)
                        ShowOfflineBanner = true;
                    else
                        IsOfflineEmpty = true;
                    return;
                }

                var page = await _explore.ListAsync(filter, null, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                ShowFirstPage(page);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                    return;
                System.Diagnostics.Debug.WriteLine($"[Explore] Loading the list failed: {ex.Message}");
                if (Items.Count > 0)
                    ShowOfflineBanner = true;
                else if (ex is SyncException { Kind: SyncErrorKind.Network })
                    IsOfflineEmpty = true;
                else
                    HasError = true;
            }
            finally
            {
                if (ReferenceEquals(_listCts, cts))
                {
                    IsLoadingFirst = false;
                    IsRefreshing = false;
                    HasLoaded = true;
                    NotifyListState();
                }
            }
        }

        private void ShowFirstPage(ExplorePage page)
        {
            _ids.Clear();
            var cards = page.Items.Where(c => _ids.Add(c.Id)).ToList();
            Items.ReplaceAll(cards);
            _nextCursor = page.NextCursor;
            HasMore = _nextCursor is not null;
            NotifyListState();
        }

        /// <summary>The list is near its end (RemainingItemsThreshold): the next page, if there is one.</summary>
        [RelayCommand]
        private async Task LoadMoreAsync()
        {
            if (IsLoadingMore || IsLoadingFirst || _nextCursor is null || _listCts is not { } cts)
                return;

            IsLoadingMore = true;
            var cursor = _nextCursor;
            try
            {
                var page = await _explore.ListAsync(Filter, cursor, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(cts, _listCts) || cursor != _nextCursor)
                    return;

                Items.AddRange(page.Items.Where(c => _ids.Add(c.Id)).ToList());
                _nextCursor = page.NextCursor;
                HasMore = _nextCursor is not null;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                // The cursor stays: scrolling to the end again tries the same page once more.
                System.Diagnostics.Debug.WriteLine($"[Explore] Loading more failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(cts, _listCts) || _listCts is null)
                    IsLoadingMore = false;
            }
        }

        // ---- search ----

        private bool _suppressSearch;

        partial void OnSearchTextChanged(string value)
        {
            _searchCts?.Cancel();
            if (_suppressSearch)
                return;
            var cts = new CancellationTokenSource();
            _searchCts = cts;
            SearchAfterPauseAsync(value, cts.Token).FireAndForget();
        }

        /// <summary>Trimmed, at most 100 characters; shorter than 2 characters is no search.</summary>
        internal static string? NormalizeQuery(string? text)
        {
            var query = (text ?? string.Empty).Trim();
            if (query.Length > ExploreFilter.MaxQueryLength)
                query = query[..ExploreFilter.MaxQueryLength].Trim();
            return query.Length < ExploreFilter.MinQueryLength ? null : query;
        }

        private async Task SearchAfterPauseAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(SearchDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var query = NormalizeQuery(text);
            if (query == NormalizeQuery(Filter.Query))
                return;
            Filter = Filter with { Query = query };
            await ReloadAsync(useCache: true);
        }

        // ---- filter sheet ----

        [RelayCommand]
        private void OpenFilter()
        {
            FilterPlace = Filter.Place ?? string.Empty;
            var duration = Filter.MinDays is null && Filter.MaxDays is null ? null : $"{Filter.MinDays ?? 1}-{Filter.MaxDays ?? 90}";
            foreach (var choice in DurationChoices)
                choice.IsSelected = choice.Key == duration;
            foreach (var choice in BudgetChoices)
                choice.IsSelected = Filter.BudgetLevels.Contains(int.Parse(choice.Key, CultureInfo.InvariantCulture));
            foreach (var choice in StyleChoices)
                choice.IsSelected = Filter.Tags.Contains(choice.Key);
            foreach (var choice in MonthChoices)
                choice.IsSelected = Filter.Month?.ToString(CultureInfo.InvariantCulture) == choice.Key;
            foreach (var choice in SortChoices)
                choice.IsSelected = choice.Key == Filter.Sort;
            IsFilterOpen = true;
        }

        [RelayCommand]
        private void CloseFilter() => IsFilterOpen = false;

        [RelayCommand]
        private void ToggleChoice(ExploreChoice? choice)
        {
            if (choice is null)
                return;

            switch (choice.Group)
            {
                case GroupBudget:
                case GroupStyle:
                    choice.IsSelected = !choice.IsSelected;
                    break;
                case GroupSort:
                    foreach (var other in SortChoices)
                        other.IsSelected = ReferenceEquals(other, choice);
                    break;
                default:
                    // single choice that can be switched off again
                    var group = choice.Group == GroupDuration ? DurationChoices : MonthChoices;
                    var select = !choice.IsSelected;
                    foreach (var other in group)
                        other.IsSelected = false;
                    choice.IsSelected = select;
                    break;
            }
        }

        /// <summary>The sheet's choices as a filter (the search text is kept).</summary>
        internal ExploreFilter FilterFromSheet()
        {
            int? minDays = null, maxDays = null;
            if (DurationChoices.FirstOrDefault(c => c.IsSelected) is { } duration)
            {
                var parts = duration.Key.Split('-');
                minDays = int.Parse(parts[0], CultureInfo.InvariantCulture);
                maxDays = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }

            var month = MonthChoices.FirstOrDefault(c => c.IsSelected);
            var place = FilterPlace.Trim();
            return new ExploreFilter
            {
                Query = Filter.Query,
                Place = place.Length == 0 ? null : InputLimits.Truncate(place, 100),
                MinDays = minDays,
                MaxDays = maxDays,
                BudgetLevels = BudgetChoices.Where(c => c.IsSelected).Select(c => int.Parse(c.Key, CultureInfo.InvariantCulture)).ToList(),
                Tags = StyleChoices.Where(c => c.IsSelected).Select(c => c.Key).ToList(),
                Month = month is null ? null : int.Parse(month.Key, CultureInfo.InvariantCulture),
                Sort = SortChoices.FirstOrDefault(c => c.IsSelected)?.Key ?? ExploreSort.Recommended,
            };
        }

        [RelayCommand]
        private Task ApplyFilterAsync()
        {
            IsFilterOpen = false;
            return UseFilterAsync(FilterFromSheet());
        }

        /// <summary>"Temizle" on the sheet: every filter off (search and sort stay), applied at once.</summary>
        [RelayCommand]
        private Task ClearFilterAsync()
        {
            IsFilterOpen = false;
            return UseFilterAsync(Filter.Cleared());
        }

        /// <summary>"Filtreleri temizle" on an empty result: filters and search off.</summary>
        [RelayCommand]
        private Task ClearAllAsync()
        {
            _searchCts?.Cancel();
            if (SearchText.Length > 0)
            {
                // without starting a second, delayed search
                _suppressSearch = true;
                SearchText = string.Empty;
                _suppressSearch = false;
            }
            return UseFilterAsync(new ExploreFilter { Sort = Filter.Sort });
        }

        private Task UseFilterAsync(ExploreFilter next)
        {
            SaveFilter(next);
            if (next == Filter && HasLoaded && !HasError && !IsOfflineEmpty)
                return Task.CompletedTask;
            Filter = next;
            return ReloadAsync(useCache: true);
        }

        // ---- remembered on the device (without the search text) ----

        private sealed record StoredFilter(string? Place, int? MinDays, int? MaxDays, List<int>? BudgetLevels, List<string>? Tags, int? Month, string? Sort);

        private ExploreFilter LoadFilter()
        {
            try
            {
                var json = _preferences.Get(FilterPreferenceKey, string.Empty);
                if (string.IsNullOrEmpty(json) || JsonSerializer.Deserialize<StoredFilter>(json) is not { } stored)
                    return new ExploreFilter();

                var sort = stored.Sort is ExploreSort.Newest or ExploreSort.Shortest ? stored.Sort : ExploreSort.Recommended;
                return new ExploreFilter
                {
                    Place = string.IsNullOrWhiteSpace(stored.Place) ? null : InputLimits.Truncate(stored.Place.Trim(), 100),
                    MinDays = stored.MinDays is >= 1 and <= 90 ? stored.MinDays : null,
                    MaxDays = stored.MaxDays is >= 1 and <= 90 ? stored.MaxDays : null,
                    BudgetLevels = (stored.BudgetLevels ?? new()).Where(b => b is >= 1 and <= 3).Distinct().ToList(),
                    Tags = (stored.Tags ?? new()).Where(ExploreTags.All.Contains).Distinct().ToList(),
                    Month = stored.Month is >= 1 and <= 12 ? stored.Month : null,
                    Sort = sort,
                };
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidCastException)
            {
                return new ExploreFilter();
            }
        }

        private void SaveFilter(ExploreFilter value)
        {
            var stored = new StoredFilter(value.Place, value.MinDays, value.MaxDays, value.BudgetLevels.ToList(), value.Tags.ToList(), value.Month, value.Sort);
            _preferences.Set(FilterPreferenceKey, JsonSerializer.Serialize(stored));
        }

        // ---- navigation ----

        [RelayCommand]
        private Task OpenTripAsync(ExploreTripCard? card) => card is null
            ? Task.CompletedTask
            : RunSafeAsync(() => OpenDetail(card.Id));

        /// <summary>Opens a sample trip. Replaced in tests.</summary>
        internal Func<Guid, Task> OpenDetail { get; set; } = id =>
            Shell.Current is null ? Task.CompletedTask : Shell.Current.GoToAsync($"{Routes.ExploreDetail}?Id={id}");
    }
}
