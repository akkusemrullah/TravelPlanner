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
    /// <summary>A participant checkbox on the expense form (TP-504).</summary>
    public partial class SplitParticipantOption : ObservableObject
    {
        public SplitParticipantOption(Participant participant)
        {
            Participant = participant;
        }

        public Participant Participant { get; }
        public string Name => Participant.Name;

        [ObservableProperty]
        private bool isSelected;
    }

    [QueryProperty(nameof(TripId), "TripId")]
    [QueryProperty(nameof(ExpenseId), "ExpenseId")]
    public partial class ExpenseFormViewModel : ViewModelBase
    {
        private readonly IExpenseService _expenseService;
        private readonly ITripService _tripService;
        private readonly IExchangeRateService _exchangeRates;
        private readonly IBudgetAlertService _budgetAlerts;
        private readonly ISplitService _splitService;
        private Expense? _existingExpense;
        private bool _participantsLoaded;
        private bool _splitApplied;
        private Trip? _trip;

        // Bumped on every amount/currency/date change; a rate lookup only shows its preview if still the latest.
        private int _previewGeneration;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        private int expenseId;

        [ObservableProperty]
        private ExpenseCategory category = ExpenseCategory.Other;

        /// <summary>The amount as typed, in <see cref="SelectedCurrency"/>.</summary>
        [ObservableProperty]
        private decimal amount;

        [ObservableProperty]
        private DateTime date = DateTime.Today;

        [ObservableProperty]
        private string? description;

        /// <summary>Currency the amount is entered in (TP-403). Defaults to the trip's budget currency.</summary>
        [ObservableProperty]
        private SettingsOption<string>? selectedCurrency;

        public IReadOnlyList<SettingsOption<string>> Currencies { get; } = MoneyFormatter.SupportedCurrencies
            .Select(code => new SettingsOption<string>(code, MoneyFormatter.DisplayName(code)))
            .ToList();

        /// <summary>"≈ ₺1.850,00 · 1 EUR = 37,0000 TRY (07.10.2026)" when converting; empty otherwise.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasConversionPreview))]
        private string conversionPreview = string.Empty;

        public bool HasConversionPreview => !string.IsNullOrEmpty(ConversionPreview);

        /// <summary>Validation message shown above the Save button (TP-102). Empty = no error.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasValidationError))]
        private string validationMessage = string.Empty;

        public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

        /// <summary>Non-blocking hint when the expense date falls outside the trip's dates (TP-308). Empty = fine.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasDateWarning))]
        private string dateWarning = string.Empty;

        public bool HasDateWarning => !string.IsNullOrEmpty(DateWarning);

        public List<ExpenseCategory> Categories { get; } = Enum.GetValues<ExpenseCategory>().ToList();

        // ---- Cost split (TP-504) ----

        /// <summary>"Bölüşülmesin" followed by the trip's participants.</summary>
        public ObservableCollection<SettingsOption<int?>> PayerOptions { get; } = new();

        public ObservableCollection<SplitParticipantOption> SplitParticipants { get; } = new();

        [ObservableProperty]
        private SettingsOption<int?>? selectedPayer;

        /// <summary>True once the trip has participants - the split section is shown only then.</summary>
        [ObservableProperty]
        private bool hasParticipants;

        /// <summary>A payer is chosen, so the "split between" checkboxes matter.</summary>
        [ObservableProperty]
        private bool isSplitting;

        public ExpenseFormViewModel(
            IExpenseService expenseService,
            ITripService tripService,
            IExchangeRateService exchangeRates,
            IBudgetAlertService budgetAlerts,
            ISplitService splitService,
            IDialogService dialogs)
            : base(dialogs)
        {
            _expenseService = expenseService;
            _tripService = tripService;
            _exchangeRates = exchangeRates;
            _budgetAlerts = budgetAlerts;
            _splitService = splitService;

            SelectedCurrency = FindCurrency(MoneyFormatter.CurrencyCode);
        }

        /// <summary>The trip's budget currency - every stored amount is in this currency.</summary>
        private string TripCurrency => _trip?.EffectiveCurrencyCode ?? _existingExpense?.TripCurrencyCode ?? MoneyFormatter.CurrencyCode;

        private string EnteredCurrency => SelectedCurrency?.Value ?? TripCurrency;

        private bool NeedsConversion => !string.Equals(EnteredCurrency, TripCurrency, StringComparison.OrdinalIgnoreCase);

        private SettingsOption<string> FindCurrency(string? code) =>
            Currencies.FirstOrDefault(c => string.Equals(c.Value, MoneyFormatter.Normalize(code), StringComparison.OrdinalIgnoreCase)) ?? Currencies[0];

        partial void OnTripIdChanged(int value)
        {
            if (value > 0)
                RunSafeAsync(() => LoadTripAsync(value)).FireAndForget();
        }

        partial void OnDateChanged(DateTime value)
        {
            UpdateDateWarning();
            UpdateConversionPreview();
        }

        partial void OnSelectedCurrencyChanged(SettingsOption<string>? value) => UpdateConversionPreview();

        private async Task LoadTripAsync(int tripId)
        {
            _trip = await _tripService.GetByIdAsync(tripId);

            // A new expense defaults to today; for a trip that hasn't started yet (or is already over)
            // the trip's first day is a better starting point.
            if (_existingExpense is null && ExpenseId == 0 && _trip is not null && !_trip.IsOngoing)
                Date = _trip.StartDate.Date;

            // New expenses start in the trip's currency; an edited one keeps the currency it was entered in.
            if (_existingExpense is null && ExpenseId == 0)
                SelectedCurrency = FindCurrency(TripCurrency);

            UpdateDateWarning();
            UpdateConversionPreview();

            await LoadParticipantsAsync(tripId);
        }

        private async Task LoadParticipantsAsync(int tripId)
        {
            var participants = await _splitService.GetParticipantsAsync(tripId);

            PayerOptions.Clear();
            PayerOptions.Add(new SettingsOption<int?>(null, AppResources.Split_NoPayer));
            SplitParticipants.Clear();
            foreach (var participant in participants)
            {
                PayerOptions.Add(new SettingsOption<int?>(participant.Id, participant.Name));
                SplitParticipants.Add(new SplitParticipantOption(participant));
            }

            HasParticipants = participants.Count > 0;
            SelectedPayer ??= PayerOptions[0];
            _participantsLoaded = true;
            await ApplyExistingSplitAsync();
        }

        /// <summary>Shows an edited expense's payer and participants once both it and the participant list are loaded.</summary>
        private async Task ApplyExistingSplitAsync()
        {
            if (_splitApplied || !_participantsLoaded || _existingExpense is null)
                return;
            _splitApplied = true;

            var shareIds = await _splitService.GetShareParticipantIdsAsync(_existingExpense.Id);
            foreach (var option in SplitParticipants)
                option.IsSelected = shareIds.Contains(option.Participant.Id);

            SelectedPayer = PayerOptions.FirstOrDefault(p => p.Value == _existingExpense.PaidByParticipantId) ?? PayerOptions.FirstOrDefault();
        }

        partial void OnSelectedPayerChanged(SettingsOption<int?>? value)
        {
            IsSplitting = value?.Value is not null;

            // Choosing a payer for the first time: start with everyone selected - the usual case.
            if (IsSplitting && SplitParticipants.All(p => !p.IsSelected))
            {
                foreach (var option in SplitParticipants)
                    option.IsSelected = true;
            }

            if (HasValidationError && ValidationMessage == AppResources.Split_ParticipantsRequired)
                ValidationMessage = string.Empty;
        }

        private void UpdateDateWarning()
        {
            DateWarning = _trip is not null && (Date.Date < _trip.StartDate.Date || Date.Date > _trip.EndDate.Date)
                ? string.Format(AppResources.Expense_DateOutsideTrip, _trip.CompactDateRangeDisplay)
                : string.Empty;
        }

        partial void OnExpenseIdChanged(int value)
        {
            if (value > 0)
                RunSafeAsync(() => LoadExpenseAsync(value), AppResources.Expense_LoadError).FireAndForget();
        }

        partial void OnAmountChanged(decimal value)
        {
            if (HasValidationError && value > 0)
                ValidationMessage = string.Empty;

            UpdateConversionPreview();
        }

        private async Task LoadExpenseAsync(int id)
        {
            _existingExpense = await _expenseService.GetByIdAsync(id);
            if (_existingExpense is null) return;

            await ApplyExistingSplitAsync();

            Category = _existingExpense.Category;
            Date = _existingExpense.Date;
            Description = _existingExpense.Description;

            // Show what the user originally typed (e.g. €50), not the converted trip-currency amount.
            if (!string.IsNullOrWhiteSpace(_existingExpense.OriginalCurrencyCode) && _existingExpense.OriginalAmountMinor > 0)
            {
                SelectedCurrency = FindCurrency(_existingExpense.OriginalCurrencyCode);
                Amount = _existingExpense.OriginalAmount;
            }
            else
            {
                SelectedCurrency = FindCurrency(_existingExpense.TripCurrencyCode);
                Amount = _existingExpense.Amount;
            }
        }

        private void UpdateConversionPreview()
        {
            var generation = ++_previewGeneration;

            if (!NeedsConversion || Amount <= 0 || Amount > InputLimits.MaxMoneyAmount)
            {
                ConversionPreview = string.Empty;
                return;
            }

            UpdateConversionPreviewAsync(generation, Amount, EnteredCurrency, TripCurrency, Date).FireAndForget();
        }

        private async Task UpdateConversionPreviewAsync(int generation, decimal value, string from, string to, DateTime day)
        {
            await Task.Delay(300); // debounce typing
            if (generation != _previewGeneration)
                return;

            var quote = await _exchangeRates.GetRateAsync(from, to, day);
            if (generation != _previewGeneration)
                return;

            ConversionPreview = quote is null
                ? AppResources.Fx_Unavailable
                : FormatPreview(quote, value);
        }

        internal static string FormatPreview(ExchangeRateQuote quote, decimal value) =>
            string.Format(CultureInfo.CurrentCulture, AppResources.Expense_ConversionPreviewFormat,
                MoneyFormatter.FormatIn(quote.Convert(value), quote.To),
                quote.From,
                quote.Rate.ToString("0.####", CultureInfo.CurrentCulture),
                quote.To,
                quote.RateDate.ToString("d", CultureInfo.CurrentCulture));

        [RelayCommand]
        private Task SaveAsync() => RunSafeAsync(async () =>
        {
            if (Amount <= 0)
            {
                ValidationMessage = AppResources.Expense_AmountRequired;
                return;
            }

            if (Amount > InputLimits.MaxMoneyAmount)
            {
                ValidationMessage = AmountTooLargeMessage(EnteredCurrency);
                return;
            }

            var payerId = HasParticipants ? SelectedPayer?.Value : null;
            var splitIds = SplitParticipants.Where(p => p.IsSelected).Select(p => p.Participant.Id).ToList();
            if (payerId is not null && splitIds.Count == 0)
            {
                ValidationMessage = AppResources.Split_ParticipantsRequired;
                return;
            }

            ValidationMessage = string.Empty;

            // Saved before the trip finished loading (a very quick tap): without it TripCurrency would fall back to
            // the default currency and a € amount on a ₺ trip would be stored unconverted.
            if (_trip is null && TripId > 0)
                _trip = await _tripService.GetByIdAsync(TripId);

            var tripCurrency = TripCurrency;
            var enteredCurrency = EnteredCurrency;
            var tripAmount = Amount;
            var rate = 1m;

            if (NeedsConversion && IsUnchangedConvertedExpense(enteredCurrency))
            {
                // Only category/description changed: keep the stored conversion instead of re-pricing it at a
                // newer rate (and don't require a rate - this also works offline).
                rate = (decimal)_existingExpense!.ExchangeRate;
                tripAmount = _existingExpense.Amount;
            }
            else if (NeedsConversion)
            {
                var quote = await _exchangeRates.GetRateAsync(enteredCurrency, tripCurrency, Date);
                if (quote is null)
                {
                    ValidationMessage = AppResources.Fx_Unavailable;
                    return;
                }

                rate = quote.Rate;
                tripAmount = quote.Convert(Amount);
            }

            // Converting into a "smaller" currency (e.g. EUR -> HUF) multiplies the amount - check the result too.
            if (tripAmount <= 0 || tripAmount > InputLimits.MaxMoneyAmount)
            {
                ValidationMessage = tripAmount <= 0 ? AppResources.Expense_AmountRequired : AmountTooLargeMessage(tripCurrency);
                return;
            }

            var expense = _existingExpense ?? new Expense { TripId = TripId };
            expense.Category = Category;
            expense.Amount = tripAmount;
            expense.OriginalAmount = Amount;
            expense.OriginalCurrencyCode = enteredCurrency;
            expense.ExchangeRate = (double)rate;
            expense.Date = Date;
            expense.Description = Description;

            await _expenseService.SaveAsync(expense);

            // TP-504: who paid and who shares it (an unsplit expense clears any earlier split).
            if (HasParticipants)
                await _splitService.SaveSplitAsync(expense, payerId, payerId is null ? Array.Empty<int>() : splitIds);

            await _budgetAlerts.CheckAsync(expense.TripId); // TP-404: 80% / 100% notifications
            await Shell.Current.GoToAsync("..");
        }, AppResources.Expense_SaveError);

        private static string AmountTooLargeMessage(string currency) =>
            string.Format(AppResources.Money_TooLargeFormat, MoneyFormatter.FormatIn(InputLimits.MaxMoneyAmount, currency, 0));

        private bool IsUnchangedConvertedExpense(string enteredCurrency) =>
            _existingExpense is not null &&
            InputLimits.IsValidRate(_existingExpense.ExchangeRate) && // (decimal)NaN/huge double would throw
            string.Equals(_existingExpense.OriginalCurrencyCode, enteredCurrency, StringComparison.OrdinalIgnoreCase) &&
            _existingExpense.OriginalAmount == Amount &&
            _existingExpense.Date.Date == Date.Date &&
            string.Equals(_existingExpense.TripCurrencyCode, TripCurrency, StringComparison.OrdinalIgnoreCase);

        [RelayCommand]
        private async Task CancelAsync() => await Shell.Current.GoToAsync("..");
    }
}
