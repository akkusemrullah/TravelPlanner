using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class DashboardPage : ContentPage
{
    private readonly DashboardViewModel _viewModel;

    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null));
        _viewModel.AttachSyncStatus(true);
#if ANDROID
        // TP-106: the status bar takes the hero's blue while Home is open
        if (Application.Current?.Resources.TryGetValue("tp-hero", out var hero) == true && hero is Color color)
            SystemBars.ApplyStatusBar(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, color);
#endif
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.AttachSyncStatus(false);
#if ANDROID
        SystemBars.Apply(Microsoft.Maui.ApplicationModel.Platform.CurrentActivity, Application.Current?.RequestedTheme == AppTheme.Dark);
#endif
    }
}
