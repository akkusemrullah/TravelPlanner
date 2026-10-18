using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>One Keşfet sample trip (TP-33). Route Routes.ExploreDetail, query Id (the sample's uuid).</summary>
public partial class ExploreDetailPage : ContentPage
{
    private readonly ExploreDetailViewModel _viewModel;

    public ExploreDetailPage(ExploreDetailViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.LoadCommand.Execute(null); // loads once; coming back from the map keeps the page as it was
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _viewModel.Cancel();
    }

    // Hardware/gesture back first closes the sheet / leaves "Düzenle ve ekle".
    protected override bool OnBackButtonPressed() => _viewModel.HandleBack() || base.OnBackButtonPressed();
}
