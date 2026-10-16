using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One day tab of the itinerary ("2. Gün · Sal 13 Eki").</summary>
    public partial class ItineraryDay : ObservableObject
    {
        public ItineraryDay(DateTime date, int number)
        {
            Date = date.Date;
            Number = number;
            Title = string.Format(AppResources.Itinerary_DayFormat, number);
            DateLabel = Date.ToString("ddd d MMM", CultureInfo.CurrentCulture);
            IsToday = Date == DateTime.Today;
        }

        public DateTime Date { get; }
        public int Number { get; }
        public string Title { get; }
        public string DateLabel { get; }
        public bool IsToday { get; }

        [ObservableProperty]
        private bool isSelected;

        [ObservableProperty]
        private int activityCount;
    }

    /// <summary>
    /// Day-by-day plan of a trip (TP-401): one tab per trip day, the selected day's activities in order,
    /// reordered by dragging the grip on each row.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class ItineraryViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly ITripService _tripService;
        private readonly IActivityService _activityService;
        private readonly IPlaceService _placeService;

        private List<TripActivity> _allActivities = new();
        private TripActivity? _dragged;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private Trip? trip;

        [ObservableProperty]
        private ItineraryDay? selectedDay;

        [ObservableProperty]
        private bool hasActivities;

        [ObservableProperty]
        private bool hasNoActivities;

        /// <summary>Any day of the trip has an activity.</summary>
        [ObservableProperty]
        private bool hasAnyActivities;

        /// <summary>
        /// Shown above the day tabs when part of the plan can't be displayed: a trip saved before the
        /// <see cref="AppConstants.MaxTripDays"/> limit that is longer than that, or activities dated outside the
        /// trip (left behind by date edits in older versions). Empty = nothing to report.
        /// </summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasRangeNotice))]
        private string rangeNotice = string.Empty;

        public bool HasRangeNotice => !string.IsNullOrEmpty(RangeNotice);

        public ObservableCollection<ItineraryDay> Days { get; } = new RangeObservableCollection<ItineraryDay>();

        /// <summary>The selected day's activities in display order.</summary>
        public ObservableCollection<TripActivity> DayActivities { get; } = new RangeObservableCollection<TripActivity>();

        /// <summary>"Seçerek sil" mode of the selected day's list.</summary>
        public ListSelection<TripActivity> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button: only with something to delete and the right to delete it.</summary>
        public bool ShowDeleteMenu => CanEdit && HasActivities;

        /// <summary>The add button and the reorder grips are out of the way while rows are being ticked.</summary>
        public bool ShowEditActions => CanEdit && !Selection.IsActive;

        public ItineraryViewModel(ITripService tripService, IActivityService activityService, IPlaceService placeService, IDialogService dialogs)
            : base(dialogs)
        {
            _tripService = tripService;
            _activityService = activityService;
            _placeService = placeService;
            Selection = new ListSelection<TripActivity>(() => DayActivities);
            Selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selection.IsActive))
                    OnPropertyChanged(nameof(ShowEditActions));
            };
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(CanEdit) or nameof(HasActivities))
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowDeleteMenu)));
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowEditActions)));
            }
        }

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            await RefreshAccessAsync(TripId); // G6: viewers don't get add/edit buttons

            Trip = await _tripService.GetByIdAsync(TripId);
            if (Trip is null) return;

            var placeNames = (await _placeService.GetByTripIdAsync(TripId)).ToDictionary(p => p.Id, p => p.Name);
            _allActivities = await _activityService.GetByTripIdAsync(TripId);
            foreach (var activity in _allActivities)
                activity.PlaceName = activity.PlaceId is int id && placeNames.TryGetValue(id, out var name) ? name : null;

            var previouslySelected = SelectedDay?.Date;
            var days = BuildDays(Trip).ToList();
            foreach (var day in days)
                day.ActivityCount = _allActivities.Count(a => a.Date.Date == day.Date);
            Days.ReplaceAll(days);

            // Keep the user's tab across reloads; otherwise open on today (mid-trip) or day 1.
            RangeNotice = BuildRangeNotice(Trip, Days.Count, _allActivities.Count(a => !Days.Any(d => d.Date == a.Date.Date)));

            var target = Days.FirstOrDefault(d => d.Date == previouslySelected)
                         ?? Days.FirstOrDefault(d => d.IsToday)
                         ?? Days.FirstOrDefault();
            SelectDay(target);
        }, AppResources.Itinerary_LoadError);

        /// <summary>One tab per trip day, capped at <see cref="AppConstants.MaxItineraryDays"/>.</summary>
        internal static List<ItineraryDay> BuildDays(Trip trip)
        {
            var start = trip.StartDate.Date;
            var end = trip.EndDate.Date < start ? start : trip.EndDate.Date;
            var count = Math.Min((end - start).Days + 1, AppConstants.MaxItineraryDays);
            return Enumerable.Range(0, count).Select(i => new ItineraryDay(start.AddDays(i), i + 1)).ToList();
        }

        internal static string BuildRangeNotice(Trip trip, int shownDays, int hiddenActivities)
        {
            var parts = new List<string>();
            if (trip.DurationInDays > shownDays)
                parts.Add(string.Format(AppResources.Itinerary_TruncatedFormat, trip.DurationInDays, shownDays));
            if (hiddenActivities > 0)
                parts.Add(string.Format(AppResources.Itinerary_HiddenActivitiesFormat, hiddenActivities));
            return string.Join(" ", parts);
        }

        [RelayCommand]
        private void SelectDay(ItineraryDay? day)
        {
            foreach (var d in Days)
                d.IsSelected = ReferenceEquals(d, day);

            SelectedDay = day;
            RefreshDayActivities();
        }

        private void RefreshDayActivities()
        {
            DayActivities.ReplaceAll(SelectedDay is null
                ? Enumerable.Empty<TripActivity>()
                : _allActivities.Where(a => a.Date.Date == SelectedDay.Date).OrderBy(a => a.SortOrder));
            if (SelectedDay is not null)
                SelectedDay.ActivityCount = DayActivities.Count;

            HasActivities = DayActivities.Count > 0;
            HasNoActivities = SelectedDay is not null && !HasActivities;
            HasAnyActivities = _allActivities.Count > 0;
            Selection.Sync();
        }

        [RelayCommand]
        private async Task AddActivityAsync()
        {
            if (Trip is null) return;
            var date = (SelectedDay?.Date ?? Trip.StartDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await Shell.Current.GoToAsync($"{Routes.ActivityForm}?TripId={Trip.Id}&Date={date}");
        }

        [RelayCommand]
        private async Task EditActivityAsync(TripActivity? activity)
        {
            if (activity is null || Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.ActivityForm}?TripId={Trip.Id}&ActivityId={activity.Id}");
        }

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on an activity: opens it, or ticks it in selection mode.</summary>
        [RelayCommand]
        private async Task ActivityTappedAsync(TripActivity? activity)
        {
            if (activity is null) return;

            if (Selection.IsActive)
                Selection.Toggle(activity);
            else
                await EditActivityAsync(activity);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.Itinerary_DeleteError);

        private async Task DeleteManyAsync(IReadOnlyList<TripActivity> activities, string title)
        {
            if (activities.Count == 0)
                return;

            var confirmed = activities.Count == 1
                ? await Dialogs.ConfirmAsync(AppResources.Itinerary_DeleteTitle, string.Format(AppResources.Itinerary_DeleteMessage, activities[0].Title))
                : await ConfirmBulkDeleteAsync(title, string.Format(AppResources.TripDelete_ActivitiesFormat, activities.Count));
            if (!confirmed)
                return;

            try
            {
                await DeleteEachAsync(activities, a => _activityService.DeleteAsync(a), a =>
                {
                    _allActivities.Remove(a);
                    DayActivities.Remove(a);
                });

                // The rest of the open day closes up (0..n-1); other days keep their order as it is.
                await PersistDayOrderAsync();
            }
            finally
            {
                Selection.Cancel();
                foreach (var day in Days)
                    day.ActivityCount = _allActivities.Count(a => a.Date.Date == day.Date);
                RefreshDayActivities();
            }
        }

        // ---- Reordering: drag & drop ----

        [RelayCommand]
        private void DragStarting(TripActivity? activity) => _dragged = activity;

        [RelayCommand]
        private Task DropAsync(TripActivity? target) => RunSafeAsync(async () =>
        {
            var dragged = _dragged;
            _dragged = null;
            if (dragged is null || target is null || ReferenceEquals(dragged, target))
                return;

            await MoveToIndexAsync(dragged, DayActivities.IndexOf(target));
        }, AppResources.Itinerary_ReorderError);

        private async Task MoveToIndexAsync(TripActivity activity, int newIndex)
        {
            var oldIndex = DayActivities.IndexOf(activity);
            if (oldIndex < 0 || newIndex < 0 || newIndex >= DayActivities.Count || newIndex == oldIndex)
                return;

            DayActivities.Move(oldIndex, newIndex);
            try
            {
                await PersistDayOrderAsync();
            }
            catch
            {
                // Not saved (e.g. read-only trip): put the row back so the list shows the stored order.
                DayActivities.Move(newIndex, oldIndex);
                throw;
            }
        }

        // ---- Reordering with the grip on the right of each row (ItineraryPage → DragHandle) ----
        // The page moves the row one place at a time while the finger is down (MoveStep, in memory only) and
        // saves the final order once when it is lifted (EndReorderAsync) - one write, one sync change set.

        private bool _reorderChanged;

        /// <summary>Starts a grip drag. False when the row can't be moved (read-only trip, row not in the day).</summary>
        public bool BeginReorder(TripActivity activity)
        {
            _reorderChanged = false;
            return CanEdit && !Selection.IsActive && DayActivities.Contains(activity);
        }

        /// <summary>Moves the row one place up (direction &lt; 0) or down (&gt; 0). False at either end of the day.</summary>
        public bool MoveStep(TripActivity activity, int direction)
        {
            var from = DayActivities.IndexOf(activity);
            var to = from + Math.Sign(direction);
            if (from < 0 || direction == 0 || to < 0 || to >= DayActivities.Count)
                return false;

            DayActivities.Move(from, to);
            _reorderChanged = true;
            return true;
        }

        /// <summary>The finger was lifted: saves the new order (if it changed). On failure the stored order is reloaded.</summary>
        public Task EndReorderAsync() => RunSafeAsync(async () =>
        {
            if (!_reorderChanged)
                return;
            _reorderChanged = false;

            try
            {
                await PersistDayOrderAsync();
            }
            catch
            {
                await LoadAsync(); // show what is actually stored again
                throw;
            }
        }, AppResources.Itinerary_ReorderError);

        private async Task PersistDayOrderAsync()
        {
            var ordered = DayActivities.ToList();
            await _activityService.ReorderAsync(ordered); // also updates each SortOrder in memory
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
