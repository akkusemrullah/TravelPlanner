using TravelPlanner.Helpers;
using TravelPlanner.Views;

namespace TravelPlanner
{
    public partial class AppShell : Shell
    {
        // G3: the shell is rebuilt after signing in/out - routes are global and registered only once.
        private static bool s_routesRegistered;

        // TP-90: back on a tab's root page goes to the tab opened before it instead of leaving the app
        private readonly TabBackHistory _tabHistory = new("DashboardPage");

        public AppShell()
        {
            InitializeComponent();

            if (s_routesRegistered)
                return;
            s_routesRegistered = true;

            // Dashboard, TripList, Explore and Settings are the TabBar's ShellContents (AppShell.xaml) and get their
            // routes there - registering them again here would throw "duplicate route". Everything pushed on
            // top of a tab is registered below.
            Routing.RegisterRoute(Routes.TripDetail, typeof(TripDetailPage));
            Routing.RegisterRoute(Routes.TripForm, typeof(TripFormPage));
            Routing.RegisterRoute(Routes.TodoList, typeof(TodoListPage));
            Routing.RegisterRoute(Routes.ExpenseList, typeof(ExpenseListPage));
            Routing.RegisterRoute(Routes.ExpenseForm, typeof(ExpenseFormPage));
            Routing.RegisterRoute(Routes.PlaceList, typeof(PlaceListPage));
            Routing.RegisterRoute(Routes.PlaceForm, typeof(PlaceFormPage));
            Routing.RegisterRoute(Routes.Weather, typeof(WeatherPage));
            Routing.RegisterRoute(Routes.Map, typeof(MapPage));
            Routing.RegisterRoute(Routes.Itinerary, typeof(ItineraryPage));
            Routing.RegisterRoute(Routes.ActivityForm, typeof(ActivityFormPage));
            Routing.RegisterRoute(Routes.ExpenseAnalysis, typeof(ExpenseAnalysisPage));
            Routing.RegisterRoute(Routes.Attachments, typeof(AttachmentsPage));
            Routing.RegisterRoute(Routes.Split, typeof(SplitPage));
            Routing.RegisterRoute(Routes.Privacy, typeof(PrivacyPage));
            Routing.RegisterRoute(Routes.Auth, typeof(AuthPage));   // G3
            Routing.RegisterRoute(Routes.Members, typeof(MembersPage)); // G6
            Routing.RegisterRoute(Routes.Trash, typeof(TrashPage));     // G6
            Routing.RegisterRoute(Routes.Preferences, typeof(PreferencesPage)); // Profil → Tercihler
            Routing.RegisterRoute(Routes.AiPlanner, typeof(AiPlannerPage)); // AI trip planner
            Routing.RegisterRoute(Routes.HelpCenter, typeof(HelpCenterPage)); // help centre
            Routing.RegisterRoute(Routes.ExploreDetail, typeof(ExploreDetailPage)); // TP-33: a Keşfet sample trip
        }

        protected override void OnNavigated(ShellNavigatedEventArgs args)
        {
            base.OnNavigated(args);
            if (CurrentItem?.CurrentItem?.CurrentItem?.Route is { } tab)
                _tabHistory.Show(tab);
        }

        // TP-90: the page handles back first (closing a sheet, leaving selection), then the open tab pops a page;
        // only when neither did is the previous tab shown. Back on Home with nothing behind it leaves the app.
        protected override bool OnBackButtonPressed()
        {
            if (base.OnBackButtonPressed())
                return true;

            if (Navigation.ModalStack.Count > 0)
            {
                Navigation.PopModalAsync().FireAndForget();
                return true;
            }

            if (_tabHistory.Back() is not { } route)
                return false;

            var section = CurrentItem?.Items.FirstOrDefault(s => s.CurrentItem?.Route == route);
            if (section is null)
                return false;

            CurrentItem!.CurrentItem = section; // like a tap on the tab: its pushed pages stay
            return true;
        }
    }
}
