using System.Runtime.CompilerServices;

namespace TravelPlanner.Helpers
{
    /// <summary>
    /// When a page (re)loads its data in OnAppearing, without fighting the page transition:
    /// - the first time a page appears its data is loaded right away (the page is empty, a spinner shows);
    /// - when it appears again (the user came back from a sub-page) the screen still shows its data, so the
    ///   refresh waits until the back animation has finished - rebuilding a list while it slides is what stutters;
    /// - the CollectionView re-measure workaround (TripListPage) runs after the load, never during the transition.
    /// </summary>
    public static class PageLoading
    {
        /// <summary>A little longer than Shell's page transition on Android.</summary>
        public static readonly TimeSpan TransitionDelay = TimeSpan.FromMilliseconds(320);

        private static readonly TimeSpan LayoutFixDelay = TimeSpan.FromMilliseconds(300);
        private static readonly ConditionalWeakTable<Page, object> Appeared = new();

        public static void LoadOnAppearing(this Page page, Action load, Action? layoutFix = null)
        {
            void Run()
            {
                load();
                if (layoutFix is not null)
                    page.Dispatcher.DispatchDelayed(LayoutFixDelay, layoutFix);
            }

            if (Appeared.TryGetValue(page, out _))
            {
                page.Dispatcher.DispatchDelayed(TransitionDelay, Run);
                return;
            }

            Appeared.AddOrUpdate(page, new object());
            Run();
        }
    }
}
