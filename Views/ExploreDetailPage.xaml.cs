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

    // TP-105: in "Düzenle ve ekle" a tap anywhere on the row ticks / unticks it.
    private void OnRowTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel.IsEditing && (sender as BindableObject)?.BindingContext is ExploreSelectableRow row)
            row.IsSelected = !row.IsSelected;
    }

    // Hardware/gesture back first closes the sheet / leaves "Düzenle ve ekle".
    protected override bool OnBackButtonPressed() => _viewModel.HandleBack() || base.OnBackButtonPressed();
}
