using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class TripDetailViewModel : ViewModelBase
    {
        private readonly ITripService _tripService;
        private readonly IExpenseService _expenseService;
        private readonly IActivityService _activityService;
        private readonly IPlaceService _placeService;
        private readonly ITripReportService _reportService;
        private readonly IShare _share;
        private readonly ITripAccessService _access;
        private readonly IAccountService _account;
        private readonly IConnectivity _connectivity;
        private readonly IAiPlannerService? _aiPlanner;

        [ObservableProperty]
        private int tripId;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Ticket))]
        private Trip? trip;

        /// <summary>Boarding-pass header card (TripTicket) for the loaded trip.</summary>
        public TripTicket? Ticket { get; private set; }

        partial void OnTripChanged(Trip? value) => Ticket = value is null ? null : new TripTicket(value);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(TotalExpensesFormatted))]
        private decimal totalExpenses;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RemainingBudgetFormatted))]
        private decimal remainingBudget;

        /// <summary>Amounts in the trip's own budget currency (TP-403).</summary>
        public string TotalExpensesFormatted => MoneyFormatter.FormatIn(TotalExpenses, Trip?.EffectiveCurrencyCode);

        public string RemainingBudgetFormatted => MoneyFormatter.FormatIn(RemainingBudget, Trip?.EffectiveCurrencyCode);

        // ---- "Bugün" (today) view - TP-401 ----

        /// <summary>True while the trip is under way: the page then shows today's plan.</summary>
        [ObservableProperty]
        private bool isTripOngoing;

        /// <summary>"Bugün · 3. gün" header for the today card.</summary>
        [ObservableProperty]
        private string todayTitle = string.Empty;

        public ObservableCollection<TripActivity> TodayActivities { get; } = new();

        [ObservableProperty]
        private bool hasTodayActivities;

        [ObservableProperty]
        private bool hasNoTodayActivities;

        [ObservableProperty]
        private double budgetUsagePercentage;

        /// <summary>True once a real (>0) planned budget exists - controls whether the budget grid or the "no budget" message shows.</summary>
        public bool HasBudget => Trip is not null && Trip.PlannedBudget > 0;

        /// <summary>True when a trip has no planned budget set yet (edge case: zero budget).</summary>
        public bool NoBudgetSet => !HasBudget;

        /// <summary>True when expenses have exceeded the planned budget (edge case: over budget).</summary>
        public bool IsOverBudget => HasBudget && RemainingBudget < 0;

        // ---- G6: sharing ----

        /// <summary>Only the owner may delete the trip; others leave it from the members page.</summary>
        [ObservableProperty]
        private bool isOwner = true;

        /// <summary>Subtitle of the "Üyeler ve davet" row: the user's role, or "sign in to share".</summary>
        [ObservableProperty]
        private string sharingSubtitle = string.Empty;

        /// <summary>"Son değişiklik: Ayşe · 5 dk önce".</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasLastChange))]
        private string? lastChangeText;

        public bool HasLastChange => !string.IsNullOrEmpty(LastChangeText);

        public TripDetailViewModel(
            ITripService tripService,
            IExpenseService expenseService,
            IActivityService activityService,
            IPlaceService placeService,
            ITripReportService reportService,
            IShare share,
            ITripAccessService access,
            IAccountService account,
            IConnectivity connectivity,
            IDialogService dialogs,
            IAiPlannerService? aiPlanner = null,
            ITripCoverService? covers = null,
            IAiPlanJobs? aiJobs = null,
            ITodoService? todos = null,
            IAttachmentService? attachments = null)
            : base(dialogs)
        {
            _todos = todos;
            _attachments = attachments;
            _aiJobs = aiJobs;
            _covers = covers;
            _aiPlanner = aiPlanner;
            _access = access;
            _account = account;
            _connectivity = connectivity;
            _tripService = tripService;
            _expenseService = expenseService;
            _activityService = activityService;
            _placeService = placeService;
            _reportService = reportService;
            _share = share;
            ListPositions.Track(TodayActivities); // TP-102: dividers between today's rows
        }

        private readonly ITripCoverService? _covers;
        private readonly IAiPlanJobs? _aiJobs;
        private readonly ITodoService? _todos;
        private readonly IAttachmentService? _attachments;

        // ---- TP-98: the grey line under each row (TripDetailSummary) ----

        [ObservableProperty]
        private string activitiesSummary = string.Empty;

        [ObservableProperty]
        private string tasksSummary = string.Empty;

        [ObservableProperty]
        private string placesSummary = string.Empty;

        [ObservableProperty]
        private string expensesSummary = string.Empty;

        [ObservableProperty]
        private string filesSummary = string.Empty;

        /// <summary>TP-98: the budget bar turns amber from 80 % (red once over the budget).</summary>
        public bool IsBudgetWarning => HasBudget && TripDetailSummary.IsBudgetWarning(BudgetUsagePercentage, IsOverBudget);

        /// <summary>TP-102: the budget bar's colour (Controls/ProgressPill tone).</summary>
        public string BudgetTone => IsOverBudget ? "danger" : IsBudgetWarning ? "warning" : "accent";

        /// <summary>TP-102: the budget bar's fill, 0..1.</summary>
        public double BudgetProgress => Math.Clamp(BudgetUsagePercentage / 100.0, 0, 1);

        /// <summary>"%64" next to the budget title.</summary>
        public string BudgetPercentText => string.Format(AppResources.TripDetail_PercentFormat, Math.Round(BudgetUsagePercentage));

        private async Task LoadSummariesAsync(int id)
        {
            try
            {
                ActivitiesSummary = TripDetailSummary.Activities((await _activityService.GetByTripIdAsync(id)).Count);
                PlacesSummary = TripDetailSummary.Places(await _placeService.GetByTripIdAsync(id));
                ExpensesSummary = TripDetailSummary.Expenses((await _expenseService.GetByTripIdAsync(id)).Count, TotalExpensesFormatted);
                TasksSummary = _todos is null ? string.Empty : TripDetailSummary.Tasks(await _todos.GetByTripIdAsync(id));
                FilesSummary = _attachments is null ? string.Empty : TripDetailSummary.Files((await _attachments.GetByTripIdAsync(id)).Count);
            }
            catch (Exception ex)
            {
                // only the small lines: the page itself is already there
                System.Diagnostics.Debug.WriteLine($"[TripDetail] Summaries: {ex.Message}");
            }
        }

        /// <summary>The trip's cover photo on the phone (downloaded when needed), or null: no cover shown.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasCoverFile))]
        private string? coverFile;

        public bool HasCoverFile => CoverFile is not null;

        private async Task LoadCoverAsync(Trip trip)
        {
            var file = await _covers!.GetFileAsync(trip, thumbnail: false);
            if (ReferenceEquals(Trip, trip))
                CoverFile = file;
        }

        private Task LoadAsync(int id) => RunLoadAsync(async () =>
        {
            var loaded = await _tripService.GetByIdAsync(id);
            if (loaded is null)
            {
                // G6: the trip vanished while this page was open - deleted by its owner on another device, or the
                // user was taken off the shared trip - and a sync removed it. Don't leave a blank page behind.
                if (Trip is not null)
                    await LeaveRemovedTripAsync();
                return;
            }

            Trip = loaded;
            if (_covers?.HasCover(loaded) == true)
                LoadCoverAsync(loaded).FireAndForget(); // may download it: the page doesn't wait
            else
                CoverFile = null;

            TotalExpenses = await _expenseService.GetTotalExpensesAsync(id);
            RemainingBudget = await _expenseService.GetRemainingBudgetAsync(id, Trip.PlannedBudget);
            BudgetUsagePercentage = await _expenseService.GetBudgetUsagePercentageAsync(id, Trip.PlannedBudget);

            OnPropertyChanged(nameof(HasBudget));
            OnPropertyChanged(nameof(NoBudgetSet));
            OnPropertyChanged(nameof(IsOverBudget));
            OnPropertyChanged(nameof(IsBudgetWarning));
            OnPropertyChanged(nameof(BudgetTone));
            OnPropertyChanged(nameof(BudgetProgress));
            OnPropertyChanged(nameof(BudgetPercentText));
            OnPropertyChanged(nameof(TotalExpensesFormatted));
            OnPropertyChanged(nameof(RemainingBudgetFormatted));

            await LoadTodayAsync(Trip);
            await LoadSummariesAsync(id);
            await LoadSharingAsync(id);
            // a network call: never hold the page's load (and its spinner) for it
            LoadAiQuotaAsync().FireAndForget();
        }, AppResources.TripDetail_LoadError);

        /// <summary>Subtitle of the AI planner card: how many plans are left today, or why it can't be used.</summary>
        [ObservableProperty]
        private string aiPlannerSubtitle = AppResources.AiPlan_EntrySubtitle;

        private async Task LoadAiQuotaAsync()
        {
            if (!_account.IsSignedIn)
            {
                AiPlannerSubtitle = AppResources.AiPlan_EntrySignIn;
                return;
            }
            // TP-57: a plan of this trip being made in the background, or made and not looked at yet
            switch (Trip is null ? null : _aiJobs?.For(Trip.Id)?.Phase)
            {
                case AiPlanJobPhase.Running:
                    AiPlannerSubtitle = AppResources.AiPlan_EntryRunning;
                    return;
                case AiPlanJobPhase.Ready:
                    AiPlannerSubtitle = AppResources.AiPlan_EntryReady;
                    return;
            }
            if (_aiPlanner is null || _connectivity.NetworkAccess != NetworkAccess.Internet)
            {
                AiPlannerSubtitle = AppResources.AiPlan_EntrySubtitle;
                return;
            }

            var quota = await _aiPlanner.GetQuotaAsync();
            AiPlannerSubtitle = quota is null
                ? AppResources.AiPlan_EntrySubtitle
                : quota.Paused
                    ? quota.Message ?? AppResources.AiPlan_Paused // maintenance: the admin's note, not "no plans left"
                : quota.Remaining > 0
                    ? string.Format(AppResources.AiPlan_QuotaFormat, quota.Remaining, quota.Limit)
                    : AppResources.AiPlan_QuotaNone;
        }

        /// <summary>Navigation after the open trip disappeared. Replaced in tests.</summary>
        internal Func<Task> AfterTripRemovedAction { get; set; } = async () =>
        {
            if (Shell.Current is not null)
                await Shell.Current.GoToAsync("..");
        };

        private async Task LeaveRemovedTripAsync()
        {
            Trip = null;
            await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.TripDetail_TripRemoved);
            await AfterTripRemovedAction();
        }

        private async Task LoadSharingAsync(int id)
        {
            await RefreshAccessAsync(id);
            IsOwner = Access?.IsOwner ?? true;
            SharingSubtitle = _account.IsSignedIn
                ? string.Format(AppResources.Members_MyRoleFormat, SharingTexts.RoleName(Access?.Role ?? TripRole.Owner))
                : AppResources.TripDetail_SharingSignIn;

            LastChangeText = null;
            if (!_account.IsSignedIn)
                return;

            try
            {
                var last = await _access.GetLastChangeAsync(id);
                if (last is null)
                    return;

                var name = string.Equals(last.UserId, Services.Sync.ChangeTracker.CurrentUserId, StringComparison.OrdinalIgnoreCase)
                    ? AppResources.Members_You
                    : await _access.GetUserNameAsync(last.UserId);
                if (name is null)
                    return; // someone we don't know the name of yet (members list not opened)

                LastChangeText = string.Format(AppResources.TripDetail_LastChangeFormat, name,
                    SharingTexts.RelativeTime(last.AtUtc, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TripDetail] Last change: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task GoToMembersAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Members}?TripId={Trip.Id}");
        }

        private async Task LoadTodayAsync(Trip trip)
        {
            IsTripOngoing = trip.IsOngoing;
            TodayActivities.Clear();

            if (!trip.IsOngoing)
            {
                HasTodayActivities = HasNoTodayActivities = false;
                return;
            }

            var dayNumber = (DateTime.Today - trip.StartDate.Date).Days + 1;
            TodayTitle = string.Format(AppResources.TripDetail_TodayTitleFormat, dayNumber);

            var activities = await _activityService.GetByDateAsync(trip.Id, DateTime.Today);
            if (activities.Any(a => a.PlaceId is not null))
            {
                var places = (await _placeService.GetByTripIdAsync(trip.Id)).ToDictionary(p => p.Id, p => p.Name);
                foreach (var activity in activities)
                    activity.PlaceName = activity.PlaceId is int id && places.TryGetValue(id, out var name) ? name : null;
            }

            foreach (var activity in activities)
                TodayActivities.Add(activity);

            HasTodayActivities = TodayActivities.Count > 0;
            HasNoTodayActivities = !HasTodayActivities;
        }

        [RelayCommand]
        private Task RefreshAsync() => TripId > 0 ? LoadAsync(TripId) : Task.CompletedTask;

        // Backs the page's custom back-arrow header button now that TripDetailPage builds its own
        // header (Shell.NavBarIsVisible="False") instead of relying on Shell's default nav bar/back button -
        // same "..".GoToAsync convention TripFormViewModel.CancelAsync already uses.
        [RelayCommand]
        private async Task GoBackAsync() => await Shell.Current.GoToAsync("..");

        [RelayCommand]
        private async Task EditTripAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.TripForm}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private Task DeleteTripAsync() => RunSafeAsync(async () =>
        {
            if (Trip is null) return;

            // G6: deleting a trip others may share is an online-only action (and the owner's) - same check as
            // the trip list's swipe action.
            if (!await TripDeleteConfirmation.CheckAllowedAsync(Dialogs, Trip, _connectivity))
                return;

            if (!await TripDeleteConfirmation.AskAsync(Dialogs, _tripService, Trip))
                return;

            await _tripService.DeleteAsync(Trip);
            await Shell.Current.GoToAsync("..");
        }, AppResources.TripDetail_DeleteError);

        /// <summary>
        /// "Seyahati kopyala": a new trip with the same destination, budget, to-dos (unticked), places (not visited),
        /// day plan and participants - handy for a trip that repeats. Expenses and files are not copied. The copy
        /// opens right away; editing its dates moves the copied day plan along (TripDateRealignment).
        /// </summary>
        [RelayCommand]
        private Task DuplicateTripAsync() => RunSafeAsync(async () =>
        {
            if (Trip is null) return;

            if (!await Dialogs.ConfirmAsync(AppResources.TripDuplicate_Title,
                    string.Format(AppResources.TripDuplicate_Message, Trip.Name),
                    AppResources.TripDuplicate_Accept, AppResources.Common_Cancel))
                return;

            var copy = await _tripService.DuplicateAsync(Trip.Id, DuplicateName(Trip.Name));
            if (copy is null) return;

            await Dialogs.ShowMessageAsync(AppResources.Common_Info, AppResources.TripDuplicate_Done);
            await AfterDuplicateAction(copy.Id);
        }, AppResources.TripDuplicate_Error);

        /// <summary>"Roma Tatili" → "Roma Tatili (kopya)", kept within the 100-character name limit.</summary>
        internal static string DuplicateName(string name)
        {
            var suffix = string.Format(AppResources.TripDuplicate_NameFormat, string.Empty);
            var room = Math.Max(1, TripFormViewModel.NameMaxLength - suffix.Length);
            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length > room)
                trimmed = trimmed[..room].TrimEnd();
            return string.Format(AppResources.TripDuplicate_NameFormat, trimmed);
        }

        /// <summary>Opens the copy in place of this page. Replaced in tests.</summary>
        internal Func<int, Task> AfterDuplicateAction { get; set; } = async newTripId =>
        {
            if (Shell.Current is not null)
                await Shell.Current.GoToAsync($"../{Routes.TripDetail}?TripId={newTripId}");
        };

        /// <summary>AI trip planner (only for editors - the page says so otherwise).</summary>
        [RelayCommand]
        private async Task GoToAiPlannerAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.AiPlanner}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToTasksAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.TodoList}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToExpensesAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.ExpenseList}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToPlacesAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.PlaceList}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToMapAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Map}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToItineraryAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Itinerary}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToAnalysisAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.ExpenseAnalysis}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToAttachmentsAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Attachments}?TripId={Trip.Id}");
        }

        [RelayCommand]
        private async Task GoToSplitAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Split}?TripId={Trip.Id}");
        }

        /// <summary>
        /// TP-503: share the plan and summary through the system share sheet - as a message (text) or as a PDF
        /// (TP-502). The share sheet also lets the user just save the PDF (Files, Drive…).
        /// </summary>
        [RelayCommand]
        private Task ShareAsync() => RunSafeAsync(async () =>
        {
            if (Trip is null) return;

            var choice = await Dialogs.ChooseAsync(AppResources.Share_Title, new[] { AppResources.Share_AsText, AppResources.Share_AsPdf });
            if (choice is null) return;

            var report = await _reportService.BuildAsync(Trip.Id);
            if (report is null) return;

            if (choice == AppResources.Share_AsText)
            {
                await _share.RequestAsync(new ShareTextRequest
                {
                    Title = Trip.Name,
                    Subject = Trip.Name,
                    Text = _reportService.ToText(report)
                });
            }
            else
            {
                var path = await _reportService.CreatePdfAsync(report);
                await _share.RequestAsync(new ShareFileRequest
                {
                    Title = Trip.Name,
                    File = new ShareFile(path, "application/pdf")
                });
            }
        }, AppResources.Share_Error);

        [RelayCommand]
        private async Task GoToWeatherAsync()
        {
            if (Trip is null) return;
            await Shell.Current.GoToAsync($"{Routes.Weather}?TripId={Trip.Id}");
        }
    }
}
