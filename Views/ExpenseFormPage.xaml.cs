using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ExpenseFormPage : ContentPage
{
    public ExpenseFormPage(ExpenseFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
