using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Members and invites of a trip (G6). Route Routes.Members, query TripId.</summary>
public partial class MembersPage : ContentPage
{
    private readonly MembersViewModel _viewModel;

    public MembersPage(MembersViewModel viewModel)
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
