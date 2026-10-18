using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

/// <summary>Picks the row template of a Keşfet sample trip's list by row type (ExploreDetailViewModel.Rows).</summary>
public sealed class ExploreRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Section { get; set; }
    public DataTemplate? Day { get; set; }
    public DataTemplate? Activity { get; set; }
    public DataTemplate? Place { get; set; }
    public DataTemplate? Todo { get; set; }
    public DataTemplate? Tip { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container) => item switch
    {
        ExploreSectionRow => Section!,
        ExploreDayRow => Day!,
        ExploreActivityRow => Activity!,
        ExplorePlaceRow => Place!,
        ExploreTodoRow => Todo!,
        _ => Tip!,
    };
}
