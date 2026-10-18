using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Keşfet tab (TP-33): sample trips. Route "Explore" (a TabBar ShellContent).</summary>
public partial class ExplorePage : ContentPage
{
    private readonly ExploreViewModel _viewModel;

    public ExplorePage(ExploreViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Same CollectionView width workaround as the trip list (see TripListPage).
        this.LoadOnAppearing(() => _viewModel.StartAsync().FireAndForget(), () => CardsView?.InvalidateMeasure());
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Pause(); // requests still running are cancelled
    }

    // Hardware/gesture back first closes the filter sheet.
    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.IsFilterOpen)
        {
            _viewModel.IsFilterOpen = false;
            return true;
        }
        return base.OnBackButtonPressed();
    }
}
