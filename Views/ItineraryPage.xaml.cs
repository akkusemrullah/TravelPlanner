using TravelPlanner.Helpers;
using TravelPlanner.Controls;
using TravelPlanner.Models;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ItineraryPage : ContentPage
{
    /// <summary>Same as the CollectionView's ItemSpacing - part of the distance between two rows.</summary>
    private const double RowSpacing = 8;

    private readonly ItineraryViewModel _viewModel;

    // The row being dragged by its grip, and how much of the finger's movement has already been turned into moves.
    private TripActivity? _dragItem;
    private View? _dragRow;
    private double _consumedY;

    public ItineraryPage(ItineraryViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        // Reloads after returning from the activity form, keeping the selected day (TP-401).
        // Same CollectionView first-layout workaround as TripListPage.
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null), () => ActivitiesCollectionView?.InvalidateMeasure());
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

    // ---- Reorder by dragging the grip on the right of a row ----
    // While the finger is down the row swaps places with its neighbour each time the finger has travelled about one
    // row height, so the list rearranges under the finger; the order is saved once when the finger is lifted.

    private void OnHandleDragStarted(object? sender, EventArgs e)
    {
        EndDragVisuals();
        if (sender is not DragHandle { BindingContext: TripActivity activity } handle || !_viewModel.BeginReorder(activity))
            return;

        _dragItem = activity;
        _consumedY = 0;
        _dragRow = FindRow(handle);
        if (_dragRow is not null)
        {
            _dragRow.Opacity = 0.85;
            _dragRow.Scale = 1.02;
        }

        try
        {
            HapticFeedback.Default.Perform(HapticFeedbackType.Click);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Itinerary] Haptics: {ex.Message}");
        }
    }

    private void OnHandleDragMoved(object? sender, DragDeltaEventArgs e)
    {
        if (_dragItem is null)
            return;

        var rowHeight = _dragRow?.Height ?? 0;
        var step = Math.Max(rowHeight + RowSpacing, 48);
        var offset = e.TotalY - _consumedY;

        while (offset > step / 2 && _viewModel.MoveStep(_dragItem, +1))
        {
            _consumedY += step;
            offset -= step;
        }

        while (offset < -step / 2 && _viewModel.MoveStep(_dragItem, -1))
        {
            _consumedY -= step;
            offset += step;
        }
    }

    private async void OnHandleDragEnded(object? sender, EventArgs e)
    {
        var wasDragging = _dragItem is not null;
        EndDragVisuals();
        if (!wasDragging)
            return;

        // async void event handler: EndReorderAsync never throws (it shows its own error message).
        await _viewModel.EndReorderAsync();
    }

    private void EndDragVisuals()
    {
        if (_dragRow is not null)
        {
            _dragRow.Opacity = 1;
            _dragRow.Scale = 1;
        }

        _dragRow = null;
        _dragItem = null;
        _consumedY = 0;
    }

    /// <summary>The list row (the card marked ClassId="ItineraryRow" in the item template) that contains the grip.</summary>
    private static View? FindRow(Element element)
    {
        for (var current = element.Parent; current is not null; current = current.Parent)
        {
            if (current is View { ClassId: "ItineraryRow" } row)
                return row;
            if (current is CollectionView)
                break;
        }

        return element.Parent as View;
    }
}
