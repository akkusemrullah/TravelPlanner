namespace TravelPlanner.Models
{
    /// <summary>
    /// What the user tells the AI planner (4 questions + the trip itself). Keys match the Edge Function
    /// (supabase/functions/plan-trip/planner.ts): interests "history"…, pace "relaxed|balanced|packed",
    /// budget "budget|mid|luxury", companions "solo|couple|family|friends". <c>City</c>/<c>Country</c> let the app send
    /// the destination's centre (its own geocoder), so the server looks for places and restaurants in the right spot.
    /// </summary>
    public sealed record AiPlanRequest(
        string Language,
        string Destination,
        DateTime StartDate,
        int Days,
        IReadOnlyList<string> Interests,
        string Pace,
        string Budget,
        string Companions,
        string Notes,
        IReadOnlyList<string> ExistingPlaces,
        string? City = null,
        string? Country = null);

    /// <summary>The AI's plan, as the Edge Function returns it (already cleaned and length-limited there).</summary>
    public sealed class AiTripPlan
    {
        public string Summary { get; set; } = string.Empty;
        public List<AiPlanDay> Days { get; set; } = new();
        public List<AiPlanPlace> Places { get; set; } = new();
        public List<string> Todos { get; set; } = new();
        public List<string> Tips { get; set; } = new();
    }

    public sealed class AiPlanDay
    {
        /// <summary>1 = the trip's first day.</summary>
        public int Day { get; set; }
        public string Title { get; set; } = string.Empty;
        public List<AiPlanActivity> Activities { get; set; } = new();
    }

    public sealed class AiPlanActivity
    {
        /// <summary>Start, "HH:mm" local time, or null.</summary>
        public string? Start { get; set; }

        /// <summary>End, "HH:mm" (only with <see cref="Start"/> and after it), or null.</summary>
        public string? End { get; set; }

        public string Title { get; set; } = string.Empty;
        public string? Note { get; set; }

        /// <summary><see cref="AiPlanPlace.Key"/> of the place this happens at, or null.</summary>
        public string? PlaceKey { get; set; }
    }

    public sealed class AiPlanPlace
    {
        /// <summary>"p1", "p2"… - what the plan's activities point at.</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>The name in the user's language ("Kolezyum").</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The name as written on maps in the local language ("Colosseo"), or null when it's the same.</summary>
        public string? LocalName { get; set; }

        /// <summary>District / neighbourhood ("Monti"), or null.</summary>
        public string? Area { get; set; }

        /// <summary>One of <see cref="AiPlaceCategories.All"/>.</summary>
        public string Category { get; set; } = AiPlaceCategories.Other;

        public string Description { get; set; } = string.Empty;

        /// <summary>The map's position when <see cref="Verified"/>; otherwise the AI's guess, only a hint for the app's geocoder.</summary>
        public double? Lat { get; set; }
        public double? Lon { get; set; }

        /// <summary>Typical visit length in minutes, or null.</summary>
        public int? VisitMinutes { get; set; }

        /// <summary>
        /// The server found the place on the map (OpenStreetMap): <see cref="Lat"/>/<see cref="Lon"/> are the map's
        /// coordinates, not a guess, and are used as they are.
        /// </summary>
        public bool Verified { get; set; }
    }

    /// <summary>The fixed place categories of the Edge Function (PLACE_CATEGORIES in planner.ts).</summary>
    public static class AiPlaceCategories
    {
        public const string Other = "other";

        public static readonly IReadOnlyList<string> All = new[]
        {
            "landmark", "museum", "historic", "religious", "park", "nature", "beach", "viewpoint",
            "restaurant", "cafe", "market", "shopping", "nightlife", "neighborhood", "experience", Other,
        };

        /// <summary>"museum" → "Müze" / "Museum"; empty for "other" and unknown keys.</summary>
        public static string Label(string? category) => category switch
        {
            "landmark" => Resources.Strings.AppResources.AiPlan_Cat_Landmark,
            "museum" => Resources.Strings.AppResources.AiPlan_Cat_Museum,
            "historic" => Resources.Strings.AppResources.AiPlan_Cat_Historic,
            "religious" => Resources.Strings.AppResources.AiPlan_Cat_Religious,
            "park" => Resources.Strings.AppResources.AiPlan_Cat_Park,
            "nature" => Resources.Strings.AppResources.AiPlan_Cat_Nature,
            "beach" => Resources.Strings.AppResources.AiPlan_Cat_Beach,
            "viewpoint" => Resources.Strings.AppResources.AiPlan_Cat_Viewpoint,
            "restaurant" => Resources.Strings.AppResources.AiPlan_Cat_Restaurant,
            "cafe" => Resources.Strings.AppResources.AiPlan_Cat_Cafe,
            "market" => Resources.Strings.AppResources.AiPlan_Cat_Market,
            "shopping" => Resources.Strings.AppResources.AiPlan_Cat_Shopping,
            "nightlife" => Resources.Strings.AppResources.AiPlan_Cat_Nightlife,
            "neighborhood" => Resources.Strings.AppResources.AiPlan_Cat_Neighborhood,
            "experience" => Resources.Strings.AppResources.AiPlan_Cat_Experience,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// What the user kept ticked in the preview. <paramref name="PlanPlaces"/> is every place of the plan, so an
    /// activity can still be linked to a place the trip already has even when that place wasn't ticked.
    /// </summary>
    /// <param name="PlaceKeys">Keys of the places to add to the trip.</param>
    public sealed record AiPlanSelection(
        IReadOnlyList<AiPlanPlace> PlanPlaces,
        IReadOnlyList<(int Day, AiPlanActivity Activity)> Activities,
        IReadOnlyCollection<string> PlaceKeys,
        IReadOnlyList<string> Todos);

    /// <param name="Remaining">Plans the account may still ask for today.</param>
    /// <param name="TruncatedDays">The trip is longer than the planner's limit: only its first days were planned.</param>
    /// <param name="RequestId">The server's log id of the request (TP-54); null from an older function.</param>
    public sealed record AiPlanResult(AiTripPlan Plan, int Remaining, bool TruncatedDays, string? RequestId = null);

    /// <param name="Paused">AI planning is in maintenance (admin panel); <paramref name="Message"/> is the admin's note, if any.</param>
    public sealed record AiQuota(int Remaining, int Limit, bool Paused = false, string? Message = null);

    /// <summary>What went into the trip after "Plana ekle".</summary>
    /// <param name="UnlocatedPlaces">Ticked places that couldn't be found on the map and were left out (no wrong pins).</param>
    public sealed record AiApplyResult(int Activities, int Places, int Todos, int UnlocatedPlaces);

    public enum AiPlanErrorKind
    {
        Offline,
        NotSignedIn,
        AccountDisabled,
        QuotaExceeded,
        InvalidRequest,
        NotConfigured,
        Paused,
        Failed
    }

    public sealed class AiPlanException : Exception
    {
        public AiPlanException(AiPlanErrorKind kind, string? message = null, Exception? inner = null)
            : base(message ?? kind.ToString(), inner)
        {
            Kind = kind;
        }

        public AiPlanErrorKind Kind { get; }
    }
}
