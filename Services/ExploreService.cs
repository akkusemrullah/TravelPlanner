using System.Text.Json;
using System.Text.Json.Nodes;
using TravelPlanner.Models;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Services
{
    /// <summary>
    /// TP-33 "Keşfet": reads the admin's sample trip catalog (public.explore_list / explore_detail, supabase migration
    /// 23) with the publishable key only, so it works for guests too. The catalog is not part of sync: nothing is
    /// stored in SQLite until the user adds a sample trip (TripService.CreateFromTemplateAsync).
    /// </summary>
    public interface IExploreService
    {
        /// <summary>One page of cards. <paramref name="cursor"/> = the previous page's NextCursor (null for the first page).</summary>
        Task<ExplorePage> ListAsync(ExploreFilter filter, string? cursor, CancellationToken cancellationToken = default);

        /// <summary>
        /// A first page shown before the network answers: from memory (6 hours), or - for the unfiltered list only -
        /// the copy kept on disk, however old (offline use). Null when there is none.
        /// </summary>
        ExplorePage? GetCachedFirstPage(ExploreFilter filter);

        /// <summary>The whole sample trip, or null when it is gone (archived, unpublished).</summary>
        Task<ExploreTripDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default);
    }

    public class ExploreService : IExploreService
    {
        public const int PageSize = 20;
        public static readonly TimeSpan CacheTime = TimeSpan.FromHours(6);
        private const string FirstPageFile = "explore_first_page.json";

        internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };

        private readonly ISupabaseClient _cloud;
        private readonly IResponseCache _cache;
        private readonly string? _cacheDirectory;

        public ExploreService(ISupabaseClient cloud, IResponseCache cache, string? cacheDirectory = null)
        {
            _cloud = cloud;
            _cache = cache;
            _cacheDirectory = cacheDirectory;
        }

        private static string ListKey(ExploreFilter filter) => "explore:list:" + filter.Key;

        public async Task<ExplorePage> ListAsync(ExploreFilter filter, string? cursor, CancellationToken cancellationToken = default)
        {
            var query = filter.Query?.Trim();
            if (query is { Length: > ExploreFilter.MaxQueryLength })
                query = query[..ExploreFilter.MaxQueryLength];

            var arguments = new JsonObject
            {
                ["p_query"] = string.IsNullOrEmpty(query) || query.Length < ExploreFilter.MinQueryLength ? null : query,
                ["p_country"] = string.IsNullOrWhiteSpace(filter.Place) ? null : Helpers.InputLimits.Truncate(filter.Place.Trim(), 100),
                ["p_min_days"] = filter.MinDays,
                ["p_max_days"] = filter.MaxDays,
                ["p_budget_levels"] = filter.BudgetLevels.Count == 0 ? null : new JsonArray(filter.BudgetLevels.Select(b => (JsonNode?)b).ToArray()),
                ["p_tags"] = filter.Tags.Count == 0 ? null : new JsonArray(filter.Tags.Select(t => (JsonNode?)t).ToArray()),
                ["p_month"] = filter.Month,
                ["p_sort"] = filter.Sort,
                ["p_cursor"] = cursor,
                ["p_limit"] = PageSize,
            };

            var result = await _cloud.PublicRpcAsync("explore_list", arguments, cancellationToken).ConfigureAwait(false);
            var page = ParsePage(result);

            if (cursor is null)
            {
                _cache.Set(ListKey(filter), page, CacheTime);
                if (filter.IsDefault)
                    SaveFirstPage(result);
            }
            return page;
        }

        internal static ExplorePage ParsePage(JsonElement result)
        {
            if (result.ValueKind != JsonValueKind.Object)
                return new ExplorePage(Array.Empty<ExploreTripCard>(), null);

            var items = result.TryGetProperty("items", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.Deserialize<List<ExploreTripCard>>(Json) ?? new List<ExploreTripCard>()
                : new List<ExploreTripCard>();
            var next = result.TryGetProperty("next_cursor", out var cursor) && cursor.ValueKind == JsonValueKind.String
                ? cursor.GetString()
                : null;
            return new ExplorePage(items, string.IsNullOrEmpty(next) ? null : next);
        }

        public ExplorePage? GetCachedFirstPage(ExploreFilter filter)
        {
            if (_cache.TryGet<ExplorePage>(ListKey(filter), out var page) && page is not null)
                return page;
            return filter.IsDefault ? LoadFirstPage() : null;
        }

        public async Task<ExploreTripDetail?> GetDetailAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var key = $"explore:detail:{id}";
            if (_cache.TryGet<ExploreTripDetail>(key, out var cached) && cached is not null)
                return cached;

            var result = await _cloud.PublicRpcAsync("explore_detail", new JsonObject { ["p_id"] = id.ToString() }, cancellationToken)
                .ConfigureAwait(false);
            if (result.ValueKind != JsonValueKind.Object)
                return null;

            var detail = result.Deserialize<ExploreTripDetail>(Json);
            if (detail is not null)
                _cache.Set(key, detail, CacheTime);
            return detail;
        }

        // ---- the unfiltered first page on disk: the tab opens at once, and offline still shows cards ----

        private string? FirstPagePath => _cacheDirectory is null ? null : Path.Combine(_cacheDirectory, FirstPageFile);

        private void SaveFirstPage(JsonElement result)
        {
            if (FirstPagePath is not { } path)
                return;
            try
            {
                File.WriteAllText(path, result.GetRawText());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"[Explore] Saving the first page failed: {ex.Message}");
            }
        }

        private ExplorePage? LoadFirstPage()
        {
            if (FirstPagePath is not { } path || !File.Exists(path))
                return null;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var page = ParsePage(document.RootElement);
                return page.Items.Count == 0 ? null : page;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                System.Diagnostics.Debug.WriteLine($"[Explore] Reading the first page failed: {ex.Message}");
                return null;
            }
        }
    }
}
