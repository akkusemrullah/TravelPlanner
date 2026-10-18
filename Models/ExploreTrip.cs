using System.Globalization;
using System.Text.Json.Serialization;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Models
{
    /// <summary>
    /// TP-33 "Keşfet": one card of the sample trip catalog, as public.explore_list returns it (supabase migration 23).
    /// Only the card fields travel in the list; the day plan and places come with <see cref="ExploreTripDetail"/>.
    /// </summary>
    public class ExploreTripCard
    {
        /// <summary>A card stays "Yeni" for this long after it was published.</summary>
        public const int NewForDays = 14;

        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public int DurationDays { get; set; }

        /// <summary>1 = budget-friendly, 2 = mid, 3 = premium.</summary>
        public int BudgetLevel { get; set; } = 2;

        /// <summary>Travel styles, see <see cref="ExploreTags"/>.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>"thumbs/&lt;slug&gt;.webp" in the explore-images bucket, or null.</summary>
        public string? ThumbPath { get; set; }

        public bool IsFeatured { get; set; }
        public DateTimeOffset? PublishedAt { get; set; }

        // ---- UI (not from the server) ----

        [JsonIgnore]
        public string? ThumbUrl => ExploreImages.Url(ThumbPath);

        [JsonIgnore]
        public bool HasThumb => ThumbUrl is not null;

        /// <summary>"İstanbul, Türkiye" (the city alone when both are the same).</summary>
        [JsonIgnore]
        public string Location =>
            string.IsNullOrWhiteSpace(Country) || string.Equals(City, Country, StringComparison.CurrentCultureIgnoreCase)
                ? City
                : $"{City}, {Country}";

        /// <summary>"3 gün".</summary>
        [JsonIgnore]
        public string DurationText => string.Format(CultureInfo.CurrentCulture, AppResources.Explore_DaysFormat, DurationDays);

        /// <summary>₺ / ₺₺ / ₺₺₺.</summary>
        [JsonIgnore]
        public string BudgetText => new('₺', Math.Clamp(BudgetLevel, 1, 3));

        /// <summary>The first two travel styles, as labels.</summary>
        [JsonIgnore]
        public IReadOnlyList<string> TagLabels => Tags.Select(ExploreTags.Label).Where(l => l.Length > 0).Take(2).ToList();

        [JsonIgnore]
        public string TagText => string.Join(" · ", TagLabels);

        [JsonIgnore]
        public bool HasTags => TagLabels.Count > 0;

        [JsonIgnore]
        public bool IsNew => PublishedAt is { } published && DateTimeOffset.UtcNow - published < TimeSpan.FromDays(NewForDays);
    }

    /// <summary>The whole sample trip, as public.explore_detail returns it.</summary>
    public sealed class ExploreTripDetail : ExploreTripCard
    {
        public string? Summary { get; set; }
        public string? Description { get; set; }

        /// <summary>Estimated budget in minor units of <see cref="CurrencyCode"/>, or null.</summary>
        public long? EstBudgetMinor { get; set; }

        public string CurrencyCode { get; set; } = "TRY";

        /// <summary>Good months (1-12); empty = all year.</summary>
        public List<int> Months { get; set; } = new();

        public List<string> Tips { get; set; } = new();
        public string? CoverPath { get; set; }
        public string? ImageCredit { get; set; }
        public int ContentVersion { get; set; }
        public List<ExplorePlace> Places { get; set; } = new();
        public List<ExploreActivity> Activities { get; set; } = new();
        public List<ExploreTodo> Todos { get; set; } = new();

        [JsonIgnore]
        public string? CoverUrl => ExploreImages.Url(CoverPath) ?? ThumbUrl;
    }

    public sealed class ExplorePlace
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    public sealed class ExploreActivity
    {
        public Guid Id { get; set; }

        /// <summary>1 = the trip's first day: the copy's date is start date + (DayIndex - 1).</summary>
        public int DayIndex { get; set; }

        /// <summary>"HH:mm" or null.</summary>
        public string? StartTime { get; set; }

        /// <summary>"HH:mm" (only with a start, after it) or null.</summary>
        public string? EndTime { get; set; }

        public string Title { get; set; } = string.Empty;
        public string? Note { get; set; }
        public Guid? PlaceId { get; set; }
    }

    public sealed class ExploreTodo
    {
        public const string KindTodo = "todo";
        public const string KindPacking = "packing";

        public Guid Id { get; set; }
        public string Title { get; set; } = string.Empty;

        /// <summary><see cref="KindTodo"/> or <see cref="KindPacking"/> (both become to-dos of the copy).</summary>
        public string Kind { get; set; } = KindTodo;
    }

    /// <summary>One page of cards; <see cref="NextCursor"/> is null on the last page.</summary>
    public sealed record ExplorePage(IReadOnlyList<ExploreTripCard> Items, string? NextCursor);

    /// <summary>The travel styles the catalog knows (same list as the CHECK in migration 23).</summary>
    public static class ExploreTags
    {
        public static readonly IReadOnlyList<string> All = new[] { "culture", "nature", "sea", "food", "family", "romantic", "adventure" };

        public static string Label(string? tag) => tag switch
        {
            "culture" => AppResources.Explore_Tag_Culture,
            "nature" => AppResources.Explore_Tag_Nature,
            "sea" => AppResources.Explore_Tag_Sea,
            "food" => AppResources.Explore_Tag_Food,
            "family" => AppResources.Explore_Tag_Family,
            "romantic" => AppResources.Explore_Tag_Romantic,
            "adventure" => AppResources.Explore_Tag_Adventure,
            _ => string.Empty,
        };
    }

    /// <summary>Sort orders of explore_list.</summary>
    public static class ExploreSort
    {
        public const string Recommended = "recommended";
        public const string Newest = "newest";
        public const string Shortest = "shortest";
    }

    /// <summary>Public URLs of the explore-images bucket (covers/…, thumbs/…).</summary>
    public static class ExploreImages
    {
        public const string Bucket = "explore-images";

        public static string? Url(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            var segments = path.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
                return null;
            return $"{SupabaseConfig.Url.TrimEnd('/')}/storage/v1/object/public/{Bucket}/{string.Join('/', segments.Select(Uri.EscapeDataString))}";
        }
    }

    /// <summary>
    /// What the list asks for: search text, filters and sort. A value object - two filters with the same choices are
    /// equal, and <see cref="Key"/> names them in the response cache.
    /// </summary>
    public sealed record ExploreFilter
    {
        public const int MaxQueryLength = 100;
        public const int MinQueryLength = 2;

        public string? Query { get; init; }

        /// <summary>Country or city ("Türkiye", "Roma").</summary>
        public string? Place { get; init; }

        public int? MinDays { get; init; }
        public int? MaxDays { get; init; }
        public IReadOnlyList<int> BudgetLevels { get; init; } = Array.Empty<int>();
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public int? Month { get; init; }
        public string Sort { get; init; } = ExploreSort.Recommended;

        /// <summary>How many filters are on (search and sort not counted) - the badge on the filter button.</summary>
        public int ActiveCount =>
            (string.IsNullOrWhiteSpace(Place) ? 0 : 1) + (MinDays is null && MaxDays is null ? 0 : 1) +
            (BudgetLevels.Count > 0 ? 1 : 0) + (Tags.Count > 0 ? 1 : 0) + (Month is null ? 0 : 1);

        /// <summary>No search, no filter, default sort: the list that is kept for offline use.</summary>
        public bool IsDefault => string.IsNullOrWhiteSpace(Query) && ActiveCount == 0 && Sort == ExploreSort.Recommended;

        /// <summary>The same choices without the search text and filters (sort kept).</summary>
        public ExploreFilter Cleared() => new() { Query = Query, Sort = Sort };

        public string Key => string.Join("|",
            (Query ?? string.Empty).Trim().ToLowerInvariant(), (Place ?? string.Empty).Trim().ToLowerInvariant(),
            MinDays, MaxDays, string.Join(',', BudgetLevels.OrderBy(b => b)), string.Join(',', Tags.OrderBy(t => t, StringComparer.Ordinal)),
            Month, Sort);

        public bool Equals(ExploreFilter? other) => other is not null && Key == other.Key;

        public override int GetHashCode() => Key.GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>What the user picked on the "Ekle" sheet (or in "Düzenle ve ekle").</summary>
    public sealed record ExploreAddOptions(
        string Name,
        DateTime StartDate,
        string CurrencyCode,
        long PlannedBudgetMinor = 0,
        IReadOnlyCollection<Guid>? ActivityIds = null,
        IReadOnlyCollection<Guid>? PlaceIds = null,
        IReadOnlyCollection<Guid>? TodoIds = null);
}
