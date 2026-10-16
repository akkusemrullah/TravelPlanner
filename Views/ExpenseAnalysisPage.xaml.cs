using TravelPlanner.Helpers;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class ExpenseAnalysisPage : ContentPage
{
    private readonly ExpenseAnalysisViewModel _viewModel;

    public ExpenseAnalysisPage(ExpenseAnalysisViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.ChartsUpdated += OnChartsUpdated;
        this.LoadOnAppearing(() => _viewModel.LoadCommand.Execute(null));
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.ChartsUpdated -= OnChartsUpdated;
    }

    // The drawables are the same objects with new data, so the GraphicsViews must be told to repaint.
    private void OnChartsUpdated(object? sender, EventArgs e) =>
        Dispatcher.Dispatch(() =>
        {
            CategoryChartView.Invalidate();
            DailyChartView.Invalidate();
        });
}
