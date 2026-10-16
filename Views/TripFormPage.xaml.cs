using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class TripFormPage : ContentPage
{
    public TripFormPage(TripFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
