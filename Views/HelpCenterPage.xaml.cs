using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Yardım merkezi (route Routes.HelpCenter): FAQ, contact and the legal documents.</summary>
public partial class HelpCenterPage : ContentPage
{
    public HelpCenterPage(HelpCenterViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
