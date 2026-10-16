namespace TravelPlanner.Views;

public partial class PrivacyPage : ContentPage
{
    public PrivacyPage()
    {
        InitializeComponent();
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PrivacyPage] Back navigation failed: {ex}");
        }
    }
}
