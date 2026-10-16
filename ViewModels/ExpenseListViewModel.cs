using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class ExpenseListViewModel : ViewModelBase, ISelectableListViewModel
    {
        /// <summary>Sentinel value for the category filter Picker meaning "no filter".</summary>
        private const string AllCategoriesFilter = "All";

        private readonly IExpenseService _expenseService;
        private readonly IBudgetAlertService _budgetAlerts;
        private readonly ITripService? _trips;
        private readonly ISplitService? _split;
        private readonly IShare? _share;

        /// <summary>Every expense for the trip, unfiltered - the source ApplyFilter() projects from.</summary>
        private List<Expense> _allExpenses = new();

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalExpensesFormatted))]
        private decimal totalExpenses;

        /// <summary>Trip currency of the loaded expenses (TP-403).</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalExpensesFormatted))]
        private string currencyCode = MoneyFormatter.CurrencyCode;

        public string TotalExpensesFormatted => MoneyFormatter.FormatIn(TotalExpenses, CurrencyCode);

        [ObservableProperty]
        private string selectedCategoryFilter = AllCategoriesFilter;

        public List<string> CategoryFilterOptions { get; } =
            new[] { AllCategoriesFilter }.Concat(Enum.GetNames<ExpenseCategory>()).ToList();

        public ObservableCollection<Expense> Expenses { get; } = new RangeObservableCollection<Expense>();

        /// <summary>"Seçerek sil" mode of the list (works on the expenses shown - i.e. within the category filter).</summary>
        public ListSelection<Expense> Selection { get; }

        IListSelection ISelectableListViewModel.Selection => Selection;

        /// <summary>The app bar's trash button: only with something to delete and the right to delete it.</summary>
        public bool ShowDeleteMenu => CanEdit && HasExpenses;

        /// <summary>The add button is out of the way while rows are being ticked.</summary>
        public bool ShowAddButton => CanEdit && !Selection.IsActive;

        [ObservableProperty]
        private bool hasExpenses;

        [ObservableProperty]
        private bool hasNoExpenses; // false until the first load has run, so the empty state never flashes

        /// <param name="trips">The trip's currency even while the list is empty, and its name for the export file.</param>
        /// <param name="split">Payer names for the CSV export (TP-504).</param>
        /// <param name="share">Share sheet for the CSV export. The last three are optional so older call sites keep working.</param>
        public ExpenseListViewModel(IExpenseService expenseService, IBudgetAlertService budgetAlerts, IDialogService dialogs,
                                    ITripService? trips = null, ISplitService? split = null, IShare? share = null)
            : base(dialogs)
        {
            _expenseService = expenseService;
            _budgetAlerts = budgetAlerts;
            _trips = trips;
            _split = split;
            _share = share;
            Selection = new ListSelection<Expense>(() => Expenses);
            Selection.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Selection.IsActive))
                    OnPropertyChanged(nameof(ShowAddButton));
            };
        }

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is nameof(CanEdit) or nameof(HasExpenses))
            {
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowDeleteMenu)));
                base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(ShowAddButton)));
            }
        }

        /// <summary>Folder the CSV export is written to before sharing. Replaced in tests.</summary>
        internal Func<string> ExportDirectory { get; set; } = () => FileSystem.CacheDirectory;

        /// <summary>The export button is only offered when there is something to export and a share sheet.</summary>
        public bool CanExport => _share is not null;

        /// <summary>Whole trip, not just the filtered category - the export always contains every expense.</summary>
        public bool ShowExport => CanExport && _allExpenses.Count > 0;

        // NOTE (TP-104): no OnTripIdChanged -> load here any more. ExpenseListPage.OnAppearing runs
        // LoadExpensesCommand (Shell applies the TripId query before the page appears), and doing it from
        // both places raced and could show every expense twice.

        partial void OnSelectedCategoryFilterChanged(string value) => ApplyFilter();

        [RelayCommand]
        private Task LoadExpensesAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            await RefreshAccessAsync(TripId); // G6: viewers don't get add/edit buttons

            _allExpenses = await _expenseService.GetByTripIdAsync(TripId);

            // The trip's own currency - also when it has no expenses yet (the total then reads "€0,00", not "₺0,00").
            var tripCurrency = _trips is null ? null : (await _trips.GetByIdAsync(TripId))?.EffectiveCurrencyCode;
            CurrencyCode = tripCurrency ?? _allExpenses.FirstOrDefault()?.TripCurrencyCode ?? CurrencyCode;
            ApplyFilter();
        }, AppResources.ExpenseList_LoadError);

        private void ApplyFilter()
        {
            var filtered = SelectedCategoryFilter == AllCategoriesFilter
                ? _allExpenses
                : _allExpenses.Where(e => e.Category.ToString() == SelectedCategoryFilter).ToList();

            Expenses.ReplaceAll(filtered);

            TotalExpenses = filtered.Sum(e => e.Amount);

            HasExpenses = Expenses.Count > 0;
            HasNoExpenses = !HasExpenses;
            OnPropertyChanged(nameof(ShowExport));
            Selection?.Sync(); // null only while the constructor sets the default filter
        }

        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        [RelayCommand]
        private async Task GoToAnalysisAsync()
        {
            await Shell.Current.GoToAsync($"{Routes.ExpenseAnalysis}?TripId={TripId}");
        }

        [RelayCommand]
        private async Task GoToAddExpenseAsync()
        {
            await Shell.Current.GoToAsync($"{Routes.ExpenseForm}?TripId={TripId}");
        }

        [RelayCommand]
        private async Task GoToEditExpenseAsync(Expense expense)
        {
            if (expense is null) return;
            await Shell.Current.GoToAsync($"{Routes.ExpenseForm}?TripId={TripId}&ExpenseId={expense.Id}");
        }

        /// <summary>
        /// Shares every expense of the trip as a CSV file (opens in Excel / Google Sheets): date, category,
        /// description, amount in the trip currency, the amount and currency as typed, the rate and who paid.
        /// </summary>
        [RelayCommand]
        private Task ExportCsvAsync() => RunSafeAsync(async () =>
        {
            if (TripId == 0 || _share is null) return;

            var expenses = await _expenseService.GetByTripIdAsync(TripId);
            if (expenses.Count == 0)
            {
                await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.ExpenseList_ExportEmpty);
                return;
            }

            var trip = _trips is null ? null : await _trips.GetByIdAsync(TripId);
            var currency = trip?.EffectiveCurrencyCode ?? expenses[0].TripCurrencyCode ?? CurrencyCode;
            var payers = _split is null
                ? new Dictionary<int, string>()
                : (await _split.GetParticipantsAsync(TripId)).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First().Name);

            var csv = ExpenseCsv.Build(expenses.OrderBy(e => e.Date).ThenBy(e => e.Id), currency, payers);

            var directory = ExportDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ExpenseCsv.FileName(trip?.Name));
            await File.WriteAllTextAsync(path, csv, ExpenseCsv.FileEncoding);

            await _share.RequestAsync(new ShareFileRequest
            {
                Title = AppResources.ExpenseList_Export,
                File = new ShareFile(path, "text/csv"),
            });
        }, AppResources.ExpenseList_ExportError);

        // ================= Selecting rows and deleting them together =================

        /// <summary>A tap on a row: opens the expense, or ticks it in selection mode.</summary>
        [RelayCommand]
        private async Task ExpenseTappedAsync(Expense? expense)
        {
            if (expense is null) return;

            if (Selection.IsActive)
                Selection.Toggle(expense);
            else
                await GoToEditExpenseAsync(expense);
        }

        [RelayCommand]
        private Task DeleteSelectedAsync() =>
            RunSafeAsync(() => DeleteManyAsync(Selection.SelectedItems, Selection.DeleteTitle), AppResources.ExpenseList_DeleteError);

        private async Task DeleteManyAsync(IReadOnlyList<Expense> expenses, string title)
        {
            if (expenses.Count == 0)
                return;

            bool confirmed;
            if (expenses.Count == 1)
            {
                var expense = expenses[0];
                var label = string.IsNullOrWhiteSpace(expense.Description)
                    ? expense.FormattedAmount
                    : $"{expense.Description} ({expense.FormattedAmount})";
                confirmed = await Dialogs.ConfirmAsync(AppResources.ExpenseList_DeleteTitle, string.Format(AppResources.ExpenseList_DeleteMessage, label));
            }
            else
            {
                confirmed = await ConfirmBulkDeleteAsync(title, string.Format(AppResources.TripDelete_ExpensesFormat, expenses.Count));
            }

            if (!confirmed)
                return;

            var deletedAny = false;
            try
            {
                await DeleteEachAsync(expenses, e => _expenseService.DeleteAsync(e), e =>
                {
                    _allExpenses.Remove(e);
                    deletedAny = true;
                });
            }
            finally
            {
                Selection.Cancel();
                ApplyFilter();
            }

            // Spending went down - re-arms the 80% / 100% alerts if it dropped below them (TP-404).
            if (deletedAny)
                await _budgetAlerts.CheckAsync(TripId);
        }
    }
}
