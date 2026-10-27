using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;

namespace TravelPlanner.ViewModels
{
    /// <summary>One answer chip of the planner's questions ("Tarih", "Dengeli", "Çift"…).</summary>
    public partial class AiChoice : ObservableObject
    {
        public AiChoice(string group, string key, string label, bool isSelected = false)
        {
            Group = group;
            Key = key;
            Label = label;
            IsSelected = isSelected;
        }

        public string Group { get; }
        public string Key { get; }
        public string Label { get; }

        [ObservableProperty]
        private bool isSelected;
    }

    public partial class AiPreviewActivity : ObservableObject, IListPositioned
    {
        /// <summary>TP-104: its slice of the day card (see <see cref="ListPositions"/>).</summary>
        [ObservableProperty]
        private ListPosition listPosition;

        public AiPreviewActivity(int day, AiPlanActivity activity, string? placeName = null)
        {
            Day = day;
            Activity = activity;
            PlaceName = placeName;
        }

        public int Day { get; }
        public AiPlanActivity Activity { get; }

        /// <summary>Name of the place the activity happens at, or null.</summary>
        public string? PlaceName { get; }

        /// <summary>The start ("09:30"), "—" without a time.</summary>
        public string TimeText => Activity.Start ?? "—";

        /// <summary>The end under the start ("11:00"), empty when there is none.</summary>
        public string EndText => Activity.Start is null ? string.Empty : Activity.End ?? string.Empty;

        public bool HasEnd => EndText.Length > 0;

        public string Title => Activity.Title;

        /// <summary>"Kolezyum · Erken git, kuyruk uzun".</summary>
        public string Detail => string.Join(" · ", new[] { PlaceName, Activity.Note }
            .Where(s => !string.IsNullOrWhiteSpace(s) && !Activity.Title.Contains(s!, StringComparison.CurrentCultureIgnoreCase)));

        public bool HasDetail => Detail.Length > 0;

        [ObservableProperty]
        private bool isSelected = true;
    }

    public sealed class AiPreviewDay : IListPositioned
    {
        /// <summary>TP-104: the card's header slice.</summary>
        public ListPosition ListPosition { get; set; }

        public AiPreviewDay(string header, string title, IReadOnlyList<AiPreviewActivity> activities)
        {
            Header = header;
            Title = title;
            Activities = activities;
        }

        public string Header { get; }
        public string Title { get; }
        public bool HasTitle => !string.IsNullOrWhiteSpace(Title);
        public IReadOnlyList<AiPreviewActivity> Activities { get; }
    }

    public partial class AiPreviewPlace : ObservableObject, IListPositioned
    {
        [ObservableProperty]
        private ListPosition listPosition;

        public AiPreviewPlace(AiPlanPlace place) => Place = place;

        public AiPlanPlace Place { get; }
        public string Name => Place.Name;

        /// <summary>"Müze · Monti — Antik Roma'nın en büyük amfitiyatrosu".</summary>
        public string Description
        {
            get
            {
                var header = string.Join(" · ", new[] { AiPlaceCategories.Label(Place.Category), Place.Area }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
                if (header.Length == 0)
                    return Place.Description;
                return string.IsNullOrWhiteSpace(Place.Description) ? header : $"{header} — {Place.Description}";
            }
        }

        [ObservableProperty]
        private bool isSelected = true;
    }

    /// <summary>
    /// A section title in the preview list ("Gün gün plan"), with an optional caption under it. TP-104: the
    /// prototype puts an icon (<see cref="IconKey"/>, an Ic.* resource) before it; tips get the warning colour.
    /// </summary>
    public sealed record AiPreviewSection(string Title, string? Hint = null, string IconKey = "Ic.Calendar", bool IsWarning = false)
    {
        public bool HasHint => !string.IsNullOrEmpty(Hint);

        public string Glyph => Application.Current?.Resources.TryGetValue(IconKey, out var value) == true ? value as string ?? string.Empty : string.Empty;
    }

    /// <summary>A read-only local tip in the preview list.</summary>
    public sealed record AiPreviewTip(string Text) : IListPositioned
    {
        public ListPosition ListPosition { get; set; }
    }

    public partial class AiPreviewTodo : ObservableObject, IListPositioned
    {
        [ObservableProperty]
        private ListPosition listPosition;

        public AiPreviewTodo(string title) => Title = title;

        public string Title { get; }

        [ObservableProperty]
        private bool isSelected = true;
    }

    /// <summary>
    /// AI trip planner: 4 questions (interests, pace, budget, company) + an optional note → the plan from the
    /// "plan-trip" Edge Function → a preview where everything can be unticked → "Seçilenleri plana ekle".
    /// Needs an account (the daily quota is per account) and a connection; only editors of the trip get here.
    /// TP-57: the plan is made by <see cref="IAiPlanJobs"/>, not by this page: the user can leave while it is being
    /// made, and coming back (even after the app was closed) shows the progress or the ready plan.
    /// </summary>
    [QueryProperty(nameof(TripId), "TripId")]
    public partial class AiPlannerViewModel : ViewModelBase
    {
        public const string GroupInterest = "interest";
        public const string GroupPace = "pace";
        public const string GroupBudget = "budget";
        public const string GroupCompanions = "companions";

        /// <summary>More interests make a muddled plan.</summary>
        public const int MaxInterests = 4;

        private readonly IAiPlannerService _planner;
        private readonly IAiPlanJobs _jobs;
        private readonly ITripService _trips;
        private readonly IPlaceService _places;
        private readonly IAccountService _account;
        private readonly IConnectivity _connectivity;
        private readonly IConsentService? _consent;
        private Trip? _trip;
        private AiTripPlan? _plan;
        private string? _planRequestId;
        private long _planShownAt;

        /// <summary>The job whose plan (or error) is on screen: shown once.</summary>
        private string? _shownJobId;
        private bool _watching;
        private CancellationTokenSource? _steps;

        public AiPlannerViewModel(IAiPlannerService planner, IAiPlanJobs jobs, ITripService trips, IPlaceService places,
                                  IAccountService account, IConnectivity connectivity, IDialogService dialogs,
                                  IConsentService? consent = null)
            : base(dialogs)
        {
            _consent = consent;
            _planner = planner;
            _jobs = jobs;
            _trips = trips;
            _places = places;
            _account = account;
            _connectivity = connectivity;

            Interests = new ObservableCollection<AiChoice>(new[]
            {
                Choice(GroupInterest, "history", AppResources.AiPlan_I_History, true),
                Choice(GroupInterest, "food", AppResources.AiPlan_I_Food, true),
                Choice(GroupInterest, "museums", AppResources.AiPlan_I_Museums),
                Choice(GroupInterest, "nature", AppResources.AiPlan_I_Nature),
                Choice(GroupInterest, "beach", AppResources.AiPlan_I_Beach),
                Choice(GroupInterest, "architecture", AppResources.AiPlan_I_Architecture),
                Choice(GroupInterest, "art", AppResources.AiPlan_I_Art),
                Choice(GroupInterest, "shopping", AppResources.AiPlan_I_Shopping),
                Choice(GroupInterest, "nightlife", AppResources.AiPlan_I_Nightlife),
                Choice(GroupInterest, "family", AppResources.AiPlan_I_Family),
                Choice(GroupInterest, "adventure", AppResources.AiPlan_I_Adventure),
                Choice(GroupInterest, "relax", AppResources.AiPlan_I_Relax),
            });
            foreach (var interest in Interests)
                interest.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(AiChoice.IsSelected))
                        OnPropertyChanged(nameof(InterestCountText));
                };
            Paces = new ObservableCollection<AiChoice>(new[]
            {
                Choice(GroupPace, "relaxed", AppResources.AiPlan_Pace_Relaxed),
                Choice(GroupPace, "balanced", AppResources.AiPlan_Pace_Balanced, true),
                Choice(GroupPace, "packed", AppResources.AiPlan_Pace_Packed),
            });
            Budgets = new ObservableCollection<AiChoice>(new[]
            {
                Choice(GroupBudget, "budget", AppResources.AiPlan_Budget_Low),
                Choice(GroupBudget, "mid", AppResources.AiPlan_Budget_Mid, true),
                Choice(GroupBudget, "luxury", AppResources.AiPlan_Budget_High),
            });
            Companions = new ObservableCollection<AiChoice>(new[]
            {
                Choice(GroupCompanions, "solo", AppResources.AiPlan_With_Solo),
                Choice(GroupCompanions, "couple", AppResources.AiPlan_With_Couple, true),
                Choice(GroupCompanions, "family", AppResources.AiPlan_With_Family),
                Choice(GroupCompanions, "friends", AppResources.AiPlan_With_Friends),
            });
        }

        private static AiChoice Choice(string group, string key, string label, bool selected = false) => new(group, key, label, selected);

        public ObservableCollection<AiChoice> Interests { get; }

        /// <summary>TP-104: "2/4" next to the interests question.</summary>
        public string InterestCountText => $"{Interests.Count(i => i.IsSelected)}/{MaxInterests}";
        public ObservableCollection<AiChoice> Paces { get; }
        public ObservableCollection<AiChoice> Budgets { get; }
        public ObservableCollection<AiChoice> Companions { get; }

        public ObservableCollection<AiPreviewDay> Days { get; } = new();
        public ObservableCollection<AiPreviewPlace> Places { get; } = new();
        public ObservableCollection<AiPreviewTodo> Todos { get; } = new();
        public ObservableCollection<string> Tips { get; } = new();

        /// <summary>
        /// The whole preview as one flat list (sections, day headers, activities, places, to-dos, tips) for a single
        /// virtualized CollectionView: a long plan used to create every row and check box at once and froze the screen.
        /// </summary>
        public ObservableCollection<object> PreviewRows { get; } = new RangeObservableCollection<object>();

        [ObservableProperty]
        private int tripId;

        /// <summary>"Roma için 5 günlük bir plan hazırlayalım."</summary>
        [ObservableProperty]
        private string introText = string.Empty;

        [ObservableProperty]
        private string notes = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsGuest), nameof(CanGenerate))]
        private bool isSignedIn;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsOffline), nameof(CanGenerate))]
        private bool isOnline = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsBlocked), nameof(CanGenerate))]
        private bool isGenerating;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsBlocked), nameof(IsFormVisible))]
        private bool isPreview;

        [ObservableProperty]
        private bool isApplying;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGenerate), nameof(IsAsking), nameof(IsBlocked))]
        private int? remaining;

        /// <summary>AI maintenance (admin panel) rather than the daily limit.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(BlockedTitle))]
        private bool isPaused;

        /// <summary>Under the blocked card's title: the admin's maintenance note, or when the plans come back.</summary>
        [ObservableProperty]
        private string blockedText = string.Empty;

        /// <summary>TP-57: a plan of another trip is being made: one at a time.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGenerate))]
        private bool isBusyElsewhere;

        [ObservableProperty]
        private string quotaText = string.Empty;

        /// <summary>TP-92: what the allowance belongs to and when it comes back ("Yenilenme: 28 Eki 00:00").</summary>
        [ObservableProperty]
        private string quotaRuleText = AppResources.AiPlan_QuotaRule;

        /// <summary>TP-92: the last period the server told (a plan's answer doesn't repeat it).</summary>
        private AiQuota? _period;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string? errorText;

        [ObservableProperty]
        private string summary = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasTruncatedText))]
        private string truncatedText = string.Empty;

        public bool IsGuest => !IsSignedIn;
        public bool IsOffline => !IsOnline;
        public bool IsAsking => !IsGenerating && !IsPreview && !IsBlocked;

        /// <summary>No plan can be asked for (daily limit or maintenance): one clear card instead of the questions.</summary>
        public bool IsBlocked => Remaining is 0 && !IsGenerating && !IsPreview;
        public string BlockedTitle => IsPaused ? AppResources.AiPlan_PausedTitle : AppResources.AiPlan_QuotaNoneTitle;

        /// <summary>The questions / spinner area (everything but the preview list).</summary>
        public bool IsFormVisible => !IsPreview;
        public bool HasError => !string.IsNullOrEmpty(ErrorText);
        public bool HasTruncatedText => !string.IsNullOrEmpty(TruncatedText);
        public bool HasPlaces => Places.Count > 0;
        public bool HasTodos => Todos.Count > 0;
        public bool HasTips => Tips.Count > 0;
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        public bool CanGenerate => IsSignedIn && IsOnline && !IsGenerating && !IsBusyElsewhere && CanEdit && Remaining is not 0;

        // TP-104: the prototype shows one status card instead of the questions when no plan can be asked for.
        private bool CanAsk => !IsGenerating && !IsPreview;
        public bool ShowGuestGate => CanAsk && IsGuest;
        public bool ShowOfflineGate => CanAsk && IsSignedIn && IsOffline;
        public bool ShowReadOnlyGate => CanAsk && IsSignedIn && IsOnline && IsReadOnly;
        public bool ShowBusyGate => CanAsk && IsSignedIn && IsOnline && !IsReadOnly && IsBusyElsewhere;
        public bool ShowBlockedGate => IsBlocked && IsSignedIn && IsOnline && !IsReadOnly && !IsBusyElsewhere;
        public bool ShowQuestions => IsAsking && IsSignedIn && IsOnline && !IsReadOnly && !IsBusyElsewhere;
        public bool ShowErrorBanner => HasError && !ShowBusyGate;
        public bool ShowBackButton => ShowGuestGate || ShowOfflineGate || ShowReadOnlyGate || ShowBusyGate || ShowBlockedGate;

        /// <summary>TP-104: the app bar's second line.</summary>
        [ObservableProperty]
        private string tripName = string.Empty;

        private static readonly HashSet<string> GateInputs = new()
        {
            nameof(IsSignedIn), nameof(IsOnline), nameof(IsGenerating), nameof(IsPreview), nameof(Remaining), nameof(CanEdit), nameof(IsBusyElsewhere), nameof(ErrorText),
        };

        protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.PropertyName is null || !GateInputs.Contains(e.PropertyName))
                return;
            foreach (var name in new[] { nameof(ShowGuestGate), nameof(ShowOfflineGate), nameof(ShowReadOnlyGate), nameof(ShowBusyGate),
                                         nameof(ShowBlockedGate), nameof(ShowQuestions), nameof(ShowBackButton), nameof(ShowErrorBanner) })
                base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(name));
        }

        partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            _trip = await _trips.GetByIdAsync(TripId);
            if (_trip is null) return;
            TripName = _trip.Name;

            await RefreshAccessAsync(TripId);
            OnPropertyChanged(nameof(CanGenerate));

            ShowIntro();
            IsSignedIn = _account.IsSignedIn;
            IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;

            Watch();
            await ShowJobAsync();
            if (IsSignedIn && IsOnline && !IsPreview && !IsGenerating)
                ShowQuota(await _planner.GetQuotaAsync());
        }, AppResources.AiPlan_LoadError);

        /// <summary>Runs on the UI thread (the jobs report from any thread). Replaced in tests.</summary>
        internal Action<Action> Dispatch { get; set; } = MainThread.BeginInvokeOnMainThread;

        /// <summary>The page is on screen: follow the plan of this trip, and no notification for it.</summary>
        private void Watch()
        {
            _jobs.WatchedTripId = TripId;
            if (_watching)
                return;
            _watching = true;
            _jobs.Changed += OnJobsChanged;
        }

        /// <summary>The page left the screen: the plan goes on without it, and a notification says when it is ready.</summary>
        public void Leave()
        {
            if (_jobs.WatchedTripId == TripId)
                _jobs.WatchedTripId = null;
            if (_watching)
            {
                _watching = false;
                _jobs.Changed -= OnJobsChanged;
            }
            StopSteps();
        }

        private void OnJobsChanged(object? sender, EventArgs e) =>
            Dispatch(() => RunSafeAsync(ShowJobAsync, AppResources.AiPlan_ErrorFailed).FireAndForget());

        /// <summary>Shows where this trip's plan is: being made, ready (the preview) or failed (why).</summary>
        private async Task ShowJobAsync()
        {
            if (_trip is null)
                return;

            var job = _jobs.For(_trip.Id);
            var busyElsewhere = job is null && _jobs.IsRunning;
            if (IsBusyElsewhere != busyElsewhere)
            {
                IsBusyElsewhere = busyElsewhere;
                if (busyElsewhere)
                    ErrorText = AppResources.AiPlan_Busy;
                else if (ErrorText == AppResources.AiPlan_Busy)
                    ErrorText = null;
            }

            switch (job?.Phase)
            {
                case null:
                    StopSteps();
                    IsGenerating = false;
                    return;

                case AiPlanJobPhase.Running:
                    IsPreview = false;
                    ErrorText = null;
                    IsGenerating = true;
                    StartSteps(job.StartedUtc);
                    return;

                case AiPlanJobPhase.Ready when job.Result is { } result:
                    StopSteps();
                    IsGenerating = false;
                    if (!job.Offered)
                        _jobs.MarkOffered();
                    if (_shownJobId == job.Id && IsPreview)
                        return;
                    _shownJobId = job.Id;
                    var existing = (await _places.GetByTripIdAsync(_trip.Id)).Select(p => p.Name).ToList();
                    ShowPreview(_trip, result, existing);
                    ShowQuota(new AiQuota(result.Remaining, Math.Max(result.Remaining, 1)));
                    if (result.Remaining > 0)
                        QuotaText = string.Format(AppResources.AiPlan_QuotaLeftFormat, result.Remaining);
                    return;

                default: // failed: say why once, then the questions are back
                    StopSteps();
                    IsGenerating = false;
                    var kind = job.Error ?? AiPlanErrorKind.Failed;
                    // paused / daily limit: the blocked card says it
                    ErrorText = kind is AiPlanErrorKind.Paused or AiPlanErrorKind.QuotaExceeded ? null : MessageFor(kind);
                    if (kind == AiPlanErrorKind.QuotaExceeded)
                        ShowQuota(new AiQuota(0, 0));
                    else if (kind == AiPlanErrorKind.Paused)
                        ShowQuota(new AiQuota(0, 0, Paused: true));
                    else if (job.Remaining is int left)
                        ShowQuota(new AiQuota(left, Math.Max(left, 1)));
                    _jobs.Dismiss(_trip.Id);
                    return;
            }
        }

        // The preview must not be thrown away by a sync that reloads the page.
        protected override bool ReloadOnRemoteChanges => false;

        /// <summary>The panel may plan fewer days than <see cref="AiPlannerService.MaxDays"/>.</summary>
        private int MaxDays => _period?.MaxDays ?? AiPlannerService.MaxDays;

        private void ShowIntro()
        {
            if (_trip is not null)
                IntroText = string.Format(AppResources.AiPlan_IntroFormat, _trip.City, Math.Min(_trip.DurationInDays, MaxDays));
        }

        /// <summary>"28 Eki 00:00" in the phone's time and language.</summary>
        internal static string ResetText(DateTimeOffset at) =>
            at.ToLocalTime().ToString("d MMM HH:mm", System.Globalization.CultureInfo.CurrentCulture);

        private void ShowQuota(AiQuota? quota)
        {
            if (quota is null)
                return;
            if (quota.Period is not null)
            {
                _period = quota;
                ShowIntro();
            }
            QuotaRuleText = _period is null ? AppResources.AiPlan_QuotaRule
                : _period.NeverResets ? AppResources.AiPlan_QuotaRuleNever
                : _period.ResetsAt is { } next ? string.Format(AppResources.AiPlan_QuotaRuleFormat, ResetText(next))
                : AppResources.AiPlan_QuotaRule;
            IsPaused = quota.Paused;
            if (quota.Paused)
            {
                // maintenance (admin panel): nothing can be asked for; the admin's note when there is one
                QuotaText = quota.Message ?? AppResources.AiPlan_Paused;
                BlockedText = quota.Message ?? AppResources.AiPlan_PausedText;
                Remaining = 0;
                return;
            }
            BlockedText = _period is null ? AppResources.AiPlan_QuotaNoneText
                : _period.NeverResets ? AppResources.AiPlan_QuotaNoneNever
                : _period.ResetsAt is { } at ? string.Format(AppResources.AiPlan_QuotaNoneFormat, ResetText(at))
                : AppResources.AiPlan_QuotaNoneText;
            Remaining = quota.Remaining;
            QuotaText = quota.Remaining > 0
                ? string.Format(AppResources.AiPlan_QuotaFormat, quota.Remaining, quota.Limit)
                : AppResources.AiPlan_QuotaNone;
        }

        [RelayCommand]
        private void ToggleChoice(AiChoice? choice)
        {
            if (choice is null) return;

            if (choice.Group == GroupInterest)
            {
                if (!choice.IsSelected && Interests.Count(i => i.IsSelected) >= MaxInterests)
                    return;
                choice.IsSelected = !choice.IsSelected;
                return;
            }

            var group = choice.Group switch
            {
                GroupPace => Paces,
                GroupBudget => Budgets,
                _ => Companions,
            };
            foreach (var other in group)
                other.IsSelected = ReferenceEquals(other, choice);
        }

        /// <summary>
        /// Google Play's AI-generated content policy: users can report an offensive or harmful suggestion. Opens a mail
        /// to support naming the destination and the summary shown - nothing else from the trip.
        /// </summary>
        [RelayCommand]
        private Task ReportContentAsync() => RunSafeAsync(() =>
            SupportContact.ComposeOrShowAddressAsync(Dialogs, AppResources.AiPlan_ReportSubject,
                string.Format(CultureInfo.CurrentCulture, AppResources.AiPlan_ReportBodyFormat,
                    _trip?.City ?? string.Empty, Summary ?? string.Empty)));

        /// <summary>The answers as the Edge Function expects them.</summary>
        internal AiPlanRequest BuildRequest(Trip trip, IReadOnlyList<string> existingPlaces) => new(
            Language: CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? "en" : "tr",
            Destination: string.IsNullOrWhiteSpace(trip.Country) ? trip.City : $"{trip.City}, {trip.Country}",
            StartDate: trip.StartDate.Date,
            Days: Math.Max(trip.DurationInDays, 1),
            Interests: Interests.Where(i => i.IsSelected).Select(i => i.Key).ToList(),
            Pace: Paces.FirstOrDefault(p => p.IsSelected)?.Key ?? "balanced",
            Budget: Budgets.FirstOrDefault(b => b.IsSelected)?.Key ?? "mid",
            Companions: Companions.FirstOrDefault(c => c.IsSelected)?.Key ?? "couple",
            Notes: InputLimits.Truncate(Notes.Trim(), 200)!,
            ExistingPlaces: existingPlaces,
            City: trip.City,
            Country: trip.Country);

        [RelayCommand]
        private Task GenerateAsync() => RunSafeAsync(async () =>
        {
            if (_trip is null || IsGenerating) return;

            IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;
            if (!CanGenerate) return;

            // TP-25: the answers go to OpenAI (USA) - explicit consent first (KVKK md. 9)
            if (_consent is not null && !await _consent.EnsureAsync(ConsentKind.AiPlanning))
                return;

            // TP-57: one plan at a time (also one of another trip)
            if (_jobs.IsRunning)
            {
                await ShowJobAsync();
                return;
            }

            ErrorText = null;
            var existing = (await _places.GetByTripIdAsync(_trip.Id)).Select(p => p.Name).ToList();
            if (!_jobs.Start(_trip.Id, _trip.Name, BuildRequest(_trip, existing)))
                ErrorText = AppResources.AiPlan_Busy;
            await ShowJobAsync();
        }, AppResources.AiPlan_ErrorFailed);

        /// <summary>What the waiting screen says the planner is doing ("Gezilecek yerler seçiliyor…").</summary>
        [ObservableProperty]
        private string generatingStatus = string.Empty;

        /// <summary>TP-77: how far the waiting messages have got, 0..1, for the progress bar under them.</summary>
        [ObservableProperty]
        private double generatingProgress;

        /// <summary>TP-77: "Adım 3/6".</summary>
        [ObservableProperty]
        private string generatingStepText = string.Empty;

        /// <summary>How long each waiting message stays before the next one.</summary>
        internal static TimeSpan StepInterval { get; set; } = TimeSpan.FromSeconds(4);

        internal static IReadOnlyList<string> GeneratingSteps => new[]
        {
            AppResources.AiPlan_Step_Places,
            AppResources.AiPlan_Step_Group,
            AppResources.AiPlan_Step_Times,
            AppResources.AiPlan_Step_Food,
            AppResources.AiPlan_Step_Map,
            AppResources.AiPlan_Step_Finish,
        };

        private void StartSteps(DateTime startedUtc)
        {
            if (_steps is not null)
                return;
            _steps = new CancellationTokenSource();
            // back on the page later: the message the plan has got to by now
            var at = (int)Math.Max(0, (DateTime.UtcNow - startedUtc) / StepInterval);
            ShowGeneratingStepsAsync(at, _steps.Token).FireAndForget();
        }

        private void StopSteps()
        {
            _steps?.Cancel();
            _steps = null;
        }

        /// <summary>
        /// Walks through the waiting messages while the plan is being made (the page shows a fixed rough duration
        /// under them); the last one stays until the plan arrives.
        /// </summary>
        private async Task ShowGeneratingStepsAsync(int from, CancellationToken cancellationToken)
        {
            var steps = GeneratingSteps;
            for (var i = Math.Min(from, steps.Count - 1); i < steps.Count && !cancellationToken.IsCancellationRequested; i++)
            {
                GeneratingStatus = steps[i];
                GeneratingProgress = (i + 1) / (double)steps.Count;
                GeneratingStepText = string.Format(System.Globalization.CultureInfo.CurrentCulture, AppResources.AiPlan_StepFormat, i + 1, steps.Count);
                if (i == steps.Count - 1)
                    return;
                try
                {
                    await Task.Delay(StepInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        internal static string MessageFor(AiPlanErrorKind kind) => kind switch
        {
            AiPlanErrorKind.Offline => AppResources.AiPlan_ErrorOffline,
            AiPlanErrorKind.NotSignedIn => AppResources.AiPlan_ErrorSignIn,
            AiPlanErrorKind.AccountDisabled => AppResources.AiPlan_ErrorDisabled,
            AiPlanErrorKind.QuotaExceeded => AppResources.AiPlan_QuotaNone,
            AiPlanErrorKind.NotConfigured => AppResources.AiPlan_ErrorNotConfigured,
            AiPlanErrorKind.Paused => AppResources.AiPlan_Paused,
            AiPlanErrorKind.Busy => AppResources.AiPlan_Busy,
            _ => AppResources.AiPlan_ErrorFailed,
        };

        private void ShowPreview(Trip trip, AiPlanResult result, IReadOnlyList<string> existingPlaces)
        {
            _plan = result.Plan;
            _planRequestId = result.RequestId;
            _planShownAt = Clock.GetTimestamp();
            Days.Clear();
            Places.Clear();
            Todos.Clear();
            Tips.Clear();

            var names = result.Plan.Places.Where(p => !string.IsNullOrEmpty(p.Key))
                .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Name);
            foreach (var day in result.Plan.Days.Where(d => d.Activities.Count > 0).OrderBy(d => d.Day))
            {
                var date = trip.StartDate.Date.AddDays(day.Day - 1);
                var header = string.Format(AppResources.AiPlan_DayFormat, day.Day, date.ToString("d MMM ddd", CultureInfo.CurrentCulture));
                Days.Add(new AiPreviewDay(header, day.Title, day.Activities
                    .Select(a => new AiPreviewActivity(day.Day, a, a.PlaceKey is null ? null : names.GetValueOrDefault(a.PlaceKey)))
                    .ToList()));
            }

            // places the trip already has are not offered again - their activities are linked to them anyway
            var saved = existingPlaces.Select(AiPlannerService.NameKey).ToHashSet();
            foreach (var place in result.Plan.Places.Where(p => !saved.Contains(AiPlannerService.NameKey(p.Name)) &&
                                                                 (p.LocalName is null || !saved.Contains(AiPlannerService.NameKey(p.LocalName)))))
                Places.Add(new AiPreviewPlace(place));
            foreach (var todo in result.Plan.Todos)
                Todos.Add(new AiPreviewTodo(todo));
            foreach (var tip in result.Plan.Tips)
                Tips.Add(tip);

            Summary = result.Plan.Summary;
            TruncatedText = result.TruncatedDays ? string.Format(AppResources.AiPlan_TruncatedFormat, MaxDays) : string.Empty;

            OnPropertyChanged(nameof(HasPlaces));
            OnPropertyChanged(nameof(HasTodos));
            OnPropertyChanged(nameof(HasTips));
            var rows = BuildPreviewRows().ToList();
            AssignCardPositions(rows);
            PreviewRows.ReplaceAll(rows);
            IsPreview = true;
        }

        private IEnumerable<object> BuildPreviewRows()
        {
            if (Days.Count > 0)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewDays, IconKey: "Ic.Calendar");
                foreach (var day in Days)
                {
                    yield return day;
                    foreach (var activity in day.Activities)
                        yield return activity;
                }
            }
            if (HasPlaces)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewPlaces, IconKey: "Ic.Pin");
                foreach (var place in Places)
                    yield return place;
            }
            if (HasTodos)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewTodos, AppResources.AiPlan_PackingHint, "Ic.ListChecks");
                foreach (var todo in Todos)
                    yield return todo;
            }
            if (HasTips)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewTips, IconKey: "Ic.Lightbulb", IsWarning: true);
                foreach (var tip in Tips)
                    yield return new AiPreviewTip(tip);
            }
        }

        /// <summary>
        /// TP-104: the preview is one flat list, drawn as the prototype's cards - each day (its header and activities),
        /// and the places, tasks and tips under their section title, become one card of slices.
        /// </summary>
        internal static void AssignCardPositions(IEnumerable<object> rows) =>
            ListPositions.AssignCards(rows, row => row is AiPreviewDay);

        [RelayCommand]
        private void StartOver()
        {
            IsPreview = false;
            ErrorText = null;
            if (_trip is not null)
                _jobs.Dismiss(_trip.Id);
        }

        [RelayCommand]
        private Task AddSelectedAsync() => RunSafeAsync(async () =>
        {
            if (_trip is null || _plan is null || IsApplying) return;

            var activities = Days.SelectMany(d => d.Activities).Where(a => a.IsSelected).Select(a => (a.Day, a.Activity)).ToList();
            var places = Places.Where(p => p.IsSelected).Select(p => p.Place.Key).ToList();
            var todos = Todos.Where(t => t.IsSelected).Select(t => t.Title).ToList();
            if (activities.Count + places.Count + todos.Count == 0)
            {
                await Dialogs.ShowMessageAsync(AppResources.AiPlan_Title, AppResources.AiPlan_NothingSelected);
                return;
            }

            IsApplying = true;
            AiApplyResult result;
            try
            {
                result = await _planner.ApplyAsync(_trip, new AiPlanSelection(_plan.Places, activities, places, todos));
            }
            finally
            {
                IsApplying = false;
            }

            // TP-54: how long the user looked at the plan before saving it, for the AI plan log (once per plan)
            if (_planRequestId is { } requestId)
            {
                _planRequestId = null;
                _ = _planner.ReportSavedAsync(requestId, Clock.GetElapsedTime(_planShownAt));
            }

            var message = string.Format(AppResources.AiPlan_AddedFormat, result.Activities, result.Places, result.Todos);
            if (result.UnlocatedPlaces > 0)
                message += " " + string.Format(AppResources.AiPlan_ApproximateFormat, result.UnlocatedPlaces);
            _jobs.Dismiss(_trip.Id); // in the trip now
            await Dialogs.ShowMessageAsync(AppResources.AiPlan_Title, message);
            await AfterApplied(_trip.Id);
        }, AppResources.AiPlan_ApplyError);

        /// <summary>Measures the time from the plan to "Plana ekle". Replaced in tests.</summary>
        internal TimeProvider Clock { get; set; } = TimeProvider.System;

        /// <summary>After adding: show the day plan instead of this page. Replaced in tests.</summary>
        internal Func<int, Task> AfterApplied { get; set; } = async tripId =>
        {
            if (Shell.Current is not null)
                await Shell.Current.GoToAsync($"../{Routes.Itinerary}?TripId={tripId}");
        };

        [RelayCommand]
        private Task OpenSignInAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(Routes.Auth));

        /// <summary>TP-57 (was TP-49's "leave?" question): the plan goes on in the background, so the page just closes.</summary>
        [RelayCommand]
        private Task GoBackAsync() => RunSafeAsync(() => Shell.Current.GoToAsync(".."));
    }
}
