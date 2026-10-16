using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class SplitPage : ContentPage
{
    private readonly SplitViewModel _viewModel;

    public SplitPage(SplitViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Reloads after returning from the expense list, where payers / participants may have changed.
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null));
    }
}
