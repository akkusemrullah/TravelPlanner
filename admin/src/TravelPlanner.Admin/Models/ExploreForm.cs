using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TravelPlanner.Admin.Models;

/// <summary>
/// The edit form of a Keşfet sample trip. Converts to and from the JSON private.explore_upsert takes
/// (docs/EXPLORE_CONTENT.md); places, day plan and to-dos stay JSON in <see cref="PlanJson"/>.
/// </summary>
public sealed partial class ExploreForm
{
    public static readonly string[] AllTags = ["culture", "nature", "sea", "food", "family", "romantic", "adventure"];
    public static readonly string[] AllStatuses = ["draft", "published", "archived"];

    public const string EmptyPlan = "{\n  \"places\": [],\n  \"days\": [],\n  \"todos\": []\n}";

    [Required, StringLength(80), RegularExpression("^[a-z0-9]+(-[a-z0-9]+)*$", ErrorMessage = "Küçük harf, rakam ve tire.")]
    public string Slug { get; set; } = "";

    [Required, StringLength(100)] public string Title { get; set; } = "";
    [StringLength(500)] public string? Summary { get; set; }
    [StringLength(2000)] public string? Description { get; set; }
    [Required, StringLength(100)] public string Country { get; set; } = "";
    [Required, StringLength(100)] public string City { get; set; } = "";
    [Range(1, 90)] public int DurationDays { get; set; } = 3;
    [Range(1, 3)] public int BudgetLevel { get; set; } = 2;
    [Range(0, 9_999_999_999)] public decimal? EstBudget { get; set; }

    [Required, RegularExpression("^[A-Z]{3}$", ErrorMessage = "3 büyük harf (TRY, EUR...).")]
    public string CurrencyCode { get; set; } = "TRY";

    public List<string> Tags { get; set; } = [];
    public List<int> Months { get; set; } = [];

    /// <summary>One tip per line.</summary>
    public string? Tips { get; set; }

    [StringLength(1000)] public string? CoverPath { get; set; }
    [StringLength(1000)] public string? ThumbPath { get; set; }
    [StringLength(200)] public string? ImageCredit { get; set; }
    [RegularExpression("^(tr|en)$")] public string Lang { get; set; } = "tr";
    [Range(0, 1_000_000)] public int SortRank { get; set; } = 1000;
    public bool IsFeatured { get; set; }
    [RegularExpression("^(draft|published|archived)$")] public string Status { get; set; } = "draft";

    [DataType(DataType.Date)] public DateTime? PublishFrom { get; set; }
    [DataType(DataType.Date)] public DateTime? PublishUntil { get; set; }

    /// <summary>{"places": [...], "days": [...], "todos": [...]} in the explore_upsert format.</summary>
    public string PlanJson { get; set; } = EmptyPlan;

    public static ExploreForm FromJson(JsonObject json)
    {
        var plan = new JsonObject
        {
            ["places"] = json["places"]?.DeepClone() ?? new JsonArray(),
            ["days"] = json["days"]?.DeepClone() ?? new JsonArray(),
            ["todos"] = json["todos"]?.DeepClone() ?? new JsonArray(),
        };
        var estMinor = Long(json["est_budget_minor"]);
        return new ExploreForm
        {
            Slug = Str(json["slug"]) ?? "",
            Title = Str(json["title"]) ?? "",
            Summary = Str(json["summary"]),
            Description = Str(json["description"]),
            Country = Str(json["country"]) ?? "",
            City = Str(json["city"]) ?? "",
            DurationDays = (int)(Long(json["duration_days"]) ?? 1),
            BudgetLevel = (int)(Long(json["budget_level"]) ?? 2),
            EstBudget = estMinor is null ? null : estMinor.Value / 100m,
            CurrencyCode = Str(json["currency_code"]) ?? "TRY",
            Tags = Strings(json["tags"]),
            Months = Strings(json["months"]).Select(m => int.Parse(m, CultureInfo.InvariantCulture)).ToList(),
            Tips = string.Join("\n", Strings(json["tips"])),
            CoverPath = Str(json["cover_path"]),
            ThumbPath = Str(json["thumb_path"]),
            ImageCredit = Str(json["image_credit"]),
            Lang = Str(json["lang"]) ?? "tr",
            SortRank = (int)(Long(json["sort_rank"]) ?? 1000),
            IsFeatured = json["is_featured"] is JsonValue f && f.TryGetValue<bool>(out var b) && b,
            Status = Str(json["status"]) ?? "draft",
            PublishFrom = Date(json["publish_from"]),
            PublishUntil = Date(json["publish_until"]),
            PlanJson = plan.ToJsonString(Indented),
        };
    }

    /// <summary>Checks what data annotations can't. Returns (field, message) pairs; empty when fine.</summary>
    public IEnumerable<(string Field, string Message)> Validate()
    {
        if (Tags.Except(AllTags).Any())
            yield return (nameof(Tags), "Bilinmeyen stil.");
        if (Months.Any(m => m is < 1 or > 12))
            yield return (nameof(Months), "Ay 1-12 olmalı.");
        if (TipList().Count > 10)
            yield return (nameof(Tips), "En fazla 10 ipucu.");
        if (PublishFrom is not null && PublishUntil is not null && PublishUntil <= PublishFrom)
            yield return (nameof(PublishUntil), "Bitiş başlangıçtan sonra olmalı.");
        foreach (var (field, value) in new[] { (nameof(CoverPath), CoverPath), (nameof(ThumbPath), ThumbPath) })
            if (!string.IsNullOrWhiteSpace(value) && !ImagePathRegex().IsMatch(value.Trim()))
                yield return (field, "covers/<slug>.webp, thumbs/<slug>.webp veya https:// adresi.");
        if (ParsePlan(out var error) is null)
            yield return (nameof(PlanJson), error!);
    }

    /// <summary>The JSON for admin_explore_upsert.</summary>
    public JsonObject ToJson()
    {
        var plan = ParsePlan(out var error) ?? throw new InvalidOperationException(error);
        var json = new JsonObject
        {
            ["slug"] = Slug.Trim(),
            ["title"] = Title.Trim(),
            ["summary"] = Blank(Summary),
            ["description"] = Blank(Description),
            ["country"] = Country.Trim(),
            ["city"] = City.Trim(),
            ["duration_days"] = DurationDays,
            ["budget_level"] = BudgetLevel,
            ["est_budget_minor"] = EstBudget is null ? null : (long)Math.Round(EstBudget.Value * 100m),
            ["currency_code"] = CurrencyCode.Trim().ToUpperInvariant(),
            ["tags"] = new JsonArray(AllTags.Where(Tags.Contains).Select(t => (JsonNode)t).ToArray()),
            ["months"] = new JsonArray(Months.Distinct().Order().Select(m => (JsonNode)m).ToArray()),
            ["tips"] = new JsonArray(TipList().Select(t => (JsonNode)t).ToArray()),
            ["cover_path"] = Blank(CoverPath),
            ["thumb_path"] = Blank(ThumbPath),
            ["image_credit"] = Blank(ImageCredit),
            ["lang"] = Lang,
            ["sort_rank"] = SortRank,
            ["is_featured"] = IsFeatured,
            ["status"] = Status,
            ["publish_from"] = PublishFrom?.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture),
            ["publish_until"] = PublishUntil?.ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture),
        };
        foreach (var key in new[] { "places", "days", "todos" })
            json[key] = plan[key]?.DeepClone() ?? new JsonArray();
        return json;
    }

    private JsonObject? ParsePlan(out string? error)
    {
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(string.IsNullOrWhiteSpace(PlanJson) ? "{}" : PlanJson);
        }
        catch (JsonException e)
        {
            error = $"Plan JSON okunamadı: {e.Message}";
            return null;
        }
        if (node is not JsonObject plan)
        {
            error = "Plan bir JSON nesnesi olmalı: {\"places\": [], \"days\": [], \"todos\": []}.";
            return null;
        }
        foreach (var key in new[] { "places", "days", "todos" })
        {
            if (plan[key] is not null and not JsonArray)
            {
                error = $"\"{key}\" bir dizi olmalı.";
                return null;
            }
        }
        var unknown = plan.Select(p => p.Key).Except(["places", "days", "todos"]).FirstOrDefault();
        if (unknown is not null)
        {
            error = $"Planda bilinmeyen alan: \"{unknown}\" (yalnızca places, days, todos).";
            return null;
        }
        return plan;
    }

    private List<string> TipList() =>
        (Tips ?? "").Split('\n').Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Pretty(JsonNode node) => node.ToJsonString(Indented);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? Long(JsonNode? node) =>
        node is JsonValue v ? v.TryGetValue<long>(out var l) ? l : v.TryGetValue<double>(out var d) ? (long)d : null : null;

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x is JsonValue v ? v.ToString() : null).OfType<string>().ToList() : [];

    private static DateTime? Date(JsonNode? node) =>
        Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime.Date
            : null;

    [GeneratedRegex(@"^((covers|thumbs)/[a-z0-9-]+\.(webp|jpg)|https://\S+)$")]
    private static partial Regex ImagePathRegex();
}
