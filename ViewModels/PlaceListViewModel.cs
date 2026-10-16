using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class PlaceListViewModel : ViewModelBase, ISelectableListViewModel
    {
        private readonly IPlaceService _placeService;

        [ObservableProperty]
        private int tripId;

        public ObservableCollection<Place> Places { get; } = new RangeObservableCollection<Place>();

        /// <summary>"Seçerek sil" mode of the list.</summary>
        public ListSelection<Place> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button: only with something to delete and the right to delete it.</summary>
        public bool ShowDeleteMenu => CanEdit && HasPlaces;

        /// <summary>The add button is out of the way while rows are being ticked.</summary>
        public bool ShowAddButton => CanEdit && !Selection.IsActive;

        [ObservableProperty]
        private bool hasPlaces;

        [ObservableProperty]
        private bool hasNoPlaces; // false until the first load has run, so the empty state never flashes

        public PlaceListViewModel(IPlaceService placeService, IDialogService dialogs)
            : base(dialogs)
        {
            _placeService = placeService;
            Selection = new ListSelection<Place>(() => Places);
            Selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selection.IsActive))
                    OnPropertyChanged(nameof(ShowAddButton));
            };
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(CanEdit) or nameof(HasPlaces))
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowDeleteMenu)));
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowAddButton)));
            }
        }

        // NOTE (TP-104): loading is triggered only from PlaceListPage.OnAppearing, not from a TripId
        // change as well - doing both raced and could duplicate rows.

        [RelayCommand]
        private Task LoadPlacesAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            await RefreshAccessAsync(TripId); // G6: viewers don't get add/edit buttons

            var places = await _placeService.GetByTripIdAsync(TripId);
            Places.ReplaceAll(places);

            UpdateEmptyState();
            Selection.Sync();
        }, AppResources.PlaceList_LoadError);

        private void UpdateEmptyState()
        {
            HasPlaces = Places.Count > 0;
            HasNoPlaces = !HasPlaces;
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        [RelayCommand]
        private async Task GoToAddPlaceAsync()
        {
            await Shell.Current.GoToAsync($"{Routes.PlaceForm}?TripId={TripId}");
        }

        [RelayCommand]
        private async Task GoToEditPlaceAsync(Place place)
        {
            if (place is null) return;
            await Shell.Current.GoToAsync($"{Routes.PlaceForm}?TripId={TripId}&PlaceId={place.Id}");
        }

        [RelayCommand]
        private Task ToggleVisitedAsync(Place place) => RunSafeAsync(async () =>
        {
            if (place is null) return;

            place.IsVisited = !place.IsVisited;
            try
            {
                await _placeService.SaveAsync(place);
            }
            catch
            {
                place.IsVisited = !place.IsVisited; // keep the UI in sync with what's actually stored
                throw;
            }

            var index = Places.IndexOf(place);
            if (index >= 0)
                Places[index] = place;
        }, AppResources.PlaceList_VisitedError);

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on a row: opens the place, or ticks it in selection mode.</summary>
        [RelayCommand]
        private async Task PlaceTappedAsync(Place? place)
        {
            if (place is null) return;

            if (Selection.IsActive)
                Selection.Toggle(place);
            else
                await GoToEditPlaceAsync(place);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.PlaceList_DeleteError);

        private async Task DeleteManyAsync(IReadOnlyList<Place> places, string title)
        {
            if (places.Count == 0)
                return;

            var confirmed = places.Count == 1
                ? await Dialogs.ConfirmAsync(AppResources.PlaceList_DeleteTitle, string.Format(AppResources.PlaceList_DeleteMessage, places[0].Name))
                : await ConfirmBulkDeleteAsync(title, string.Format(AppResources.TripDelete_PlacesFormat, places.Count));
            if (!confirmed)
                return;

            try
            {
                await DeleteEachAsync(places, p => _placeService.DeleteAsync(p), p => Places.Remove(p));
            }
            finally
            {
                UpdateEmptyState();
                Selection.Cancel();
            }
        }
    }
}
