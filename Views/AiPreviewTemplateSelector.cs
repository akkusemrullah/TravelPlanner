using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Picks the row template of the AI planner's preview list by row type (AiPlannerViewModel.PreviewRows).</summary>
public sealed class AiPreviewTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Section { get; set; }
    public DataTemplate? Day { get; set; }
    public DataTemplate? Activity { get; set; }
    public DataTemplate? Place { get; set; }
    public DataTemplate? Todo { get; set; }
    public DataTemplate? Tip { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        AiPreviewSection => Section!,
        AiPreviewDay => Day!,
        AiPreviewActivity => Activity!,
        AiPreviewPlace => Place!,
        AiPreviewTodo => Todo!,
        _ => Tip!,
    };
}
