using TravelPlanner.Helpers;
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

    // The legal documents open on top of this modal page (a Shell route can't be pushed over a modal).
    private void OnTermsTapped(object? sender, TappedEventArgs e) => OpenDocument(LegalDocuments.TermsKey);

    private void OnPrivacyTapped(object? sender, TappedEventArgs e) => OpenDocument(LegalDocuments.PrivacyKey);

    private void OnKvkkTapped(object? sender, TappedEventArgs e) => OpenDocument(LegalDocuments.KvkkKey);

    private async void OpenDocument(string key)
    {
        try
        {
            await PrivacyPage.ShowModalAsync(Navigation, key);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AuthPage] Opening {key} failed: {ex}");
        }
    }
}
