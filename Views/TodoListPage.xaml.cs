using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class TodoListPage : ContentPage
{
    private readonly TodoListViewModel _viewModel;

    public TodoListPage(TodoListViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        // Re-measure once rows have rendered so variable-height items lay out correctly.
        this.LoadOnAppearing(() => _viewModel.LoadTasksCommand.Execute(null), () => TasksCollectionView?.InvalidateMeasure());
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

    // TP-105: the CheckBox is bound OneWay, so CheckedChanged also fires when a row is created/recycled
    // and the binding pushes the stored value in. Those events carry a value equal to what's already on
    // the task - only a real user toggle differs, and only that gets saved.
    private void OnTaskCheckedChanged(object? sender, CheckedChangedEventArgs e)
    {
        if (sender is not CheckBox { BindingContext: TodoItem task })
            return;

        if (task.IsCompleted == e.Value)
            return;

        _viewModel.SetCompletedCommand.Execute(new TodoCompletionChange(task, e.Value));
    }
}
