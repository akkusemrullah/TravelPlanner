using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class TripListPage : ContentPage
{
    private readonly TripListViewModel _viewModel;

    public TripListPage(TripListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.LoadOnAppearing(() => _viewModel.LoadTripsCommand.Execute(null), () => TripsCollectionView?.InvalidateMeasure());

        // Works around a known MAUI/Android CollectionView bug: the very first time this page appears,
        // its cards get bound and measured while the Shell push transition (and the native RecyclerView
        // underneath CollectionView) haven't fully settled to the page's final width yet, so every card
        // ends up narrower than the screen with a gap on the right - the layout is never recalculated
        // after that. Navigating away (e.g. to "Seyahat Ekle") and back forces a brand-new layout pass,
        // which is exactly why that "fixes" it. Nudging one InvalidateMeasure() shortly after the page
        // has had time to finish appearing reproduces that same fresh layout pass without a real
        // navigation. PageLoading runs it after the load, never during a page transition.
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
