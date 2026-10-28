namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-106: shows a tab like a tap on the Shell's own tab bar did - the tab keeps its pushed pages, and Shell.Navigated
    /// still feeds TP-90's back history. Used by the floating bottom menu and Home's links.
    /// </summary>
    public static class TabNavigation
    {
        /// <summary>The Shell route of the Home tab (AppShell.xaml).</summary>
        public const string Home = "DashboardPage";

        public static bool Select(string route)
        {
            if (Shell.Current?.CurrentItem is not { } item)
                return false;
            var section = item.Items.FirstOrDefault(s => s.CurrentItem?.Route == route);
            if (section is null || item.CurrentItem == section)
                return false;
            item.CurrentItem = section;
            return true;
        }
    }
}
