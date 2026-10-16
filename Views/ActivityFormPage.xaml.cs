using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ActivityFormPage : ContentPage
{
    private readonly ActivityFormViewModel _viewModel;

    public ActivityFormPage(ActivityFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Shell has applied TripId / ActivityId / Date by now, so one load sees all of them (TP-104 pattern).
        _viewModel.LoadCommand.Execute(null);
    }
}
