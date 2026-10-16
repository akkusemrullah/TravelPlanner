using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ExpenseListPage : ContentPage
{
    private readonly ExpenseListViewModel _viewModel;

    public ExpenseListPage(ExpenseListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.LoadOnAppearing(() => _viewModel.LoadExpensesCommand.Execute(null), () => ExpensesCollectionView?.InvalidateMeasure());

        // Works around a known MAUI/Android CollectionView bug - see TripListPage.xaml.cs's OnAppearing
        // comment for the full explanation. Same fix here: force one remeasure shortly after the page
        // has had time to finish appearing, so cards don't get stuck narrower than the screen the first
        // time this page is shown.
    }

    // Hardware/gesture back first leaves "Seçerek sil" mode instead of the page.
    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.Selection.IsActive)
        {
            _viewModel.Selection.Cancel();
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
