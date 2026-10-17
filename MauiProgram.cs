using Microsoft.Extensions.Http.Resilience;
using Microsoft.Maui.Authentication;
using Microsoft.Extensions.Logging;
using Plugin.LocalNotification;
using TravelPlanner.Helpers;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Media;
using TravelPlanner.Services;
using TravelPlanner.Services.Pdf;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;
using TravelPlanner.Views;

namespace TravelPlanner
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    // UI typeface: Inter (SIL Open Font License, free). OpenSans stays registered because the
                    // PDF renderer embeds it and older markup may still reference the alias.
                    fonts.AddFont("Inter-Regular.ttf", "InterRegular");
                    fonts.AddFont("Inter-Medium.ttf", "InterMedium");
                    fonts.AddFont("Inter-SemiBold.ttf", "InterSemiBold");
                    fonts.AddFont("Inter-Bold.ttf", "InterBold");
                    // Icon font: Lucide (ISC licence, free) - one consistent stroke icon set for buttons, lists and the tab bar.
                    fonts.AddFont("Lucide.ttf", "Lucide");
                }).UseLocalNotification()
                .ConfigureMauiHandlers(handlers =>
                {
#if ANDROID
                    // Day plan: reorder by dragging the grip (tracks the touch natively, see DragHandleHandler).
                    handlers.AddHandler<Controls.DragHandle, DragHandleHandler>();
#endif
                });

            // One modern look for every text field: no platform underline/border, focus highlight (InputChrome.cs).
            Handlers.InputChrome.Configure();
            // TextTransform="Uppercase" in the app language (Turkish İ/I), see CultureUppercase.cs.
            Handlers.CultureUppercase.Configure();

            RegisterServices(builder.Services);
            RegisterViewModels(builder.Services);
            RegisterPages(builder.Services);

#if DEBUG
            builder.Logging.AddDebug();
#endif

            RegisterGlobalExceptionLogging();

            // Notification permission is requested after the splash (App.ShowMainShell) instead of here - at this
            // point (MainApplication.OnCreate on Android) there is no Activity yet, and
            // Plugin.LocalNotification needs one to show its permission prompt.
            return builder.Build();
        }

        // TP-203: everything is registered against its interface, so ViewModels/services depend on
        // abstractions and can be unit-tested with fakes (see Tests/TravelPlanner.Tests).
        private static void RegisterServices(IServiceCollection services)
        {
            // Platform services
            services.AddSingleton<IConnectivity>(Connectivity.Current);
            services.AddSingleton<IPreferences>(Preferences.Default);
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IThemeService, ThemeService>();
            services.AddSingleton<IDialogService, DialogService>();
            // Fully qualified on purpose: Plugin.LocalNotification (imported above for UseLocalNotification)
            // has its own INotificationService, so the bare name is ambiguous in this file.
            services.AddSingleton<Services.INotificationService, NotificationService>();
            services.AddSingleton<IResponseCache, ResponseCache>();
            // Device APIs used by Sprint 5 features, injected so ViewModels stay unit-testable.
            services.AddSingleton<IShare>(Share.Default);                 // TP-503
            services.AddSingleton<IFilePicker>(FilePicker.Default);       // TP-501, TP-502
            services.AddSingleton<IMediaPicker>(MediaPicker.Default);     // TP-501
            services.AddSingleton<ILauncher>(Launcher.Default);           // TP-501
            services.AddSingleton<IAppInfo>(AppInfo.Current);             // TP-507

            // Data
            services.AddSingleton<IDatabaseService>(sp =>
                new DatabaseService(AppConstants.DatabasePath, sp.GetRequiredService<ISettingsService>().CurrencyCode));
            services.AddSingleton<IExpenseService, ExpenseService>();
            services.AddSingleton<ITripService, TripService>();
            services.AddSingleton<ITodoService, TodoService>();
            services.AddSingleton<IPlaceService, PlaceService>();
            services.AddSingleton<IActivityService, ActivityService>();       // TP-401
            services.AddSingleton<IPackingService, PackingService>();         // TP-405
            services.AddSingleton<IBudgetAlertService, BudgetAlertService>(); // TP-404
            services.AddSingleton<IAttachmentService>(sp =>                  // TP-501 (+ G5 download from the cloud)
                new AttachmentService(sp.GetRequiredService<IDatabaseService>(), FileSystem.AppDataDirectory,
                                      sp.GetRequiredService<ISupabaseClient>()));
            services.AddSingleton<ISplitService, SplitService>();             // TP-504
            services.AddSingleton<ITrashService>(sp => new TrashService(      // G1 - 30-day trash (+ "Kalıcı olarak sil":
                sp.GetRequiredService<IDatabaseService>(),                    //   cloud copies of deleted files go too)
                sp.GetRequiredService<IAttachmentService>(),
                sp.GetRequiredService<ISupabaseClient>(),
                sp.GetRequiredService<IAuthService>()));

            // G3: account (Supabase Auth over plain HTTP; tokens in the platform's secure storage).
            services.AddSingleton<ISecureStorage>(SecureStorage.Default);
            services.AddSingleton<IWebAuthenticator>(WebAuthenticator.Default);
            services.AddHttpClient(SupabaseAuthService.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(SupabaseConfig.AuthBaseUrl);
                client.Timeout = TimeSpan.FromSeconds(20);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.HttpUserAgent);
            });
            // CAPTCHA (Cloudflare Turnstile) for e-mail sign-up / sign-in / password reset; off while SupabaseConfig.CaptchaSiteKey is empty.
            services.AddSingleton<ICaptchaService, TurnstileCaptchaService>();
            services.AddSingleton<IAuthService, SupabaseAuthService>();

            // G4/G5: sync engine (RPCs + Storage over plain HTTP).
            services.AddHttpClient(SupabaseClient.HttpClientName, client =>
            {
                client.BaseAddress = new Uri(SupabaseConfig.Url.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromMinutes(5); // attachment uploads on slow mobile connections
                client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.HttpUserAgent);
            });
            services.AddSingleton<ISupabaseClient, SupabaseClient>();
            services.AddSingleton<ISyncService, SyncService>();

            // G6: roles per trip and sharing (invites, members).
            services.AddSingleton<ITripAccessService, TripAccessService>();
            services.AddSingleton<ISharingService, SharingService>();
            services.AddSingleton<IMemberJoinWatcher, MemberJoinWatcher>();   // TP-4: "someone joined your trip"
            // AI trip planner: Supabase Edge Function "plan-trip" (the OpenAI key stays on the server).
            services.AddSingleton<IDeviceIdentity, DeviceIdentity>();
            services.AddSingleton<IAiPlannerService, AiPlannerService>();
            services.AddSingleton<IAccountService>(sp => new AccountService(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<IDatabaseService>(),
                sp.GetRequiredService<IAttachmentService>(),
                sp.GetRequiredService<ITripService>(),
                sp.GetRequiredService<Services.INotificationService>(),
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IDialogService>(),
                FileSystem.AppDataDirectory,
                AppConstants.DatabasePath,
                sp.GetRequiredService<ISupabaseClient>())); // G7: deleting the account
            services.AddSingleton<ITripPdfRenderer>(_ => new PdfSharpTripRenderer(FileSystem.CacheDirectory)); // TP-502
            services.AddSingleton<ITripReportService, TripReportService>();   // TP-502 / TP-503
            services.AddSingleton<IBackupService>(sp =>                      // TP-502
                new BackupService(sp.GetRequiredService<IDatabaseService>(), sp.GetRequiredService<IAttachmentService>(), FileSystem.CacheDirectory));

            // Web APIs (free, keyless): typed HttpClients with a timeout, a User-Agent and retries (TP-208).
            services.AddHttpClient<IWeatherService, WeatherService>(ConfigureApiClient)
                .AddStandardResilienceHandler(ConfigureResilience);
            services.AddHttpClient<IGeocodingService, GeocodingService>(ConfigureApiClient)
                .AddStandardResilienceHandler(ConfigureResilience);
            // TP-403: Frankfurter (ECB reference rates) - free, keyless, no quotas.
            services.AddHttpClient<IExchangeRateService, ExchangeRateService>(ConfigureApiClient)
                .AddStandardResilienceHandler(ConfigureResilience);
        }

        private static void RegisterViewModels(IServiceCollection services)
        {
            services.AddTransient<DashboardViewModel>();
            services.AddTransient<TripListViewModel>();
            services.AddTransient<TripFormViewModel>();
            services.AddTransient<TripDetailViewModel>();
            services.AddTransient<TodoListViewModel>();
            services.AddTransient<ExpenseListViewModel>();
            services.AddTransient<ExpenseFormViewModel>();
            services.AddTransient<PlaceListViewModel>();
            services.AddTransient<PlaceFormViewModel>();
            services.AddTransient<MapViewModel>();
            services.AddTransient<WeatherViewModel>();
            services.AddTransient<SettingsViewModel>();
            services.AddTransient<ItineraryViewModel>();
            services.AddTransient<ActivityFormViewModel>();
            services.AddTransient<ExpenseAnalysisViewModel>();
            services.AddTransient<AttachmentsViewModel>();
            services.AddTransient<SplitViewModel>();
            services.AddTransient<AuthViewModel>();     // G3
            services.AddTransient<MembersViewModel>();  // G6
            services.AddTransient<TrashViewModel>();    // G6
            services.AddTransient<AiPlannerViewModel>(); // AI trip planner
            services.AddTransient<LegalDocumentViewModel>(); // privacy / KVKK / terms / licences
            services.AddTransient<HelpCenterViewModel>();    // help centre (FAQ, contact)
        }

        // Pages navigated to via Shell routes also need to be DI-registered so their
        // constructor-injected ViewModel can be resolved when Shell instantiates them.
        private static void RegisterPages(IServiceCollection services)
        {
            services.AddTransient<DashboardPage>();
            services.AddTransient<TripListPage>();
            services.AddTransient<TripFormPage>();
            services.AddTransient<TripDetailPage>();
            services.AddTransient<TodoListPage>();
            services.AddTransient<ExpenseListPage>();
            services.AddTransient<ExpenseFormPage>();
            services.AddTransient<PlaceListPage>();
            services.AddTransient<PlaceFormPage>();
            services.AddTransient<MapPage>();
            services.AddTransient<WeatherPage>();
            services.AddTransient<SettingsPage>();
            services.AddTransient<ItineraryPage>();
            services.AddTransient<ActivityFormPage>();
            services.AddTransient<ExpenseAnalysisPage>();
            services.AddTransient<AttachmentsPage>();
            services.AddTransient<SplitPage>();
            services.AddTransient<PrivacyPage>();
            services.AddTransient<AuthPage>();          // G3
            services.AddTransient<MembersPage>();       // G6
            services.AddTransient<TrashPage>();         // G6
            services.AddTransient<AiPlannerPage>();     // AI trip planner
            services.AddTransient<HelpCenterPage>();    // help centre
        }

        private static void ConfigureApiClient(HttpClient client)
        {
            // Outer safety net only - the resilience handler's own timeouts below are tighter.
            client.Timeout = TimeSpan.FromSeconds(30);

            // Open-Meteo, Photon and Frankfurter ask API clients to identify themselves.
            client.DefaultRequestHeaders.UserAgent.ParseAdd(AppConstants.HttpUserAgent);
        }

        private static void ConfigureResilience(HttpStandardResilienceOptions options)
        {
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            options.Retry.MaxRetryAttempts = 2;
            // CircuitBreaker.SamplingDuration keeps its 30 s default (must be >= 2 x AttemptTimeout).
        }

        /// <summary>
        /// Last line of defence (TP-106): anything that slips past the ViewModels' RunSafeAsync wrappers is
        /// at least logged, and unobserved task exceptions are marked observed so they can't tear the
        /// process down later on the finalizer thread.
        /// </summary>
        private static void RegisterGlobalExceptionLogging()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                System.Diagnostics.Debug.WriteLine($"[TravelPlanner] Unhandled exception: {e.ExceptionObject}");

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                System.Diagnostics.Debug.WriteLine($"[TravelPlanner] Unobserved task exception: {e.Exception}");
                e.SetObserved();
            };
        }
    }
}
