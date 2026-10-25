using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>AI trip planner. Route Routes.AiPlanner, query TripId.</summary>
public partial class AiPlannerPage : ContentPage
{
    private readonly AiPlannerViewModel _viewModel;

    public AiPlannerPage(AiPlannerViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadCommand.Execute(null); // also after coming back from the sign-in page
    }

    // TP-57: the plan goes on without the page; a notification says when it is ready
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Leave();
    }
}
