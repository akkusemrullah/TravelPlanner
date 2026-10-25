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

    public partial class AiPreviewActivity : ObservableObject
    {
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

    public sealed class AiPreviewDay
    {
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

    public partial class AiPreviewPlace : ObservableObject
    {
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

    /// <summary>A section title in the preview list ("GÜN GÜN PLAN"), with an optional caption under it.</summary>
    public sealed record AiPreviewSection(string Title, string? Hint = null)
    {
        public bool HasHint => !string.IsNullOrEmpty(Hint);
    }

    /// <summary>A read-only local tip in the preview list.</summary>
    public sealed record AiPreviewTip(string Text);

    public partial class AiPreviewTodo : ObservableObject
    {
        public AiPreviewTodo(string title) => Title = title;

        public string Title { get; }

        [ObservableProperty]
        private bool isSelected = true;
    }

    /// <summary>
    /// AI trip planner: 4 questions (interests, pace, budget, company) + an optional note → the plan from the
    /// "plan-trip" Edge Function → a preview where everything can be unticked → "Seçilenleri plana ekle".
    /// Needs an account (the daily quota is per account) and a connection; only editors of the trip get here.
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
        private readonly ITripService _trips;
        private readonly IPlaceService _places;
        private readonly IAccountService _account;
        private readonly IConnectivity _connectivity;
        private readonly IConsentService? _consent;
        private Trip? _trip;
        private AiTripPlan? _plan;
        private string? _planRequestId;
        private long _planShownAt;

        public AiPlannerViewModel(IAiPlannerService planner, ITripService trips, IPlaceService places,
                                  IAccountService account, IConnectivity connectivity, IDialogService dialogs,
                                  IConsentService? consent = null)
            : base(dialogs)
        {
            _consent = consent;
            _planner = planner;
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
        [NotifyPropertyChangedFor(nameof(IsAsking), nameof(CanGenerate))]
        private bool isGenerating;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsFormVisible))]
        private bool isPreview;

        [ObservableProperty]
        private bool isApplying;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanGenerate))]
        private int? remaining;

        [ObservableProperty]
        private string quotaText = string.Empty;

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
        public bool IsAsking => !IsGenerating && !IsPreview;

        /// <summary>The questions / spinner area (everything but the preview list).</summary>
        public bool IsFormVisible => !IsPreview;
        public bool HasError => !string.IsNullOrEmpty(ErrorText);
        public bool HasTruncatedText => !string.IsNullOrEmpty(TruncatedText);
        public bool HasPlaces => Places.Count > 0;
        public bool HasTodos => Todos.Count > 0;
        public bool HasTips => Tips.Count > 0;
        public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
        public bool CanGenerate => IsSignedIn && IsOnline && !IsGenerating && CanEdit && Remaining is not 0;

        partial void OnSummaryChanged(string value) => OnPropertyChanged(nameof(HasSummary));

        [RelayCommand]
        private Task LoadAsync() => RunLoadAsync(async () =>
        {
            if (TripId == 0) return;

            _trip = await _trips.GetByIdAsync(TripId);
            if (_trip is null) return;

            await RefreshAccessAsync(TripId);
            OnPropertyChanged(nameof(CanGenerate));

            var days = Math.Min(_trip.DurationInDays, AiPlannerService.MaxDays);
            IntroText = string.Format(AppResources.AiPlan_IntroFormat, _trip.City, days);
            IsSignedIn = _account.IsSignedIn;
            IsOnline = _connectivity.NetworkAccess == NetworkAccess.Internet;

            if (IsSignedIn && IsOnline && !IsPreview)
                ShowQuota(await _planner.GetQuotaAsync());
        }, AppResources.AiPlan_LoadError);

        // The preview must not be thrown away by a sync that reloads the page.
        protected override bool ReloadOnRemoteChanges => false;

        private void ShowQuota(AiQuota? quota)
        {
            if (quota is null)
                return;
            if (quota.Paused)
            {
                // maintenance (admin panel): nothing can be asked for; the admin's note when there is one
                Remaining = 0;
                QuotaText = quota.Message ?? AppResources.AiPlan_Paused;
                return;
            }
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

            ErrorText = null;
            var existing = (await _places.GetByTripIdAsync(_trip.Id)).Select(p => p.Name).ToList();

            IsGenerating = true;
            using var steps = new CancellationTokenSource();
            ShowGeneratingStepsAsync(steps.Token).FireAndForget();
            AiPlanResult result;
            try
            {
                result = await _planner.CreatePlanAsync(BuildRequest(_trip, existing));
            }
            catch (AiPlanException ex)
            {
                ErrorText = ex.Kind == AiPlanErrorKind.Paused ? null : MessageFor(ex.Kind); // paused: the quota line says it
                if (ex.Kind == AiPlanErrorKind.QuotaExceeded)
                    ShowQuota(new AiQuota(0, 0));
                else if (ex.Kind == AiPlanErrorKind.Paused)
                    ShowQuota(new AiQuota(0, 0, Paused: true));
                return;
            }
            finally
            {
                steps.Cancel();
                IsGenerating = false;
            }

            ShowPreview(_trip, result, existing);
            ShowQuota(new AiQuota(result.Remaining, Math.Max(result.Remaining, 1)));
            if (result.Remaining > 0)
                QuotaText = string.Format(AppResources.AiPlan_QuotaLeftFormat, result.Remaining);
        }, AppResources.AiPlan_ErrorFailed);

        /// <summary>What the waiting screen says the planner is doing ("Gezilecek yerler seçiliyor…").</summary>
        [ObservableProperty]
        private string generatingStatus = string.Empty;

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

        /// <summary>
        /// Walks through the waiting messages while the plan is being made (the page shows a fixed rough duration
        /// under them); the last one stays until the plan arrives.
        /// </summary>
        private async Task ShowGeneratingStepsAsync(CancellationToken cancellationToken)
        {
            var steps = GeneratingSteps;
            for (var i = 0; i < steps.Count && !cancellationToken.IsCancellationRequested; i++)
            {
                GeneratingStatus = steps[i];
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
            TruncatedText = result.TruncatedDays ? string.Format(AppResources.AiPlan_TruncatedFormat, AiPlannerService.MaxDays) : string.Empty;

            OnPropertyChanged(nameof(HasPlaces));
            OnPropertyChanged(nameof(HasTodos));
            OnPropertyChanged(nameof(HasTips));
            PreviewRows.ReplaceAll(BuildPreviewRows());
            IsPreview = true;
        }

        private IEnumerable<object> BuildPreviewRows()
        {
            if (Days.Count > 0)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewDays);
                foreach (var day in Days)
                {
                    yield return day;
                    foreach (var activity in day.Activities)
                        yield return activity;
                }
            }
            if (HasPlaces)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewPlaces);
                foreach (var place in Places)
                    yield return place;
            }
            if (HasTodos)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewTodos, AppResources.AiPlan_PackingHint);
                foreach (var todo in Todos)
                    yield return todo;
            }
            if (HasTips)
            {
                yield return new AiPreviewSection(AppResources.AiPlan_PreviewTips);
                foreach (var tip in Tips)
                    yield return new AiPreviewTip(tip);
            }
        }

        [RelayCommand]
        private void StartOver()
        {
            IsPreview = false;
            ErrorText = null;
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

        [RelayCommand]
        private async Task GoBackAsync()
        {
            if (await ConfirmLeaveAsync())
                await Shell.Current.GoToAsync("..");
        }

        /// <summary>
        /// TP-49: leaving while the plan is being made still costs a plan (OpenAI answers anyway) and loses it, so
        /// ask first. True when the page may close.
        /// </summary>
        internal async Task<bool> ConfirmLeaveAsync() =>
            !IsGenerating
            || await Dialogs.ConfirmAsync(AppResources.AiPlan_LeaveTitle, AppResources.AiPlan_LeaveMessage,
                                          AppResources.AiPlan_LeaveAccept, AppResources.AiPlan_LeaveStay);
    }
}
