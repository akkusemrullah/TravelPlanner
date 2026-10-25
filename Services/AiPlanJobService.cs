using System.Text.Json;
using TravelPlanner.Helpers;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    public enum AiPlanJobPhase
    {
        Running,
        Ready,
        Failed
    }

    /// <summary>TP-57: the one AI plan being made in the background, or made and not yet looked at.</summary>
    public sealed class AiPlanJob
    {
        /// <summary>The request's id (a uuid), also the server's job id (migration 39).</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>The account that asked: another account on the phone doesn't see it.</summary>
        public string? UserId { get; set; }

        public int TripId { get; set; }
        public string TripName { get; set; } = string.Empty;
        public DateTime StartedUtc { get; set; }
        public AiPlanJobPhase Phase { get; set; }
        public AiPlanResult? Result { get; set; }
        public AiPlanErrorKind? Error { get; set; }

        /// <summary>Plans left today, when the server said.</summary>
        public int? Remaining { get; set; }

        /// <summary>The user saw that it is ready (on the planner page, or "Göster?" was asked).</summary>
        public bool Offered { get; set; }
    }

    /// <summary>
    /// TP-57: makes the AI plan outside the planner page, so leaving the page (or closing the app) doesn't lose it.
    /// The request gets an id kept on the phone; plan-trip makes the plan in the background on the server and this
    /// asks for it until it is ready - also after the app was closed and opened again (<see cref="ResumeAsync"/>).
    /// One plan at a time. When it is ready and its page isn't open: "Planın hazır" notification.
    /// </summary>
    public interface IAiPlanJobs
    {
        /// <summary>The signed-in account's plan, being made or waiting to be looked at; null when there is none.</summary>
        AiPlanJob? Current { get; }

        /// <summary>A plan is being made: no second one until it is done.</summary>
        bool IsRunning { get; }

        /// <summary><see cref="Current"/> when it is for this trip.</summary>
        AiPlanJob? For(int tripId);

        /// <summary>Raised (on any thread) when <see cref="Current"/> changes.</summary>
        event EventHandler? Changed;

        /// <summary>
        /// Raised (on any thread) when a plan got ready while its page wasn't open and no notification could be
        /// shown: the app offers it itself.
        /// </summary>
        event EventHandler? ReadyUnnoticed;

        /// <summary>The trip whose planner page is on screen (no notification for it), or null.</summary>
        int? WatchedTripId { get; set; }

        /// <summary>The app is on screen (set by the App on sleep / resume).</summary>
        bool InForeground { get; set; }

        /// <summary>Starts making a plan; false when one is already being made.</summary>
        bool Start(int tripId, string tripName, AiPlanRequest request);

        /// <summary>The plan of this trip was added, thrown away or its error shown: forget it.</summary>
        void Dismiss(int tripId);

        /// <summary>The user has been told the plan is ready.</summary>
        void MarkOffered();

        /// <summary>At start-up and on resume: goes on asking for a plan that was being made.</summary>
        Task ResumeAsync();
    }

    public class AiPlanJobs : IAiPlanJobs
    {
        /// <summary>Preference: the current job as JSON (the plan included, so it survives a restart).</summary>
        internal const string StoreKey = "ai.plan_job";

        /// <summary>A plan made but never looked at is dropped after this.</summary>
        public static readonly TimeSpan KeepReady = TimeSpan.FromDays(3);

        /// <summary>Without an answer from the server (offline) for this long, asking stops until the next resume.</summary>
        public static readonly TimeSpan MaxOffline = TimeSpan.FromMinutes(6);

        /// <summary>How often the server is asked while the plan is being made. Replaced in tests.</summary>
        internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(4);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IAiPlannerService _planner;
        private readonly IPreferences _preferences;
        private readonly INotificationService? _notifications;
        private readonly object _gate = new();
        private AiPlanJob? _job;
        private bool _loaded;

        /// <summary>The job this process is working on (starting or asking): <see cref="ResumeAsync"/> leaves it alone.</summary>
        private string? _activeId;

        public AiPlanJobs(IAiPlannerService planner, IPreferences preferences, INotificationService? notifications = null)
        {
            _planner = planner;
            _preferences = preferences;
            _notifications = notifications;
        }

        /// <summary>The signed-in account (replaced in tests).</summary>
        internal Func<string?> CurrentUser { get; set; } = () => ChangeTracker.CurrentUserId;

        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public event EventHandler? Changed;
        public event EventHandler? ReadyUnnoticed;

        public int? WatchedTripId { get; set; }
        public bool InForeground { get; set; } = true;

        public AiPlanJob? Current
        {
            get
            {
                lock (_gate)
                {
                    Load();
                    return _job is { } job && job.UserId == CurrentUser() ? job : null;
                }
            }
        }

        public bool IsRunning => Current?.Phase == AiPlanJobPhase.Running;

        public AiPlanJob? For(int tripId) => Current is { } job && job.TripId == tripId ? job : null;

        public bool Start(int tripId, string tripName, AiPlanRequest request)
        {
            AiPlanJob job;
            lock (_gate)
            {
                if (IsRunning)
                    return false;
                job = new AiPlanJob
                {
                    Id = Guid.NewGuid().ToString(),
                    UserId = CurrentUser(),
                    TripId = tripId,
                    TripName = tripName,
                    StartedUtc = UtcNow(),
                    Phase = AiPlanJobPhase.Running,
                };
                _job = job;
                _activeId = job.Id;
                Save();
            }
            Raise();
            RunAsync(job, request).FireAndForget();
            return true;
        }

        private async Task RunAsync(AiPlanJob job, AiPlanRequest request)
        {
            try
            {
                var start = await _planner.StartPlanAsync(request, job.Id);
                if (start.Result is { } result)
                {
                    await CompleteAsync(job, result); // an older server: the plan came in the answer
                    return;
                }
                Update(job, j => j.Remaining = start.Remaining);
                await PollAsync(job, waitFirst: true);
            }
            catch (AiPlanException ex)
            {
                Fail(job, ex.Kind);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Plan failed: {ex}");
                Fail(job, AiPlanErrorKind.Failed);
            }
            finally
            {
                lock (_gate)
                {
                    if (_activeId == job.Id)
                        _activeId = null;
                }
            }
        }

        public async Task ResumeAsync()
        {
            AiPlanJob? job;
            lock (_gate)
            {
                job = Current;
                if (job is null || _activeId == job.Id)
                    return;
                if (job.Phase != AiPlanJobPhase.Running)
                {
                    if (UtcNow() - job.StartedUtc > KeepReady)
                        Forget(job);
                    return;
                }
                _activeId = job.Id;
            }

            try
            {
                await PollAsync(job, waitFirst: false);
            }
            finally
            {
                lock (_gate)
                {
                    if (_activeId == job.Id)
                        _activeId = null;
                }
            }
        }

        /// <summary>Asks the server until the plan is ready or failed; stops (still running) after a long time offline.</summary>
        private async Task PollAsync(AiPlanJob job, bool waitFirst)
        {
            var lastAnswer = UtcNow();
            for (var first = true; ; first = false)
            {
                if (!first || waitFirst)
                    await Task.Delay(PollInterval);
                if (!IsCurrent(job))
                    return; // dismissed meanwhile, or another account signed in

                AiPlanJobState state;
                try
                {
                    state = await _planner.GetJobAsync(job.Id);
                }
                catch (AiPlanException ex) when (ex.Kind is AiPlanErrorKind.Offline or AiPlanErrorKind.Failed)
                {
                    if (UtcNow() - lastAnswer > MaxOffline)
                        return; // the next resume asks again
                    continue;
                }
                catch (AiPlanException ex)
                {
                    Fail(job, ex.Kind); // signed out, account disabled
                    return;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AI] Plan status check failed: {ex.Message}");
                    if (UtcNow() - lastAnswer > MaxOffline)
                        return;
                    continue;
                }

                lastAnswer = UtcNow();
                switch (state.Status)
                {
                    case AiPlanJobStatus.Running:
                        continue;
                    case AiPlanJobStatus.Done when state.Result is { } result:
                        await CompleteAsync(job, result);
                        return;
                    case AiPlanJobStatus.Failed:
                        Fail(job, state.Error ?? AiPlanErrorKind.Failed, state.Remaining);
                        return;
                    default:
                        // never reached the server (the app was closed while sending), or cleared / too old
                        Fail(job, AiPlanErrorKind.Failed);
                        return;
                }
            }
        }

        private async Task CompleteAsync(AiPlanJob job, AiPlanResult result)
        {
            if (!Update(job, j =>
                {
                    j.Phase = AiPlanJobPhase.Ready;
                    j.Result = result;
                    j.Remaining = result.Remaining;
                }))
                return;

            // the phone keeps it now: off the server
            _planner.ClearJobAsync(job.Id).FireAndForget();

            if (InForeground && WatchedTripId == job.TripId)
                return; // the page shows it

            var notified = false;
            try
            {
                notified = _notifications is not null &&
                           await _notifications.ShowAiPlanAsync(AppResources.AiPlan_ReadyTitle,
                               string.Format(AppResources.AiPlan_ReadyFormat, job.TripName));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Plan notification failed: {ex.Message}");
            }
            if (!notified && InForeground)
                ReadyUnnoticed?.Invoke(this, EventArgs.Empty);
        }

        private void Fail(AiPlanJob job, AiPlanErrorKind kind, int? remaining = null)
        {
            if (!Update(job, j =>
                {
                    j.Phase = AiPlanJobPhase.Failed;
                    j.Error = kind;
                    j.Remaining = remaining ?? j.Remaining;
                }))
                return;

            if (InForeground && WatchedTripId == job.TripId)
                return;
            // whoever left the page waits for "ready": say it didn't work out (the page tells why)
            _notifications?.ShowAiPlanAsync(AppResources.AiPlan_FailedTitle, string.Format(AppResources.AiPlan_FailedFormat, job.TripName))
                .FireAndForget();
        }

        public void Dismiss(int tripId)
        {
            AiPlanJob? job;
            lock (_gate)
            {
                job = For(tripId);
                if (job is null || job.Phase == AiPlanJobPhase.Running)
                    return;
                Forget(job);
            }
            if (job.Phase == AiPlanJobPhase.Failed)
                _planner.ClearJobAsync(job.Id).FireAndForget();
        }

        public void MarkOffered()
        {
            if (Current is { } job)
                Update(job, j => j.Offered = true);
        }

        private bool IsCurrent(AiPlanJob job)
        {
            lock (_gate)
                return ReferenceEquals(Current, job);
        }

        /// <summary>Changes the job when it is still the current one, saves it and says so.</summary>
        private bool Update(AiPlanJob job, Action<AiPlanJob> change)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_job, job))
                    return false;
                change(job);
                Save();
            }
            Raise();
            return true;
        }

        /// <summary>Called under the lock.</summary>
        private void Forget(AiPlanJob job)
        {
            if (!ReferenceEquals(_job, job))
                return;
            _job = null;
            _preferences.Remove(StoreKey);
            Raise();
        }

        private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Called under the lock.</summary>
        private void Load()
        {
            if (_loaded)
                return;
            _loaded = true;
            var text = _preferences.Get(StoreKey, string.Empty);
            if (string.IsNullOrEmpty(text))
                return;
            try
            {
                _job = JsonSerializer.Deserialize<AiPlanJob>(text, Json);
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Saved plan unreadable: {ex.Message}");
                _preferences.Remove(StoreKey);
            }
        }

        /// <summary>Called under the lock.</summary>
        private void Save()
        {
            if (_job is null)
                _preferences.Remove(StoreKey);
            else
                _preferences.Set(StoreKey, JsonSerializer.Serialize(_job, Json));
        }
    }
}
