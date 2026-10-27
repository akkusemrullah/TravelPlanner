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

    // TP-104: the whole preview row ticks/unticks (the CheckMark is drawn, not a platform CheckBox).
    private void OnPreviewRowTapped(object? sender, TappedEventArgs e)
    {
        switch ((sender as BindableObject)?.BindingContext)
        {
            case AiPreviewActivity activity:
                activity.IsSelected = !activity.IsSelected;
                break;
            case AiPreviewPlace place:
                place.IsSelected = !place.IsSelected;
                break;
            case AiPreviewTodo todo:
                todo.IsSelected = !todo.IsSelected;
                break;
        }
    }

    // TP-57: the plan goes on without the page; a notification says when it is ready
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Leave();
    }
}
