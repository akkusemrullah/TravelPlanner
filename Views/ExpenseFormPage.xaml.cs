using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ExpenseFormPage : ContentPage
{
    public ExpenseFormPage(ExpenseFormViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    // TP-104: the whole row ticks the person (the CheckMark itself is drawn, not a platform CheckBox).
    private void OnParticipantTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is SplitParticipantOption option)
            option.IsSelected = !option.IsSelected;
    }
}
