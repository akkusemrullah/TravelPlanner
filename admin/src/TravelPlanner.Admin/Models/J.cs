using System.Globalization;
using System.Text.Json.Nodes;

namespace TravelPlanner.Admin.Models;

/// <summary>Short readers for the RPC results the pages show.</summary>
public static class J
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly TimeZoneInfo Istanbul = FindZone();

    public static string S(JsonNode? node, string key) =>
        node?[key] is JsonValue v ? v.ToString() : "";

    public static long? N(JsonNode? node, string key) =>
        node?[key] is JsonValue v && long.TryParse(v.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public static bool B(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static IEnumerable<JsonNode> Items(JsonNode? node) =>
        node is JsonArray a ? a.OfType<JsonNode>() : [];

    /// <summary>A timestamp in Istanbul time ("18.10.2026 15:40"), or "—".</summary>
    public static string When(JsonNode? node, string key) =>
        DateTimeOffset.TryParse(S(node, key), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? TimeZoneInfo.ConvertTime(d, Istanbul).ToString("dd.MM.yyyy HH:mm", Tr)
            : "—";

    public static string Day(JsonNode? node, string key) =>
        DateTime.TryParse(S(node, key), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("dd.MM.yyyy ddd", Tr)
            : "—";

    public static string StatusText(string status) => status switch
    {
        "published" => "Yayında",
        "draft" => "Taslak",
        "archived" => "Arşiv",
        _ => status,
    };

    public static string StateText(string state) => state switch
    {
        "active" => "Aktif",
        "banned" => "Engelli",
        "deleted" => "Silinmiş",
        _ => state,
    };

    private static TimeZoneInfo FindZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
    }
}
