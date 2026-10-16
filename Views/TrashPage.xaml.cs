using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Deleted items of the last 30 days (G6). Route Routes.Trash.</summary>
public partial class TrashPage : ContentPage
{
    private readonly TrashViewModel _viewModel;

    public TrashPage(TrashViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null));
    }
}
