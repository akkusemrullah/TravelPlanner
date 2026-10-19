using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Profil → Tercihler: name, currency, reminder time, theme and language.</summary>
public partial class PreferencesPage : ContentPage
{
    public PreferencesPage(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }
}
