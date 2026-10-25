using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// AI trip planning: asks the "plan-trip" Edge Function (Supabase → OpenAI, key kept on the server) for a plan
    /// and puts the parts the user picked into the trip - activities into the day plan (with start/end times and
    /// linked to their place by the plan's place key), places (located with the app's own geocoder; the AI's
    /// coordinates are only a hint) and pre-trip to-dos. Packing items are not asked for: the smart packing list
    /// reads the new plan by itself.
    /// </summary>
    public interface IAiPlannerService
    {
        /// <summary>Plans left today for this account on this device; null when it can't be checked (offline, not signed in).</summary>
        Task<AiQuota?> GetQuotaAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Asks for a plan (uses one of today's plans). TP-57: with <paramref name="jobId"/> the server makes it in the
        /// background and answers at once (<see cref="AiPlanStart.JobId"/>); an older server sends the plan itself.
        /// Throws <see cref="AiPlanException"/>.
        /// </summary>
        Task<AiPlanStart> StartPlanAsync(AiPlanRequest request, string? jobId, CancellationToken cancellationToken = default);

        /// <summary>TP-57: how the background plan <paramref name="jobId"/> is doing. Throws <see cref="AiPlanException"/> (offline…).</summary>
        Task<AiPlanJobState> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

        /// <summary>TP-57: the app has the plan: it is deleted from the server. Never throws.</summary>
        Task ClearJobAsync(string jobId, CancellationToken cancellationToken = default);

        /// <summary>Adds the chosen parts of a plan to <paramref name="trip"/>.</summary>
        Task<AiApplyResult> ApplyAsync(Trip trip, AiPlanSelection selection, CancellationToken cancellationToken = default);

        /// <summary>
        /// TP-54: tells the server's AI plan log how long after the plan came the user saved it. Never throws: the log
        /// is not worth an error message.
        /// </summary>
        Task ReportSavedAsync(string requestId, TimeSpan afterPlan, CancellationToken cancellationToken = default);
    }

    public class AiPlannerService : IAiPlannerService
    {
        public const string FunctionName = "plan-trip";

        /// <summary>Same as MAX_DAYS in the Edge Function: longer trips get their first days planned.</summary>
        public const int MaxDays = 10;

        /// <summary>A place found farther than this from the trip's city is another place with the same name (day trips included).</summary>
        public const double MaxPlaceDistanceKm = 120;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ISupabaseClient _cloud;
        private readonly IGeocodingService _geocoding;
        private readonly IPlaceService _places;
        private readonly IActivityService _activities;
        private readonly ITodoService _todos;
        private readonly IDeviceIdentity _device;

        public AiPlannerService(ISupabaseClient cloud, IGeocodingService geocoding, IPlaceService places,
                                IActivityService activities, ITodoService todos, IDeviceIdentity device)
        {
            _device = device;
            _cloud = cloud;
            _geocoding = geocoding;
            _places = places;
            _activities = activities;
            _todos = todos;
        }

        public async Task<AiQuota?> GetQuotaAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // per account AND per device: another account on this phone shares the phone's plans
                var result = await _cloud.RpcAsync("ai_plan_quota_status", new JsonObject { ["p_device"] = _device.Hash }, cancellationToken);
                if (result.ValueKind != JsonValueKind.Object)
                    return null;
                var paused = result.TryGetProperty("paused", out var p) && p.ValueKind == JsonValueKind.True;
                var message = result.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return new AiQuota(result.GetProperty("remaining").GetInt32(), result.GetProperty("limit").GetInt32(), paused,
                    string.IsNullOrWhiteSpace(message) ? null : message.Trim());
            }
            catch (Exception ex) when (ex is SyncException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Quota check failed: {ex.Message}");
                return null;
            }
        }

        public async Task<AiPlanStart> StartPlanAsync(AiPlanRequest request, string? jobId, CancellationToken cancellationToken = default)
        {
            var body = new JsonObject
            {
                ["deviceId"] = _device.Hash,
                ["language"] = request.Language,
                ["destination"] = request.Destination,
                ["startDate"] = request.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["days"] = request.Days,
                ["interests"] = new JsonArray(request.Interests.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()),
                ["pace"] = request.Pace,
                ["budget"] = request.Budget,
                ["companions"] = request.Companions,
                ["notes"] = request.Notes,
                ["existingPlaces"] = new JsonArray(request.ExistingPlaces.Take(30).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
            };
            if (!string.IsNullOrEmpty(jobId))
            {
                body["background"] = true;
                body["jobId"] = jobId;
            }

            if (!string.IsNullOrWhiteSpace(request.City) && await SafeGeocodeCityAsync(request.City, request.Country) is { } center)
                body["center"] = new JsonObject { ["lat"] = center.Latitude, ["lon"] = center.Longitude };

            JsonElement response;
            try
            {
                response = await _cloud.InvokeFunctionAsync(FunctionName, body, cancellationToken);
            }
            catch (SyncException ex)
            {
                throw new AiPlanException(KindOf(ex), ex.Message, ex);
            }

            try
            {
                if (response.ValueKind == JsonValueKind.Object && !response.TryGetProperty("plan", out _) &&
                    response.TryGetProperty("jobId", out var job) && job.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(job.GetString()))
                    return new AiPlanStart(null, job.GetString(), RemainingOf(response) ?? 0);
                var result = ResultOf(response);
                return new AiPlanStart(result, null, result.Remaining);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AiPlanException(AiPlanErrorKind.Failed, "Unexpected answer from the planner.", ex);
            }
        }

        /// <summary>plan-trip's answer with a plan: { plan, remaining, truncatedDays, requestId }.</summary>
        private static AiPlanResult ResultOf(JsonElement response)
        {
            var plan = response.GetProperty("plan").Deserialize<AiTripPlan>(Json) ?? new AiTripPlan();
            var truncated = response.TryGetProperty("truncatedDays", out var t) && t.ValueKind == JsonValueKind.True;
            var requestId = response.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            return new AiPlanResult(plan, RemainingOf(response) ?? 0, truncated, string.IsNullOrWhiteSpace(requestId) ? null : requestId);
        }

        private static int? RemainingOf(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty("remaining", out var r) && r.ValueKind == JsonValueKind.Number
                ? r.GetInt32()
                : null;

        public async Task<AiPlanJobState> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            JsonElement job;
            try
            {
                job = await _cloud.RpcAsync("ai_plan_job", new JsonObject { ["p_id"] = jobId }, cancellationToken);
            }
            catch (SyncException ex) when (ex.Status == 404)
            {
                return new AiPlanJobState(AiPlanJobStatus.Missing); // migration 39 not run: there are no jobs
            }
            catch (SyncException ex)
            {
                throw new AiPlanException(KindOf(ex), ex.Message, ex);
            }

            try
            {
                if (job.ValueKind != JsonValueKind.Object)
                    return new AiPlanJobState(AiPlanJobStatus.Missing);
                var remaining = RemainingOf(job);
                switch (job.TryGetProperty("status", out var s) ? s.GetString() : null)
                {
                    case "running":
                        return new AiPlanJobState(AiPlanJobStatus.Running, Remaining: remaining);
                    case "done":
                        var result = ResultOf(job.GetProperty("result"));
                        return new AiPlanJobState(AiPlanJobStatus.Done, result, Remaining: remaining ?? result.Remaining);
                    case "failed":
                        var code = job.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                        return new AiPlanJobState(AiPlanJobStatus.Failed, Error: KindOf(code) ?? AiPlanErrorKind.Failed, Remaining: remaining);
                    default:
                        return new AiPlanJobState(AiPlanJobStatus.Missing);
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new AiPlanException(AiPlanErrorKind.Failed, "Unexpected answer from the planner.", ex);
            }
        }

        public async Task ClearJobAsync(string jobId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _cloud.RpcAsync("ai_plan_job_clear", new JsonObject { ["p_id"] = jobId }, cancellationToken);
            }
            catch (Exception ex) when (ex is SyncException or OperationCanceledException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Plan job not cleared: {ex.Message}"); // the server drops it after a day
            }
        }

        /// <summary>The kind of a plan-trip error code ("quota_exceeded"…); null for one it doesn't know.</summary>
        internal static AiPlanErrorKind? KindOf(string? code) => code switch
        {
            "quota_exceeded" => AiPlanErrorKind.QuotaExceeded,
            "account_disabled" => AiPlanErrorKind.AccountDisabled,
            "not_signed_in" => AiPlanErrorKind.NotSignedIn,
            "invalid_request" => AiPlanErrorKind.InvalidRequest,
            "ai_not_configured" => AiPlanErrorKind.NotConfigured,
            "ai_paused" => AiPlanErrorKind.Paused,
            "plan_running" => AiPlanErrorKind.Busy,
            _ => null,
        };

        internal static AiPlanErrorKind KindOf(SyncException ex) => ex.Kind switch
        {
            SyncErrorKind.Network => AiPlanErrorKind.Offline,
            SyncErrorKind.NotSignedIn => AiPlanErrorKind.NotSignedIn,
            _ => KindOf(ex.ServerCode) ?? ex.Status switch
            {
                503 => AiPlanErrorKind.Paused,
                429 => AiPlanErrorKind.QuotaExceeded,
                403 => AiPlanErrorKind.AccountDisabled,
                404 => AiPlanErrorKind.NotConfigured, // the function isn't deployed
                409 => AiPlanErrorKind.Busy,
                _ => AiPlanErrorKind.Failed,
            },
        };

        public async Task ReportSavedAsync(string requestId, TimeSpan afterPlan, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(requestId))
                return;
            try
            {
                var seconds = (int)Math.Clamp(Math.Round(afterPlan.TotalSeconds), 0, 86400);
                await _cloud.RpcAsync("ai_plan_saved", new JsonObject { ["p_request_id"] = requestId, ["p_seconds"] = seconds }, cancellationToken);
            }
            catch (Exception ex) when (ex is SyncException or OperationCanceledException or InvalidOperationException)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Save time not reported: {ex.Message}");
            }
        }

        public async Task<AiApplyResult> ApplyAsync(Trip trip, AiPlanSelection selection, CancellationToken cancellationToken = default)
        {
            // ---- Places: the plan's keys → places of the trip. Ones the trip already has are reused (by name or
            //      local name), the ticked new ones are located near the trip's city ----
            var existingPlaces = await _places.GetByTripIdAsync(trip.Id);
            var byName = new Dictionary<string, Place>();
            foreach (var place in existingPlaces)
                byName.TryAdd(NameKey(place.Name), place);

            var byKey = new Dictionary<string, Place>();
            foreach (var suggested in selection.PlanPlaces)
            {
                if (FindExisting(suggested, byName) is { } existing && !string.IsNullOrEmpty(suggested.Key))
                    byKey.TryAdd(suggested.Key, existing);
            }

            (double Latitude, double Longitude)? city = null;
            var cityLooked = false;
            int addedPlaces = 0, unlocated = 0;
            var wanted = new HashSet<string>(selection.PlaceKeys);

            foreach (var suggested in selection.PlanPlaces.Where(p => wanted.Contains(p.Key)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Helpers.InputLimits.Truncate(suggested.Name.Trim(), 150)!;
                if (name.Length == 0 || byKey.ContainsKey(suggested.Key) || FindExisting(suggested, byName) is not null)
                    continue;

                (double Latitude, double Longitude)? located;
                if (suggested.Verified && suggested.Lat is { } lat && suggested.Lon is { } lon && PlaceService.IsValidCoordinate(lat, lon))
                {
                    located = (lat, lon); // the server already found it on the map
                }
                else
                {
                    if (!cityLooked)
                    {
                        cityLooked = true;
                        city = await SafeGeocodeCityAsync(trip.City, trip.Country);
                    }
                    located = await LocateAsync(suggested, trip, city, cancellationToken);
                }
                if (located is null)
                {
                    // Not on the map: better no pin than a wrong one. Its activities keep the name in their note.
                    unlocated++;
                    continue;
                }

                var place = new Place
                {
                    TripId = trip.Id,
                    Name = name,
                    Description = Helpers.InputLimits.Truncate(DescriptionOf(suggested), 500),
                    Latitude = located.Value.Latitude,
                    Longitude = located.Value.Longitude,
                };
                await _places.SaveAsync(place);
                byName.TryAdd(NameKey(name), place);
                byKey[suggested.Key] = place;
                addedPlaces++;
            }

            // ---- Activities: into the day plan with their times, linked to their place; adding the same plan
            //      twice doesn't double the day plan ----
            var planPlaces = selection.PlanPlaces.Where(p => !string.IsNullOrEmpty(p.Key))
                .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First());
            var already = new HashSet<(DateTime, string)>(
                (await _activities.GetByTripIdAsync(trip.Id)).Select(a => (a.Date.Date, NameKey(a.Title))));

            var addedActivities = 0;
            var lastDay = trip.EndDate.Date;
            foreach (var (day, suggested) in selection.Activities)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var date = trip.StartDate.Date.AddDays(Math.Max(day, 1) - 1);
                var title = Helpers.InputLimits.Truncate(suggested.Title.Trim(), 150)!;
                if (date > lastDay || title.Length == 0 || !already.Add((date, NameKey(title))))
                    continue;

                Place? linked = null;
                AiPlanPlace? planPlace = null;
                if (!string.IsNullOrEmpty(suggested.PlaceKey))
                {
                    byKey.TryGetValue(suggested.PlaceKey, out linked);
                    planPlaces.TryGetValue(suggested.PlaceKey, out planPlace);
                }

                var note = suggested.Note?.Trim();
                if (linked is null && planPlace is not null &&
                    !NameKey(title).Contains(NameKey(planPlace.Name), StringComparison.Ordinal))
                {
                    // keep the place's name even when it isn't on the map
                    var where = planPlace.Name.Trim();
                    note = string.IsNullOrWhiteSpace(note) ? where : $"{where} · {note}";
                }

                var (start, end) = TimesOf(suggested, planPlace);
                await _activities.SaveAsync(new TripActivity
                {
                    TripId = trip.Id,
                    Date = date,
                    StartTime = start,
                    EndTime = end,
                    Title = title,
                    Note = Helpers.InputLimits.Truncate(string.IsNullOrWhiteSpace(note) ? null : note, 500),
                    PlaceId = linked?.Id,
                });
                addedActivities++;
            }

            // ---- To-dos: before the trip; ones already on the list are skipped ----
            var existingTodos = await _todos.GetByTripIdAsync(trip.Id);
            var titles = new HashSet<string>(existingTodos.Select(t => NameKey(t.Title)));
            var addedTodos = 0;
            foreach (var todo in selection.Todos)
            {
                var title = Helpers.InputLimits.Truncate(todo.Trim(), 150)!;
                if (title.Length == 0 || !titles.Add(NameKey(title)))
                    continue;
                await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = title });
                addedTodos++;
            }

            return new AiApplyResult(addedActivities, addedPlaces, addedTodos, unlocated);
        }

        private static Place? FindExisting(AiPlanPlace suggested, IReadOnlyDictionary<string, Place> byName)
        {
            foreach (var name in new[] { suggested.Name, suggested.LocalName })
            {
                if (!string.IsNullOrWhiteSpace(name) && byName.TryGetValue(NameKey(name), out var place))
                    return place;
            }
            return null;
        }

        /// <summary>
        /// Start and end of an activity. Without an end, the place's typical visit length gives one; an end that
        /// isn't after the start (or would pass midnight) is dropped.
        /// </summary>
        internal static (TimeSpan? Start, TimeSpan? End) TimesOf(AiPlanActivity activity, AiPlanPlace? place)
        {
            var start = ParseTime(activity.Start);
            if (start is null)
                return (null, null);

            var end = ParseTime(activity.End);
            if (end is null && place?.VisitMinutes is int minutes && minutes > 0)
                end = start.Value + TimeSpan.FromMinutes(minutes);

            return end is { } e && e > start && e < TimeSpan.FromDays(1) ? (start, e) : (start, null);
        }

        private async Task<(double Latitude, double Longitude)?> SafeGeocodeCityAsync(string city, string? country)
        {
            try
            {
                return await _geocoding.GeocodeAsync(city, country);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] City lookup failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The place's spot on the map, from the app's geocoder (OpenStreetMap), when the server couldn't check it:
        /// only a match whose NAME fits, near the trip's city - the exact name first, then the one nearest the AI's
        /// guess (or the city). The guess itself, or the city centre, is never used as a position: a wrong pin is
        /// worse than none. Descriptive words ("Erciş Şehri Merkezi") are also searched without.
        /// </summary>
        private async Task<(double Latitude, double Longitude)?> LocateAsync(
            AiPlanPlace suggested, Trip trip, (double Latitude, double Longitude)? city, CancellationToken cancellationToken)
        {
            if (city is not { } c)
                return null; // without the city, a namesake on another continent can't be told apart

            (double Latitude, double Longitude) anchor = c;
            if (suggested.Lat is { } lat && suggested.Lon is { } lon && PlaceService.IsValidCoordinate(lat, lon) &&
                DistanceKm(c.Latitude, c.Longitude, lat, lon) <= MaxPlaceDistanceKm)
                anchor = (lat, lon);

            var given = new[] { suggested.LocalName, suggested.Name }.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).ToList();
            var names = given.Concat(given.Select(WithoutGenericWords))
                .Where(n => NameKey(n).Length >= 3)
                .Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

            foreach (var name in names)
            {
                var best = (await SearchAsync($"{name}, {trip.City}", cancellationToken))
                    .Where(p => PlaceService.IsValidCoordinate(p.Latitude, p.Longitude))
                    .Where(p => DistanceKm(c.Latitude, c.Longitude, p.Latitude, p.Longitude) <= MaxPlaceDistanceKm)
                    .Where(p => names.Any(n => SameName(n, p.Name)))
                    .OrderBy(p => names.Any(n => NameKey(n) == NameKey(p.Name)) ? 0 : 1)
                    .ThenBy(p => DistanceKm(anchor.Latitude, anchor.Longitude, p.Latitude, p.Longitude))
                    .FirstOrDefault();
                if (best is not null)
                    return (best.Latitude, best.Longitude);
            }
            return null;
        }

        private static readonly string[] GenericWords =
        {
            "şehir merkezi", "şehri merkezi", "ilçe merkezi", "ilçesi merkezi", "kent merkezi", "merkezi", "kıyı şeridi",
            "sahil şeridi", "kıyısı", "sahili", "çevresi", "bölgesi", "city centre", "city center", "town centre",
            "town center", "downtown", "waterfront", "shoreline", "promenade area", "area",
        };

        /// <summary>"Erciş Şehri Merkezi" → "Erciş", "Van Gölü Kıyı Şeridi" → "Van Gölü" (same list as places.ts).</summary>
        internal static string WithoutGenericWords(string name)
        {
            var text = $" {name} ";
            foreach (var word in GenericWords)
                text = System.Text.RegularExpressions.Regex.Replace(text, $@"\s{System.Text.RegularExpressions.Regex.Escape(word)}(?=\s)", " ",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        private async Task<List<PlaceSuggestion>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            try
            {
                return await _geocoding.SearchPlacesAsync(query, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"[AI] Place lookup failed: {ex.Message}");
                return new List<PlaceSuggestion>();
            }
        }

        /// <summary>Case-, accent- and punctuation-insensitive form of a name ("Café  de-Flore" → "cafe de flore").</summary>
        internal static string NameKey(string? value) =>
            string.Join(' ', PackingPlanMatcher.Fold(value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        /// <summary>"Colosseo" fits "Colosseo"; "Galata Kulesi" fits "Galata Kulesi Müzesi" (one contains the other).</summary>
        internal static bool SameName(string a, string b)
        {
            var x = NameKey(a);
            var y = NameKey(b);
            if (x.Length == 0 || y.Length == 0)
                return false;
            if (x == y)
                return true;
            var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
            return shorter.Length >= 4 && $" {longer} ".Contains($" {shorter} ", StringComparison.Ordinal);
        }

        /// <summary>"Müze · Monti · Colosseo" on the first line, then the AI's description.</summary>
        internal static string DescriptionOf(AiPlanPlace place)
        {
            var header = string.Join(" · ", new[] { AiPlaceCategories.Label(place.Category), place.Area?.Trim(), place.LocalName?.Trim() }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.Join("\n", new[] { header, place.Description.Trim() }.Where(s => s.Length > 0));
        }

        internal static TimeSpan? ParseTime(string? value) =>
            TimeSpan.TryParseExact(value?.Trim(), new[] { @"hh\:mm", @"h\:mm" }, CultureInfo.InvariantCulture, out var time) &&
            time >= TimeSpan.Zero && time < TimeSpan.FromDays(1)
                ? time
                : null;

        /// <summary>Great-circle distance (haversine), good enough to tell "in this city" from "elsewhere".</summary>
        internal static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
        {
            static double Rad(double degrees) => degrees * Math.PI / 180;
            var dLat = Rad(lat2 - lat1);
            var dLon = Rad(lon2 - lon1);
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return 6371 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}
