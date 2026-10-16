using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Sign in / create account (G3). Opened as a Shell modal (route Routes.Auth).</summary>
public partial class AuthPage : ContentPage
{
    public AuthPage(AuthViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
