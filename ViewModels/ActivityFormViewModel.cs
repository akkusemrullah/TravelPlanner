using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>Add/edit one itinerary activity (TP-401).</summary>
    [QueryProperty(nameof(TripId), "TripId")]
    [QueryProperty(nameof(ActivityId), "ActivityId")]
    [QueryProperty(nameof(DateParameter), "Date")]
    public partial class ActivityFormViewModel : ViewModelBase
    {
        // A form: never overwrite what the user is typing.
        protected override bool ReloadOnRemoteChanges => false;

        private readonly ITripService _tripService;
        private readonly IActivityService _activityService;
        private readonly IPlaceService _placeService;

        private TripActivity? _existing;
        private bool _loaded;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private int activityId;

        /// <summary>"yyyy-MM-dd" of the day the user was looking at when tapping "+".</summary>
        [ObservableProperty]
        private string? dateParameter;

        [ObservableProperty]
        private string pageTitle = AppResources.Activity_TitleAdd;

        [ObservableProperty]
        private string title = string.Empty;

        [ObservableProperty]
        private DateTime date = DateTime.Today;

        [ObservableProperty]
        private DateTime minimumDate = DateTime.Today.AddYears(-5);

        [ObservableProperty]
        private DateTime maximumDate = DateTime.Today.AddYears(5);

        [ObservableProperty]
        private bool hasTime;

        [ObservableProperty]
        private TimeSpan startTime = new(9, 0, 0);

        [ObservableProperty]
        private bool hasEndTime;

        [ObservableProperty]
        private TimeSpan endTime = new(10, 0, 0);

        [ObservableProperty]
        private string? note;

        [ObservableProperty]
        private SettingsOption<int?>? selectedPlace;

        /// <summary>"Yer yok" followed by the trip's saved places.</summary>
        public System.Collections.ObjectModel.ObservableCollection<SettingsOption<int?>> PlaceOptions { get; } = new();

        [ObservableProperty]
        private bool hasPlaces;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private string validationMessage = string.Empty;

        public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

        public ActivityFormViewModel(ITripService tripService, IActivityService activityService, IPlaceService placeService, IDialogService dialogs)
            : base(dialogs)
        {
            _tripService = tripService;
            _activityService = activityService;
            _placeService = placeService;
        }

        partial void OnTitleChanged(string value)
        {
            if (HasValidationError && !string.IsNullOrWhiteSpace(value))
                ValidationMessage = string.Empty;
        }

        /// <summary>The "Saat belirt" row was tapped (the whole row is the switch's touch target).</summary>
        [RelayCommand]
        private void ToggleHasTime() => HasTime = !HasTime;

        /// <summary>The "Bitiş saati ekle" row was tapped.</summary>
        [RelayCommand]
        private void ToggleHasEndTime() => HasEndTime = !HasEndTime;

        partial void OnStartTimeChanged(TimeSpan value)
        {
            // Keep a sensible default end time one hour after the start.
            if (HasEndTime && EndTime <= value)
            {
                var next = value.Add(TimeSpan.FromHours(1));
                EndTime = next.TotalHours < 24 ? next : new TimeSpan(23, 59, 0);
            }
        }

        /// <summary>Called from the page's OnAppearing - by then Shell has applied every query parameter.</summary>
        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (_loaded || TripId == 0) return;
            _loaded = true;

            var trip = await _tripService.GetByIdAsync(TripId);
            if (trip is not null)
            {
                var min = trip.StartDate.Date;
                var max = trip.EndDate.Date < min ? min : trip.EndDate.Date;

                // The DatePicker rejects (throws on) MinimumDate > MaximumDate, and the defaults are only
                // today ± 5 years - a trip further out (or longer ago) would cross them. Widen first, then narrow.
                if (min > MaximumDate)
                {
                    MaximumDate = max;
                    MinimumDate = min;
                }
                else
                {
                    MinimumDate = min;
                    MaximumDate = max;
                }
            }

            PlaceOptions.Clear();
            PlaceOptions.Add(new SettingsOption<int?>(null, AppResources.Activity_NoPlace));
            foreach (var place in (await _placeService.GetByTripIdAsync(TripId)).OrderBy(p => p.Name, StringComparer.CurrentCulture))
                PlaceOptions.Add(new SettingsOption<int?>(place.Id, place.Name));
            HasPlaces = PlaceOptions.Count > 1;

            if (ActivityId > 0)
                _existing = await _activityService.GetByIdAsync(ActivityId);

            if (_existing is not null)
            {
                PageTitle = AppResources.Activity_TitleEdit;
                Title = _existing.Title;
                Date = ClampDate(_existing.Date);
                Note = _existing.Note;
                HasTime = _existing.StartTime is not null;
                if (_existing.StartTime is TimeSpan start) StartTime = start;
                HasEndTime = _existing.EndTime is not null;
                if (_existing.EndTime is TimeSpan end) EndTime = end;
                SelectedPlace = PlaceOptions.FirstOrDefault(p => p.Value == _existing.PlaceId) ?? PlaceOptions[0];
            }
            else
            {
                Date = DateTime.TryParseExact(DateParameter, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                    ? ClampDate(day)
                    : ClampDate(DateTime.Today);
                SelectedPlace = PlaceOptions[0];
            }
        }, AppResources.Activity_LoadError);

        private DateTime ClampDate(DateTime value) =>
            value.Date < MinimumDate ? MinimumDate : value.Date > MaximumDate ? MaximumDate : value.Date;

        /// <summary>Returns the first validation problem, or null when the form is valid.</summary>
        internal string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Title))
                return AppResources.Activity_TitleRequired;

            if (HasTime && HasEndTime && EndTime <= StartTime)
                return AppResources.Activity_EndBeforeStart;

            return null;
        }

        [RelayCommand]
        private Task SaveAsync() => RunSafeAsync(async () =>
        {
            var error = Validate();
            if (error is not null)
            {
                ValidationMessage = error;
                return;
            }

            ValidationMessage = string.Empty;

            // Edit a copy so a failed save doesn't leave a half-updated object behind.
            var activity = _existing is null
                ? new TripActivity { TripId = TripId }
                : new TripActivity { Id = _existing.Id, TripId = _existing.TripId, SortOrder = _existing.SortOrder };

            activity.Title = Title.Trim();
            activity.Date = Date.Date;
            activity.StartTime = HasTime ? StartTime : null;
            activity.EndTime = HasTime && HasEndTime ? EndTime : null;
            activity.Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim();
            activity.PlaceId = SelectedPlace?.Value;

            await _activityService.SaveAsync(activity);
            await Shell.Current.GoToAsync("..");
        }, AppResources.Activity_SaveError);

        [RelayCommand]
        private async Task CancelAsync() => await Shell.Current.GoToAsync("..");
    }
}
