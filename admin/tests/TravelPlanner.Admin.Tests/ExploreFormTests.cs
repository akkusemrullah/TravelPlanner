using System.Text.Json.Nodes;
using TravelPlanner.Admin.Models;
using Xunit;

namespace TravelPlanner.Admin.Tests;

public class ExploreFormTests
{
    // what admin_explore_get returns (migration 25)
    private const string Sample = """
        {
          "slug": "istanbul-3-gun", "title": "İstanbul 3 gün", "summary": "Tarihi yarımada.",
          "country": "Türkiye", "city": "İstanbul", "duration_days": 3, "budget_level": 2,
          "est_budget_minor": 1500050, "currency_code": "TRY", "tags": ["culture", "food"], "months": [4, 5, 10],
          "tips": ["Müze Kart al.", "Erken git."], "cover_path": "covers/istanbul-3-gun.webp",
          "thumb_path": "https://upload.wikimedia.org/x.jpg", "lang": "tr", "sort_rank": 10, "is_featured": true,
          "status": "published", "publish_from": "2026-11-01T00:00:00+00:00",
          "places": [{"key": "p1", "name": "Ayasofya", "lat": 41.0086, "lon": 28.9802}],
          "days": [{"day": 1, "activities": [{"start": "09:00", "title": "Ayasofya", "place": "p1"}]}],
          "todos": [{"title": "Rahat ayakkabı", "kind": "packing"}]
        }
        """;

    private static ExploreForm Load() => ExploreForm.FromJson(JsonNode.Parse(Sample)!.AsObject());

    [Fact]
    public void Round_trip_keeps_every_field()
    {
        var json = Load().ToJson();

        Assert.Equal("istanbul-3-gun", json["slug"]!.GetValue<string>());
        Assert.Equal(1500050, json["est_budget_minor"]!.GetValue<long>());
        Assert.Equal(["culture", "food"], json["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal([4, 5, 10], json["months"]!.AsArray().Select(m => m!.GetValue<int>()));
        Assert.Equal(2, json["tips"]!.AsArray().Count);
        Assert.True(json["is_featured"]!.GetValue<bool>());
        Assert.Equal("2026-11-01T00:00:00Z", json["publish_from"]!.GetValue<string>());
        Assert.Null(json["publish_until"]);
        Assert.Null(json["description"]);
        Assert.Equal("p1", json["days"]![0]!["activities"]![0]!["place"]!.GetValue<string>());
        Assert.Equal("packing", json["todos"]![0]!["kind"]!.GetValue<string>());
        Assert.Empty(Load().Validate());
    }

    [Fact]
    public void Tags_keep_the_catalog_order_and_unknown_ones_are_refused()
    {
        var form = new ExploreForm { Slug = "x", Title = "x", Country = "x", City = "x", Tags = ["sea", "culture"] };
        Assert.Equal(["culture", "sea"], form.ToJson()["tags"]!.AsArray().Select(t => t!.GetValue<string>()));

        form.Tags.Add("casino");
        Assert.Contains(form.Validate(), e => e.Field == nameof(ExploreForm.Tags));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"places\": {}}")]
    [InlineData("{\"place\": []}")]
    public void A_broken_plan_is_refused(string plan)
    {
        var form = new ExploreForm { PlanJson = plan };
        Assert.Contains(form.Validate(), e => e.Field == nameof(ExploreForm.PlanJson));
    }

    [Theory]
    [InlineData("covers/roma.webp", true)]
    [InlineData("thumbs/roma-4.jpg", true)]
    [InlineData("https://example.com/a.webp", true)]
    [InlineData("http://example.com/a.webp", false)]
    [InlineData("covers/../x.webp", false)]
    [InlineData("covers/Roma.png", false)]
    public void Image_paths(string path, bool ok)
    {
        var form = new ExploreForm { CoverPath = path };
        Assert.Equal(ok, !form.Validate().Any(e => e.Field == nameof(ExploreForm.CoverPath)));
    }

    [Fact]
    public void Tips_are_one_per_line_and_at_most_ten()
    {
        var form = new ExploreForm { Tips = "a\r\n\r\n b \n" };
        Assert.Equal(["a", "b"], form.ToJson()["tips"]!.AsArray().Select(t => t!.GetValue<string>()));

        form.Tips = string.Join("\n", Enumerable.Range(1, 11));
        Assert.Contains(form.Validate(), e => e.Field == nameof(ExploreForm.Tips));
    }

    [Fact]
    public void Publish_window_must_end_after_it_starts()
    {
        var form = new ExploreForm { PublishFrom = new DateTime(2026, 11, 1), PublishUntil = new DateTime(2026, 11, 1) };
        Assert.Contains(form.Validate(), e => e.Field == nameof(ExploreForm.PublishUntil));
    }
}
