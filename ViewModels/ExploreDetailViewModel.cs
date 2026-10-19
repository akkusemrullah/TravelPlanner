using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>A section title in the sample trip's list ("GÜN GÜN PLAN").</summary>
    public sealed record ExploreSectionRow(string Title);

    /// <summary>"Gün 2" above that day's activities.</summary>
    public sealed record ExploreDayRow(string Header);

    /// <summary>A read-only tip.</summary>
    public sealed record ExploreTipRow(string Text);

    /// <summary>A row that "Düzenle ve ekle" can untick.</summary>
    public abstract partial class ExploreSelectableRow : ObservableObject
    {
        public abstract Guid Id { get; }

        [ObservableProperty]
        private bool isSelected = true;
    }

    public sealed class ExploreActivityRow : ExploreSelectableRow
    {
        public ExploreActivityRow(ExploreActivity activity, string? placeName)
        {
            Activity = activity;
            PlaceName = placeName;
        }

        public ExploreActivity Activity { get; }
        public string? PlaceName { get; }
        public override Guid Id => Activity.Id;
        public string Title => Activity.Title;

        /// <summary>"09:30", "—" without a time.</summary>
        public string TimeText => Activity.StartTime ?? "—";

        public string EndText => Activity.StartTime is null ? string.Empty : Activity.EndTime ?? string.Empty;
        public bool HasEnd => EndText.Length > 0;

        /// <summary>"Ayasofya · Erken git".</summary>
        public string Detail => string.Join(" · ", new[] { PlaceName, Activity.Note }
            .Where(s => !string.IsNullOrWhiteSpace(s) && !Activity.Title.Contains(s!, StringComparison.CurrentCultureIgnoreCase)));

        public bool HasDetail => Detail.Length > 0;
    }

    public sealed class ExplorePlaceRow : ExploreSelectableRow
    {
        public ExplorePlaceRow(ExplorePlace place) => Place = place;

        public ExplorePlace Place { get; }
        public override Guid Id => Place.Id;
        public string Name => Place.Name;
        public string Description => Place.Description ?? string.Empty;
        public bool HasDescription => Description.Length > 0;
    }

    public sealed class ExploreTodoRow : ExploreSelectableRow
    {
        public ExploreTodoRow(ExploreTodo todo) => Todo = todo;

        public ExploreTodo Todo { get; }
        public override Guid Id => Todo.Id;
        public string Title => Todo.Title;
    }

    /// <summary>
    /// TP-33: one sample trip of Keşfet - cover, facts, day plan, places (map), to-dos and tips - and adding it to the
    /// user's own trips: "Ekle" copies everything, "Düzenle ve ekle" lets rows be unticked first. Both end on a small
    /// sheet (name, start date, currency, budget). Works for guests too; the copy is independent of the sample.
    /// </summary>
    [QueryProperty(nameof(Id), "Id")]
    public partial class ExploreDetailViewModel : ViewModelBase
    {
        internal const string AddedPreferenceKey = "explore.added.v1";
        private const int MaxRememberedAdds = 300;

        private readonly IExploreService _explore;
        private readonly ITripService _trips;
        private readonly ISettingsService _settings;
        private readonly IPreferences _preferences;
        private readonly IConnectivity _connectivity;
        private CancellationTokenSource? _loadCts;

        public ExploreDetailViewModel(IExploreService explore, ITripService trips, ISettingsService settings,
                                      IPreferences preferences, IConnectivity connectivity, IDialogService dialogs)
            : base(dialogs)
        {
            _explore = explore;
            _trips = trips;
            _settings = settings;
            _preferences = preferences;
            _connectivity = connectivity;
        }

        public IReadOnlyList<SettingsOption<string>> Currencies { get; } = MoneyFormatter.SupportedCurrencies
            .Select(c => new SettingsOption<string>(c, MoneyFormatter.DisplayName(c))).ToList();

        /// <summary>Day plan, places, to-dos and tips as one flat, virtualized list.</summary>
        public RangeObservableCollection<object> Rows { get; } = new();

        [ObservableProperty]
        private string id = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTrip), nameof(CoverUrl), nameof(HasCover), nameof(Title), nameof(Location),
                                  nameof(DurationText),
                                  nameof(TagText), nameof(HasTags), nameof(MonthsText), nameof(Description), nameof(HasDescription),
                                  nameof(ImageCreditText), nameof(HasImageCredit), nameof(HasPlaces))]
        private ExploreTripDetail? trip;

        [ObservableProperty]
        private bool isLoading;

        [ObservableProperty]
        private bool hasError;

        [ObservableProperty]
        private bool isOffline;

        /// <summary>The sample was archived or unpublished meanwhile.</summary>
        [ObservableProperty]
        private bool isGone;

        /// <summary>"Düzenle ve ekle": check boxes on every row.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsViewing))]
        private bool isEditing;

        [ObservableProperty]
        private bool isAddSheetOpen;

        [ObservableProperty]
        private bool isAdding;

        [ObservableProperty]
        private string tripName = string.Empty;

        [ObservableProperty]
        private DateTime startDate = DateTime.Today.AddDays(14);

        [ObservableProperty]
        private SettingsOption<string>? selectedCurrency;

        [ObservableProperty]
        private string budgetText = string.Empty;

        public DateTime MinimumDate => DateTime.Today.AddYears(-1);
        public DateTime MaximumDate => InputLimits.MaxDate.AddDays(-AppConstants.MaxTripDays);

        public bool IsViewing => !IsEditing;
        public bool HasTrip => Trip is not null;
        public string? CoverUrl => Trip?.CoverUrl;
        public bool HasCover => CoverUrl is not null;
        public string Title => Trip?.Title ?? string.Empty;
        public string Location => Trip?.Location ?? string.Empty;
        public string DurationText => Trip?.DurationText ?? string.Empty;
        public string TagText => Trip is null ? string.Empty : string.Join(" · ", Trip.Tags.Select(ExploreTags.Label).Where(l => l.Length > 0));
        public bool HasTags => TagText.Length > 0;

        /// <summary>"Uygun: Nis, May, Eyl" or "Her mevsim".</summary>
        public string MonthsText => Trip is null
            ? string.Empty
            : Trip.Months.Count == 0
                ? AppResources.Explore_MonthsAll
                : string.Format(CultureInfo.CurrentCulture, AppResources.Explore_MonthsFormat, string.Join(", ",
                    Trip.Months.Where(m => m is >= 1 and <= 12).Distinct().Order()
                        .Select(m => CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedMonthName(m))));

        public string Description => (Trip?.Description ?? Trip?.Summary)?.Trim() ?? string.Empty;
        public bool HasDescription => Description.Length > 0;
        public string ImageCreditText => string.IsNullOrWhiteSpace(Trip?.ImageCredit)
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, AppResources.Explore_ImageCreditFormat, Trip.ImageCredit.Trim());
        public bool HasImageCredit => ImageCreditText.Length > 0;
        public bool HasPlaces => Trip?.Places.Any(p => PlaceService.IsValidCoordinate(p.Latitude, p.Longitude)) == true;

        // The sample doesn't change with a sync; never reload it under an open sheet.
        protected override bool ReloadOnRemoteChanges => false;

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (Trip is not null || !Guid.TryParse(Id, out var guid))
                return;

            _loadCts?.Cancel();
            var cts = new CancellationTokenSource();
            _loadCts = cts;
            HasError = false;
            IsGone = false;
            IsOffline = false;
            IsLoading = true;
            try
            {
                var detail = await _explore.GetDetailAsync(guid, cts.Token);
                if (cts.IsCancellationRequested)
                    return;
                if (detail is null)
                {
                    IsGone = true;
                    return;
                }
                Show(detail);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Explore] Loading the detail failed: {ex.Message}");
                if (_connectivity.NetworkAccess != NetworkAccess.Internet)
                    IsOffline = true;
                else
                    HasError = true;
            }
            finally
            {
                IsLoading = false;
            }
        });

        [RelayCommand]
        private Task RetryAsync() => LoadAsync();

        /// <summary>Leaving the page: a detail still loading is cancelled.</summary>
        public void Cancel() => _loadCts?.Cancel();

        internal void Show(ExploreTripDetail detail)
        {
            Trip = detail;
            Rows.ReplaceAll(BuildRows(detail));
        }

        internal static IEnumerable<object> BuildRows(ExploreTripDetail detail)
        {
            var names = detail.Places.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);
            var days = detail.Activities.Where(a => a.DayIndex >= 1 && a.DayIndex <= detail.DurationDays)
                .GroupBy(a => a.DayIndex).OrderBy(g => g.Key).ToList();
            if (days.Count > 0)
            {
                yield return new ExploreSectionRow(AppResources.Explore_SectionDays);
                foreach (var day in days)
                {
                    yield return new ExploreDayRow(string.Format(CultureInfo.CurrentCulture, AppResources.Explore_DayFormat, day.Key));
                    foreach (var activity in day)
                        yield return new ExploreActivityRow(activity,
                            activity.PlaceId is Guid placeId ? names.GetValueOrDefault(placeId) : null);
                }
            }

            if (detail.Places.Count > 0)
            {
                yield return new ExploreSectionRow(AppResources.Explore_SectionPlaces);
                foreach (var place in detail.Places)
                    yield return new ExplorePlaceRow(place);
            }

            var todos = detail.Todos.Where(t => t.Kind != ExploreTodo.KindPacking).ToList();
            if (todos.Count > 0)
            {
                yield return new ExploreSectionRow(AppResources.Explore_SectionTodos);
                foreach (var todo in todos)
                    yield return new ExploreTodoRow(todo);
            }

            var packing = detail.Todos.Where(t => t.Kind == ExploreTodo.KindPacking).ToList();
            if (packing.Count > 0)
            {
                yield return new ExploreSectionRow(AppResources.Explore_SectionPacking);
                foreach (var item in packing)
                    yield return new ExploreTodoRow(item);
            }

            if (detail.Tips.Count > 0)
            {
                yield return new ExploreSectionRow(AppResources.Explore_SectionTips);
                foreach (var tip in detail.Tips.Where(t => !string.IsNullOrWhiteSpace(t)))
                    yield return new ExploreTipRow(tip.Trim());
            }
        }

        // ---- adding ----

        /// <summary>"Ekle": everything, straight to the sheet.</summary>
        [RelayCommand]
        private void StartAdd()
        {
            if (Trip is null)
                return;
            foreach (var row in Rows.OfType<ExploreSelectableRow>())
                row.IsSelected = true;
            IsEditing = false;
            OpenSheet();
        }

        /// <summary>"Düzenle ve ekle": tick boxes first.</summary>
        [RelayCommand]
        private void StartEdit()
        {
            if (Trip is null)
                return;
            IsEditing = true;
        }

        [RelayCommand]
        private void CancelEdit()
        {
            IsEditing = false;
            foreach (var row in Rows.OfType<ExploreSelectableRow>())
                row.IsSelected = true;
        }

        /// <summary>"Devam" after unticking.</summary>
        [RelayCommand]
        private void ContinueEdit() => OpenSheet();

        private void OpenSheet()
        {
            if (Trip is null)
                return;
            if (string.IsNullOrWhiteSpace(TripName))
                TripName = InputLimits.Truncate(Trip.Title, 100)!;
            SelectedCurrency ??= Currencies.FirstOrDefault(c => c.Value == MoneyFormatter.Normalize(_settings.CurrencyCode)) ?? Currencies[0];
            IsAddSheetOpen = true;
        }

        [RelayCommand]
        private void CloseSheet() => IsAddSheetOpen = false;

        /// <summary>The sheet's budget; null when it isn't a valid amount (empty = 0).</summary>
        internal long? ParseBudget()
        {
            var text = BudgetText.Trim();
            if (text.Length == 0)
                return 0;
            if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) || !InputLimits.IsValidAmount(amount))
                return null;
            return MoneyConverter.ToMinor(amount);
        }

        /// <summary>What goes into the copy: everything, or only the ticked rows after "Düzenle ve ekle".</summary>
        internal ExploreAddOptions BuildOptions(long budgetMinor)
        {
            var name = string.IsNullOrWhiteSpace(TripName) ? Trip!.Title : TripName.Trim();
            if (!IsEditing)
                return new ExploreAddOptions(name, StartDate.Date, SelectedCurrency?.Value ?? MoneyFormatter.CurrencyCode, budgetMinor);

            var selected = Rows.OfType<ExploreSelectableRow>().Where(r => r.IsSelected).ToList();
            return new ExploreAddOptions(name, StartDate.Date, SelectedCurrency?.Value ?? MoneyFormatter.CurrencyCode, budgetMinor,
                ActivityIds: selected.OfType<ExploreActivityRow>().Select(r => r.Id).ToHashSet(),
                PlaceIds: selected.OfType<ExplorePlaceRow>().Select(r => r.Id).ToHashSet(),
                TodoIds: selected.OfType<ExploreTodoRow>().Select(r => r.Id).ToHashSet());
        }

        [RelayCommand]
        private Task ConfirmAddAsync() => RunSafeAsync(async () =>
        {
            if (Trip is not { } template || IsAdding)
                return;

            if (ParseBudget() is not long budget)
            {
                await Dialogs.ShowMessageAsync(AppResources.Explore_SheetTitle, AppResources.Explore_BudgetInvalid);
                return;
            }

            if (WasAddedBefore(template.Id) &&
                !await Dialogs.ConfirmAsync(AppResources.Explore_AgainTitle, AppResources.Explore_AgainMessage,
                                            AppResources.Explore_Add, AppResources.Common_Cancel))
                return;

            IsAdding = true;
            Trip created;
            try
            {
                created = await _trips.CreateFromTemplateAsync(template, BuildOptions(budget));
            }
            catch (TripLimitException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Explore_SheetTitle, AppResources.Explore_TripLimit);
                return;
            }
            finally
            {
                IsAdding = false;
            }

            RememberAdded(template.Id);
            IsAddSheetOpen = false;
            IsEditing = false;
            ShowNotice(AppResources.Explore_Added);
            await AfterAdded(created.Id);
        }, AppResources.Explore_AddError);

        /// <summary>A short "Seyahatlerine eklendi" (a toast on Android, a message elsewhere). Replaced in tests.</summary>
        internal Action<string> ShowNotice { get; set; } = text =>
        {
            if (!AppNotice.TryShow(text))
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (Shell.Current?.CurrentPage is { } page)
                        await page.DisplayAlertAsync(AppResources.Explore_Title, text, AppResources.Common_Ok);
                });
        };

        /// <summary>After adding: the new trip, in the Trips tab. Replaced in tests.</summary>
        internal Func<int, Task> AfterAdded { get; set; } = async tripId =>
        {
            if (Shell.Current is null)
                return;
            await Shell.Current.GoToAsync("..", false);
            await Shell.Current.GoToAsync($"//{Routes.TripList}/{Routes.TripDetail}?TripId={tripId}");
        };

        // "Bu seyahati daha önce ekledin" - a simple list of sample ids on this device (not tied to the account).
        private HashSet<string> AddedIds() =>
            _preferences.Get(AddedPreferenceKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        internal bool WasAddedBefore(Guid templateId) => AddedIds().Contains(templateId.ToString("N"));

        private void RememberAdded(Guid templateId)
        {
            var ids = _preferences.Get(AddedPreferenceKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var key = templateId.ToString("N");
            ids.Remove(key);
            ids.Add(key);
            _preferences.Set(AddedPreferenceKey, string.Join(',', ids.TakeLast(MaxRememberedAdds)));
        }

        // ---- map ----

        [RelayCommand]
        private Task OpenMapAsync() => RunSafeAsync(async () =>
        {
            if (Trip is null || !HasPlaces)
                return;
            MapPreview.Show(Trip.Title, Trip.Places
                .Where(p => PlaceService.IsValidCoordinate(p.Latitude, p.Longitude))
                .Select((p, i) => new Place { Id = i + 1, Name = p.Name, Description = p.Description, Latitude = p.Latitude, Longitude = p.Longitude })
                .ToList());
            if (Shell.Current is not null)
                await Shell.Current.GoToAsync($"{Routes.Map}?Preview=true");
        });

        [RelayCommand]
        private async Task GoBackAsync()
        {
            if (IsAddSheetOpen)
            {
                IsAddSheetOpen = false;
                return;
            }
            if (IsEditing)
            {
                CancelEdit();
                return;
            }
            if (Shell.Current is not null)
                await Shell.Current.GoToAsync("..");
        }

        /// <summary>Hardware back: closes the sheet / leaves "Düzenle ve ekle" first. True when it did.</summary>
        public bool HandleBack()
        {
            if (IsAddSheetOpen)
            {
                IsAddSheetOpen = false;
                return true;
            }
            if (IsEditing)
            {
                CancelEdit();
                return true;
            }
            return false;
        }
    }

    /// <summary>Pins handed to MapPage for a sample trip that is not saved anywhere (MapViewModel's preview mode).</summary>
    public static class MapPreview
    {
        public static string Title { get; private set; } = string.Empty;
        public static IReadOnlyList<Place> Places { get; private set; } = Array.Empty<Place>();

        public static void Show(string title, IReadOnlyList<Place> places)
        {
            Title = title;
            Places = places;
        }
    }
}
