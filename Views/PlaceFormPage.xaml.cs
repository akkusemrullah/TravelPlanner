using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class PlaceFormPage : ContentPage
{
    public PlaceFormPage(PlaceFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
