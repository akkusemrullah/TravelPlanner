using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.AttachSyncStatus(true);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.AttachSyncStatus(false);
    }

    // No OnDisappearing theme revert any more: the theme is saved the moment it is picked. Changing the theme
    // from OnDisappearing ran in the middle of Shell's tab switch and could crash the app on Android.
}
