using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>
/// Shows one legal document (privacy policy, KVKK notice, terms of use, licences) - route
/// <see cref="Routes.Privacy"/>, query "doc" (<see cref="LegalDocuments"/>). Can also be shown modally on top of
/// the sign-in page (<see cref="ShowModalAsync"/>).
/// </summary>
[QueryProperty(nameof(DocumentKey), "doc")]
public partial class PrivacyPage : ContentPage
{
    private readonly LegalDocumentViewModel _viewModel;

    public PrivacyPage(LegalDocumentViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        UpdateHeaderIcon();
    }

    /// <summary>"privacy", "kvkk", "terms" or "licenses".</summary>
    public string? DocumentKey
    {
        get => LegalDocuments.Key(_viewModel.Kind);
        set
        {
            _viewModel.Load(LegalDocuments.FromKey(value));
            UpdateHeaderIcon();
        }
    }

    /// <summary>Opens a document over a modal page (the sign-in page), where Shell routes can't be pushed.</summary>
    public static async Task ShowModalAsync(INavigation navigation, string documentKey)
    {
        var services = IPlatformApplication.Current?.Services;
        if (services?.GetService(typeof(PrivacyPage)) is not PrivacyPage page)
            return;

        page.DocumentKey = documentKey;
        await navigation.PushModalAsync(page);
    }

    private void UpdateHeaderIcon()
    {
        var key = _viewModel.Kind switch
        {
            LegalDocumentKind.Kvkk => "Ic.Shield",
            LegalDocumentKind.Terms => "Ic.Scale",
            LegalDocumentKind.Licenses => "Ic.FileText",
            _ => "Ic.Lock",
        };

        if (Application.Current?.Resources.TryGetValue(key, out var glyph) == true && glyph is string text)
            HeaderIcon.Text = text;
    }

    private async void OnBackTapped(object? sender, TappedEventArgs e)
    {
        try
        {
            if (Navigation.ModalStack.Count > 0 && ReferenceEquals(Navigation.ModalStack[^1], this))
                await Navigation.PopModalAsync();
            else
                await Shell.Current.GoToAsync("..");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PrivacyPage] Back navigation failed: {ex}");
        }
    }
}
