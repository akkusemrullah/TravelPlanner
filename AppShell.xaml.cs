using TravelPlanner.Helpers;
using TravelPlanner.Views;

namespace TravelPlanner
{
    public partial class AppShell : Shell
    {
        // G3: the shell is rebuilt after signing in/out - routes are global and registered only once.
        private static bool s_routesRegistered;

        public AppShell()
        {
            InitializeComponent();

            if (s_routesRegistered)
                return;
            s_routesRegistered = true;

            // Dashboard, TripList and Settings are the TabBar's ShellContents (AppShell.xaml) and get their
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
            Routing.RegisterRoute(Routes.AiPlanner, typeof(AiPlannerPage)); // AI trip planner
        }
    }
}
