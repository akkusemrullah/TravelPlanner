using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>
    /// Current weather for a trip's destination city, plus the weather for every day of the trip (TP-402):
    /// Open-Meteo's daily forecast within 16 days, past-years averages beyond that.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class WeatherViewModel : ViewModelBase
    {
        // The forecast comes from a weather API, not from trip data - a sync doesn't change it.
        protected override bool ReloadOnRemoteChanges => false;

        private readonly ITripService _tripService;
        private readonly IWeatherService _weatherService;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private Trip? trip;

        [ObservableProperty]
        private WeatherInfo? weather;

        [ObservableProperty]
        private bool hasError;

        /// <summary>Text for the error card: a specific "you're offline" message, or the generic one (TP-306).</summary>
        [ObservableProperty]
        private string errorMessage = AppResources.Weather_Error;

        private readonly IConnectivity _connectivity;

        /// <summary>One row per trip day (TP-402).</summary>
        public ObservableCollection<DailyForecast> TripForecast { get; } = new RangeObservableCollection<DailyForecast>();

        [ObservableProperty]
        private bool hasTripForecast;

        /// <summary>Shown when the trip-day forecast couldn't be loaded while current weather did.</summary>
        [ObservableProperty]
        private bool tripForecastUnavailable;

        /// <summary>True when at least one day is a past-years average - shows the explanatory note.</summary>
        [ObservableProperty]
        private bool hasClimateAverages;

        /// <summary>"Only the first 90 days are shown" for trips longer than <see cref="AppConstants.MaxTripDays"/> (saved before the limit).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasForecastNotice))]
        private string forecastNotice = string.Empty;

        public bool HasForecastNotice => !string.IsNullOrEmpty(ForecastNotice);

        public WeatherViewModel(ITripService tripService, IWeatherService weatherService, IConnectivity connectivity, IDialogService dialogs)
            : base(dialogs)
        {
            _tripService = tripService;
            _weatherService = weatherService;
            _connectivity = connectivity;
        }

        partial void OnTripIdChanged(int value) => LoadWeatherAsync(value).FireAndForget();

        private Task LoadWeatherAsync(int id) => RunLoadAsync(async () =>
        {
            if (id == 0) return;

            HasError = false;
            try
            {
                Trip = await _tripService.GetByIdAsync(id);
                if (Trip is null)
                {
                    HasError = true;
                    return;
                }

                // IWeatherService already turns network/API failures into null; HasError drives the
                // in-page "unavailable" message, so no extra dialog is needed for that case.
                // Current conditions and the trip-day forecast are independent requests - run them together.
                var weatherTask = _weatherService.GetWeatherAsync(Trip);
                var forecastTask = _weatherService.GetTripForecastAsync(Trip);
                await Task.WhenAll(weatherTask, forecastTask);

                Weather = await weatherTask;
                HasError = Weather is null;
                ApplyTripForecast(await forecastTask);
                ErrorMessage = _connectivity.NetworkAccess == NetworkAccess.Internet
                    ? AppResources.Weather_Error
                    : AppResources.Weather_Offline;
            }
            catch
            {
                HasError = true;
                throw;
            }
        }, AppResources.Weather_LoadError);

        private void ApplyTripForecast(List<DailyForecast>? forecast)
        {
            TripForecast.ReplaceAll(forecast ?? new List<DailyForecast>());

            HasTripForecast = TripForecast.Count > 0;
            HasClimateAverages = TripForecast.Any(d => d.IsClimateAverage);
            ForecastNotice = HasTripForecast && Trip is { } trip && trip.DurationInDays > AppConstants.MaxTripDays
                ? string.Format(AppResources.Weather_TruncatedFormat, AppConstants.MaxTripDays)
                : string.Empty;
            // Only worth a separate message when the current weather worked (otherwise the error card covers it).
            TripForecastUnavailable = !HasTripForecast && !HasError;
        }

        [RelayCommand]
        private Task RefreshAsync() => TripId > 0 ? LoadWeatherAsync(TripId) : Task.CompletedTask;

        // Backs the page's custom back-arrow header button (Shell.NavBarIsVisible="False" now that this
        // page builds its own header to match TripDetailPage's design) - same "..".GoToAsync convention
        // used by TripDetailViewModel.GoBackAsync and the form pages' CancelAsync commands.
        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");
    }
}
