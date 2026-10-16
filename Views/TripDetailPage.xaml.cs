using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class TripDetailPage : ContentPage
{
    private readonly TripDetailViewModel _viewModel;

    public TripDetailPage(TripDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Refresh budget totals in case an expense/place/task changed on a sub-page.
        this.LoadOnAppearing(() => _viewModel.RefreshCommand.Execute(null));
    }
}
