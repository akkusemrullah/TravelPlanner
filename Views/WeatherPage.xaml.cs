using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class WeatherPage : ContentPage
{
    public WeatherPage(WeatherViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
