using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    [QueryProperty(nameof(InitialCity), "City")]
    [QueryProperty(nameof(InitialCountry), "Country")]
    public partial class TripFormViewModel : ViewModelBase
    {
        private readonly ITripService _tripService;
        private readonly IGeocodingService _geocodingService;
        private readonly IExpenseService _expenseService;
        private readonly IExchangeRateService _exchangeRates;
        private readonly IBudgetAlertService _budgetAlerts;
        private readonly IConnectivity _connectivity;
        private readonly IActivityService? _activityService;
        private readonly ITripCurrencyService? _tripCurrency;
        private readonly IDateRangePicker? _dateRangePicker;
        private readonly ITripCoverService? _covers;
        private readonly ICoverCropper? _cropper;
        private readonly IMediaPicker? _mediaPicker;

        /// <summary>A photo picked in this form, cropped and waiting for Kaydet (TripCoverService.PrepareAsync).</summary>
        private string? _stagedCoverId;

        /// <summary>Kaldır was tapped: the trip's cover goes on Kaydet.</summary>
        private bool _removeCover;

        /// <summary>The cover shown in the form: the picked photo, else the trip's (a file on the phone); null = none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCover))]
        private string? coverPreview;

        [ObservableProperty]
        private bool isCoverBusy;

        public bool HasCover => CoverPreview is not null;

        /// <summary>Covers need the image code of Android / iOS and a picker.</summary>
        public bool CanEditCover => _covers is not null && _mediaPicker is not null;

        /// <summary>True while typing without internet - shows a hint that suggestions can't load (TP-306).</summary>
        [ObservableProperty]
        private bool isOffline;
        private Trip? _existingTrip;

        // Set while City is changed programmatically (loading an existing trip or picking a suggestion),
        // so OnCityChanged doesn't fire a fresh search for text the user didn't type.
        private bool _isApplyingCitySelection;

        // TP-106: the city came from a suggestion (or is the saved trip's own). Typing clears it; saving needs it,
        // as the place form does - so City and Country always name a real place.
        private bool _hasPickedCity;

        // Incremented on every keystroke; a search only applies its results if it's still the latest.
        private int _citySearchGeneration;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string country = string.Empty;

        [ObservableProperty]
        private string city = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DateRangeText), nameof(DayCountText))]
        private DateTime startDate = DateTime.Today;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DateRangeText), nameof(DayCountText))]
        private DateTime endDate = DateTime.Today.AddDays(1);

        /// <summary>True when one calendar picks both dates (Android); otherwise the form shows two date pickers.</summary>
        public bool UsesRangePicker => _dateRangePicker?.IsSupported == true;

        public bool UsesTwoDatePickers => !UsesRangePicker;

        /// <summary>The single dates field: "20 Eki – 27 Eki 2026", with both years when the trip crosses one.</summary>
        public string DateRangeText
        {
            get
            {
                var culture = System.Globalization.CultureInfo.CurrentCulture;
                var startFormat = StartDate.Year == EndDate.Year ? "d MMM" : "d MMM yyyy";
                return $"{StartDate.ToString(startFormat, culture)} – {EndDate.ToString("d MMM yyyy", culture)}";
            }
        }

        public string DayCountText => string.Format(AppResources.Explore_DaysFormat, (EndDate.Date - StartDate.Date).Days + 1);

        /// <summary>
        /// Latest end date the End DatePicker offers: start + <see cref="AppConstants.MaxTripDays"/> - 1. Day-based
        /// screens (plan, weather, spending chart) only cover that many days, so a longer trip isn't accepted.
        /// </summary>
        [ObservableProperty]
        private DateTime maximumEndDate = DateTime.Today.AddDays(AppConstants.MaxTripDays - 1);

        [ObservableProperty]
        private decimal plannedBudget;

        [ObservableProperty]
        private string? description;

        /// <summary>The trip's budget currency (TP-403). New trips start with the default from Settings.</summary>
        [ObservableProperty]
        private SettingsOption<string>? selectedCurrency;

        public IReadOnlyList<SettingsOption<string>> Currencies { get; } = MoneyFormatter.SupportedCurrencies
            .Select(code => new SettingsOption<string>(code, MoneyFormatter.DisplayName(code)))
            .ToList();

        [ObservableProperty]
        private string pageTitle = AppResources.TripForm_TitleAdd;

        [ObservableProperty]
        private bool isCitySuggestionsVisible;

        /// <summary>Validation message shown above the Save button (TP-102). Empty = no error.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private string validationMessage = string.Empty;

        public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

        public ObservableCollection<CitySuggestion> CitySuggestions { get; } = new();

        public TripFormViewModel(
            ITripService tripService,
            IGeocodingService geocodingService,
            IExpenseService expenseService,
            IExchangeRateService exchangeRates,
            IBudgetAlertService budgetAlerts,
            ISettingsService settings,
            IConnectivity connectivity,
            IDialogService dialogs,
            IActivityService? activityService = null,
            ITripCurrencyService? tripCurrency = null,
            IDateRangePicker? dateRangePicker = null,
            ITripCoverService? covers = null,
            IMediaPicker? mediaPicker = null,
            ICoverCropper? cropper = null)
            : base(dialogs)
        {
            _covers = covers;
            _mediaPicker = mediaPicker;
            _cropper = cropper;
            _tripService = tripService;
            _geocodingService = geocodingService;
            _expenseService = expenseService;
            _exchangeRates = exchangeRates;
            _budgetAlerts = budgetAlerts;
            _connectivity = connectivity;
            _activityService = activityService;
            _tripCurrency = tripCurrency;
            _dateRangePicker = dateRangePicker;

            SelectedCurrency = FindCurrency(settings.CurrencyCode);
        }

        /// <summary>TP-11: through the currency service (server-side for trips in the cloud); without it, on the device.</summary>
        private async Task<TripCurrencyChangeOutcome> ConvertCurrencyAsync(int tripId, string from, string to, decimal rate)
        {
            if (_tripCurrency is not null)
                return await _tripCurrency.ConvertAsync(tripId, from, to, rate);

            try
            {
                await _expenseService.ConvertTripExpensesAsync(tripId, from, to, rate);
                return TripCurrencyChangeOutcome.Converted;
            }
            catch (ArgumentOutOfRangeException)
            {
                return TripCurrencyChangeOutcome.TooLarge;
            }
        }

        private SettingsOption<string> FindCurrency(string? code) =>
            Currencies.FirstOrDefault(c => string.Equals(c.Value, MoneyFormatter.Normalize(code), StringComparison.OrdinalIgnoreCase)) ?? Currencies[0];

        /// <summary>
        /// TP-106: the city picked from Home's suggestions. A new trip starts with it (and its country, below), already
        /// counted as picked, and the trip name defaults to the city.
        /// </summary>
        public string? InitialCity
        {
            get => null;
            set
            {
                var city = (value ?? string.Empty).Trim();
                if (TripId != 0 || city.Length == 0)
                    return;
                city = city.Length > NameMaxLength ? city[..NameMaxLength] : city;
                _isApplyingCitySelection = true;
                City = city;
                _isApplyingCitySelection = false;
                _hasPickedCity = true;
                if (string.IsNullOrWhiteSpace(Name))
                    Name = city;
            }
        }

        public string? InitialCountry
        {
            get => null;
            set
            {
                var country = CountryNames.Tidy((value ?? string.Empty).Trim());
                if (TripId == 0 && country.Length > 0)
                    Country = country.Length > NameMaxLength ? country[..NameMaxLength] : country;
            }
        }

        partial void OnTripIdChanged(int value)
        {
            if (value > 0)
                RunSafeAsync(() => LoadTripAsync(value), AppResources.TripForm_LoadError).FireAndForget();
        }

        private async Task LoadTripAsync(int id)
        {
            _existingTrip = await _tripService.GetByIdAsync(id);
            if (_existingTrip is null) return;

            _isApplyingCitySelection = true;

            Name = _existingTrip.Name;
            Country = _existingTrip.Country ?? string.Empty; // NULL in rows from old versions/backups
            City = _existingTrip.City;
            _hasPickedCity = true; // the saved city stays valid until the user types another
            StartDate = _existingTrip.StartDate;
            EndDate = _existingTrip.EndDate;
            PlannedBudget = _existingTrip.PlannedBudget;
            Description = _existingTrip.Description;
            SelectedCurrency = FindCurrency(_existingTrip.EffectiveCurrencyCode);
            PageTitle = AppResources.TripForm_TitleEdit;
            if (_covers is not null && _covers.HasCover(_existingTrip))
                LoadCoverPreviewAsync(_existingTrip).FireAndForget();

            _isApplyingCitySelection = false;
        }

        // TP-101: keep the end date from ever landing before the start date. The End DatePicker's
        // MinimumDate is bound to StartDate too, but moving the start date forward past an already-picked
        // end date must also drag the end date along.
        partial void OnStartDateChanged(DateTime value)
        {
            // Same idea for the upper bound: moving the start date keeps the trip within MaxTripDays.
            var latestEnd = MaxEndFor(value);
            if (EndDate.Date < value.Date)
                EndDate = value.Date;
            else if (EndDate.Date > latestEnd)
                EndDate = latestEnd;

            ClearValidationIfFixed();
        }

        partial void OnEndDateChanged(DateTime value) => ClearValidationIfFixed();

        /// <summary>Opens the range calendar; the first tap is the start, the second the end.</summary>
        [RelayCommand]
        private async Task PickDatesAsync()
        {
            if (_dateRangePicker is null) return;

            var picked = await _dateRangePicker.PickAsync(StartDate, EndDate, AppResources.TripForm_DatesTitle);
            if (picked is not { } range) return;

            // Start first: OnStartDateChanged keeps the old end within bounds, then the picked end replaces it.
            StartDate = range.Start.Date;
            EndDate = range.End.Date < StartDate ? StartDate : range.End.Date;

            // The calendar can't cap the length, so say so right away instead of on Save.
            if ((EndDate - StartDate).Days + 1 > AppConstants.MaxTripDays)
                ValidationMessage = string.Format(AppResources.TripForm_TooLongFormat, AppConstants.MaxTripDays);
        }

        /// <summary>Trip.Name/City/Country column limit ([MaxLength(100)]); the Entries also carry MaxLength.</summary>
        internal const int NameMaxLength = 100;

        private static DateTime MaxEndFor(DateTime start) => start.Date.AddDays(AppConstants.MaxTripDays - 1);

        // The End DatePicker validates MinimumDate <= MaximumDate on every change (an invalid pair throws), and
        // its MinimumDate is bound to StartDate. So the upper bound must move BEFORE StartDate is published when
        // the start moves later, and AFTER it when the start moves earlier - never leaving Min > Max in between.
        partial void OnStartDateChanging(DateTime oldValue, DateTime newValue)
        {
            if (newValue.Date > oldValue.Date)
                MaximumEndDate = MaxEndFor(newValue);
        }

        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);

            if (e.PropertyName == nameof(StartDate) && MaximumEndDate != MaxEndFor(StartDate))
                MaximumEndDate = MaxEndFor(StartDate);
        }

        partial void OnNameChanged(string value) => ClearValidationIfFixed();

        partial void OnPlannedBudgetChanged(decimal value) => ClearValidationIfFixed();

        partial void OnCityChanged(string value)
        {
            ClearValidationIfFixed();

            if (_isApplyingCitySelection)
                return;

            _hasPickedCity = false;
            var generation = ++_citySearchGeneration;
            IsOffline = _connectivity.NetworkAccess != NetworkAccess.Internet;

            if (string.IsNullOrWhiteSpace(value) || value.Trim().Length < 2)
            {
                CitySuggestions.Clear();
                IsCitySuggestionsVisible = false;
                return;
            }

            SearchCitiesDebouncedAsync(value, generation).FireAndForget();
        }

        private async Task SearchCitiesDebouncedAsync(string query, int generation)
        {
            await Task.Delay(350); // debounce - wait for a pause in typing

            if (generation != _citySearchGeneration)
                return;

            var results = await _geocodingService.SearchCitiesAsync(query);

            if (generation != _citySearchGeneration)
                return;

            CitySuggestions.ReplaceAll(results);

            IsCitySuggestionsVisible = CitySuggestions.Count > 0;
        }

        [RelayCommand]
        private void SelectCity(CitySuggestion selected)
        {
            if (selected is null)
                return;

            _isApplyingCitySelection = true;
            City = selected.Name;
            Country = CountryNames.Tidy(selected.Country);
            _isApplyingCitySelection = false;
            _hasPickedCity = true;

            IsCitySuggestionsVisible = false;
            CitySuggestions.Clear();
        }

        /// <summary>Returns the first validation problem, or null when the form is valid.</summary>
        private string? Validate()
        {
            if (string.IsNullOrWhiteSpace(Name))
                return AppResources.TripForm_NameRequired;

            if (string.IsNullOrWhiteSpace(City))
                return AppResources.TripForm_CityRequired;

            if (!_hasPickedCity)
                return AppResources.TripForm_PickCity;

            if (EndDate.Date < StartDate.Date)
                return AppResources.TripForm_EndBeforeStart;

            if ((EndDate.Date - StartDate.Date).Days + 1 > AppConstants.MaxTripDays)
                return string.Format(AppResources.TripForm_TooLongFormat, AppConstants.MaxTripDays);

            if (PlannedBudget < 0)
                return AppResources.TripForm_NegativeBudget;

            if (PlannedBudget > InputLimits.MaxMoneyAmount)
                return string.Format(AppResources.Money_TooLargeFormat,
                    MoneyFormatter.FormatIn(InputLimits.MaxMoneyAmount, SelectedCurrency?.Value, 0));

            if ((Name ?? "").Trim().Length > NameMaxLength || (City ?? "").Trim().Length > NameMaxLength || (Country ?? "").Trim().Length > NameMaxLength)
                return string.Format(AppResources.Common_TextTooLongFormat, NameMaxLength);

            return null;
        }

        // Only clear an already-shown message once the user has actually fixed the problem,
        // so the hint doesn't flicker away on unrelated edits.
        private void ClearValidationIfFixed()
        {
            if (HasValidationError && Validate() is null)
                ValidationMessage = string.Empty;
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

            // Dates first: if the user backs out of the "activities outside the trip" question, nothing
            // (not even a currency conversion below) has been written yet.
            var realignment = await PlanActivityRealignmentAsync();
            if (realignment is { Cancelled: true })
                return;

            var newCurrency = SelectedCurrency?.Value ?? MoneyFormatter.CurrencyCode;

            // TP-403: changing the currency of a trip that already has expenses re-expresses them in the new
            // currency at today's rate - after asking, since it rewrites stored amounts.
            string? convertedCurrency = null;
            if (_existingTrip is not null &&
                !string.Equals(_existingTrip.EffectiveCurrencyCode, newCurrency, StringComparison.OrdinalIgnoreCase))
            {
                var (_, expenseCount, _, _) = await _tripService.GetRelatedCountsAsync(_existingTrip.Id);
                if (expenseCount > 0)
                {
                    var oldCurrency = _existingTrip.EffectiveCurrencyCode;
                    var confirmed = await Dialogs.ConfirmAsync(
                        AppResources.TripForm_CurrencyChangeTitle,
                        string.Format(AppResources.TripForm_CurrencyChangeMessage, expenseCount, oldCurrency, newCurrency),
                        AppResources.TripForm_CurrencyChangeAccept);
                    if (!confirmed)
                        return;

                    var quote = await _exchangeRates.GetRateAsync(oldCurrency, newCurrency);
                    if (quote is null)
                    {
                        ValidationMessage = AppResources.Fx_Unavailable;
                        return;
                    }

                    var outcome = await ConvertCurrencyAsync(_existingTrip.Id, oldCurrency, newCurrency, quote.Rate);
                    if (outcome != TripCurrencyChangeOutcome.Converted)
                    {
                        // Nothing was changed (the conversion is one transaction, on the device or on the server).
                        ValidationMessage = outcome switch
                        {
                            // A converted amount would exceed the money limit (e.g. a large EUR expense into HUF/JPY).
                            TripCurrencyChangeOutcome.TooLarge => string.Format(AppResources.Money_TooLargeFormat,
                                MoneyFormatter.FormatIn(InputLimits.MaxMoneyAmount, newCurrency, 0)),
                            TripCurrencyChangeOutcome.Offline => AppResources.TripForm_CurrencyChangeOffline,
                            TripCurrencyChangeOutcome.ChangedElsewhere => AppResources.TripForm_CurrencyChangedElsewhere,
                            _ => AppResources.Access_ReadOnly,
                        };
                        return;
                    }

                    // TP-11: the trip now has the currency the conversion stored (for a shared trip: once the
                    // server's result is synced) - the form must not set it ahead of its expenses.
                    if (_tripCurrency is not null)
                        convertedCurrency = (await _tripService.GetByIdAsync(_existingTrip.Id))?.CurrencyCode ?? newCurrency;
                }
            }

            var trip = _existingTrip ?? new Trip();
            if (_existingTrip is not null && await _tripService.GetByIdAsync(_existingTrip.Id) is { } stored)
            {
                // the cover may have changed since the form opened (uploaded by the sync, or by another device):
                // the form's old copy must not put it back
                trip.CoverPath = stored.CoverPath;
                trip.PendingCoverId = stored.PendingCoverId;
            }
            trip.Name = Name.Trim();
            trip.Country = Country.Trim();
            trip.City = City.Trim();
            trip.StartDate = StartDate.Date;
            trip.EndDate = EndDate.Date;
            trip.PlannedBudget = PlannedBudget;
            trip.CurrencyCode = convertedCurrency ?? newCurrency;
            trip.Description = InputLimits.Truncate(Description, 500);

            await _tripService.SaveAsync(trip);

            if (_covers is not null && trip.Id > 0)
            {
                if (_stagedCoverId is { } coverId)
                {
                    await _covers.SetAsync(trip, coverId);
                    _stagedCoverId = null;
                }
                else if (_removeCover)
                {
                    await _covers.RemoveAsync(trip);
                }
            }

            // Day plan follows the new dates (shifted with the trip and/or the user's choice for leftovers).
            if (realignment is { HasChanges: true } && _activityService is not null)
                await _activityService.ApplyRealignmentAsync(realignment);

            // The budget may have changed - re-check the 80% / 100% alerts (TP-404).
            if (trip.Id > 0)
                await _budgetAlerts.CheckAsync(trip.Id);

            await Shell.Current.GoToAsync("..");
        }, AppResources.TripForm_SaveError);

        // ================= Cover photo =================

        private async Task LoadCoverPreviewAsync(Trip trip)
        {
            var file = await _covers!.GetFileAsync(trip, thumbnail: false);
            if (_stagedCoverId is null && !_removeCover)
                CoverPreview = file;
        }

        /// <summary>Galeriden / Fotoğraf çek, then the user crops a photo of another shape to the cover's 16:9 (Kapağı kırp).</summary>
        [RelayCommand]
        private async Task PickCoverAsync()
        {
            if (_covers is null || _mediaPicker is null || IsCoverBusy)
                return;

            var options = new List<string> { AppResources.Attachments_PickPhoto };
            if (_mediaPicker.IsCaptureSupported)
                options.Add(AppResources.Attachments_TakePhoto);
            var choice = options.Count == 1
                ? options[0]
                : await Dialogs.ChooseAsync(AppResources.TripForm_CoverLabel, options);
            if (choice is null)
                return;

            // the picker turns the photo upright and makes it smaller than a camera original (fewer bytes to crop)
            var pickerOptions = new MediaPickerOptions
            {
                SelectionLimit = 1,
                RotateImage = true,
                MaximumWidth = 2560,
                MaximumHeight = 2560,
                CompressionQuality = 90,
            };

            FileResult? file;
            try
            {
                file = choice == AppResources.Attachments_TakePhoto
                    ? await _mediaPicker.CapturePhotoAsync(pickerOptions)
                    : (await _mediaPicker.PickPhotosAsync(pickerOptions))?.FirstOrDefault();
            }
            catch (FeatureNotSupportedException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_NotSupported);
                return;
            }
            catch (PermissionException)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.Attachments_PermissionDenied);
                return;
            }
            if (file is null)
                return;

            IsCoverBusy = true;
            PickedPhoto? picked = null;
            try
            {
                CropRegion? region = null;
                await using (var stream = await file.OpenReadAsync())
                    picked = await _covers.OpenAsync(stream);
                // a photo of another shape: the user chooses the 16:9 part (the middle one without the crop screen)
                if (_cropper is not null && !CoverCropMath.IsCoverShaped(picked.Width, picked.Height))
                {
                    IsCoverBusy = false;
                    region = await _cropper.ChooseAsync(picked);
                    if (region is null)
                        return;
                    IsCoverBusy = true;
                }

                await using var source = File.OpenRead(picked.File);
                var coverId = await _covers.PrepareAsync(source, region);
                if (_stagedCoverId is { } previous)
                    _covers.DiscardStaged(previous);
                _stagedCoverId = coverId;
                _removeCover = false;
                CoverPreview = _covers.StagedFile(coverId);
            }
            catch (Exception ex) when (ex is InvalidDataException or PlatformNotSupportedException or IOException)
            {
                System.Diagnostics.Debug.WriteLine($"[TripForm] Cover: {ex.Message}");
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.TripForm_CoverError);
            }
            finally
            {
                if (picked is not null)
                    _covers.DiscardPicked(picked);
                IsCoverBusy = false;
            }
        }

        /// <summary>Kaldır: no cover after Kaydet.</summary>
        [RelayCommand]
        private void RemoveCover()
        {
            if (_stagedCoverId is { } staged)
            {
                _covers?.DiscardStaged(staged);
                _stagedCoverId = null;
            }
            _removeCover = _existingTrip is not null && _covers?.HasCover(_existingTrip) == true;
            CoverPreview = null;
        }

        /// <summary>
        /// When an existing trip is saved, works out what happens to its day-plan activities under the new dates
        /// (see <see cref="TripDateRealignment"/>): a trip moved as a whole takes its plan along, and any activity
        /// that would end up outside the trip - including ones already stranded there by older versions - makes the
        /// user choose between moving them to the nearest trip day and deleting them. Null = nothing to do.
        /// </summary>
        private async Task<TripDateRealignment?> PlanActivityRealignmentAsync()
        {
            if (_existingTrip is null || _activityService is null)
                return null;

            var activities = await _activityService.GetByTripIdAsync(_existingTrip.Id);
            if (activities.Count == 0)
                return null;

            var realignment = TripDateRealignment.Create(
                _existingTrip.Id, _existingTrip.StartDate, _existingTrip.EndDate, StartDate, EndDate, activities);

            if (realignment.OutsideCount > 0)
            {
                var moveText = AppResources.TripForm_OutsideMove;
                var deleteText = AppResources.TripForm_OutsideDelete;
                var choice = await Dialogs.ChooseAsync(
                    string.Format(AppResources.TripForm_OutsideTitleFormat, realignment.OutsideCount),
                    new[] { moveText, deleteText });

                if (choice is null)
                    realignment.Cancelled = true;
                else
                    realignment.DeleteOutside = choice == deleteText;
            }

            return realignment;
        }

        [RelayCommand]
        private async Task CancelAsync()
        {
            if (_stagedCoverId is { } staged)
            {
                _covers?.DiscardStaged(staged);
                _stagedCoverId = null;
            }
            await Shell.Current.GoToAsync("..");
        }
    }
}
