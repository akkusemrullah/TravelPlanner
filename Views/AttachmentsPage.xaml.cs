using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class AttachmentsPage : ContentPage
{
    private readonly AttachmentsViewModel _viewModel;

    public AttachmentsPage(AttachmentsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null), () => AttachmentsCollectionView?.InvalidateMeasure());
    }

    // Hardware/gesture back first leaves "Seçerek sil" mode instead of the page.
    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.Selection.IsActive)
        {
            _viewModel.Selection.Cancel();
            return true;
        }

        return base.OnBackButtonPressed();
    }
}
