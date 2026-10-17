using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    /// <summary>The CAPTCHA page (Cloudflare Turnstile) and how its result is read back.</summary>
    public class TurnstilePageTests
    {
        [Fact]
        public void Html_RendersTheWidgetWithTheSiteKeyLanguageAndTheme()
        {
            var html = TurnstilePage.BuildHtml("0x4AAAAAAAtestkey", "en", dark: true);

            Assert.Contains("challenges.cloudflare.com/turnstile/v0/api.js?onload=tpLoad&render=explicit", html);
            Assert.Contains("sitekey: \"0x4AAAAAAAtestkey\"", html);
            Assert.Contains("language: \"en\"", html);
            Assert.Contains("theme: \"dark\"", html);
            Assert.Contains(TurnstilePage.ResultUrl, html);
            Assert.Contains("language: \"tr\"", TurnstilePage.BuildHtml("k", "de", dark: false)); // anything else falls back to Turkish
            Assert.Contains("theme: \"light\"", TurnstilePage.BuildHtml("k", "tr", dark: false));
        }

        [Fact]
        public void Html_CantBeBrokenByTheSiteKey()
        {
            var html = TurnstilePage.BuildHtml("\"</script><script>alert(1)</script>", "tr", dark: false);

            Assert.DoesNotContain("<script>alert(1)", html);
        }

        [Fact]
        public void ResultUrl_IsUnderThePageUrl()
        {
            Assert.StartsWith(SupabaseConfig.CaptchaPageUrl.TrimEnd('/') + "/", TurnstilePage.ResultUrl);
            Assert.EndsWith("/", TurnstilePage.BaseUrl);
        }

        [Fact]
        public void Navigation_ToTheResultUrl_CarriesTheTokenOrTheError()
        {
            var token = TurnstilePage.FromNavigation(TurnstilePage.ResultUrl + "?token=0.abc_DEF-1%2B2");
            Assert.Equal(new CaptchaResult("0.abc_DEF-1+2", null), token);
            Assert.True(token!.Value.IsToken);

            var error = TurnstilePage.FromNavigation(TurnstilePage.ResultUrl + "?error=110200");
            Assert.Equal("110200", error!.Value.Error);
            Assert.False(error.Value.IsToken);
        }

        [Fact]
        public void OtherNavigations_AreNotResults()
        {
            Assert.Null(TurnstilePage.FromNavigation(TurnstilePage.BaseUrl));
            Assert.Null(TurnstilePage.FromNavigation("https://challenges.cloudflare.com/cdn-cgi/challenge-platform/x"));
            Assert.Null(TurnstilePage.FromNavigation(null));
        }

        [Theory]
        [InlineData("\"token=abc\"", "abc", null)]   // Android wraps the value in JSON quotes
        [InlineData("token=abc", "abc", null)]
        [InlineData("\"error=script_not_loaded\"", null, "script_not_loaded")]
        public void Script_ResultIsRead(string value, string? token, string? error)
        {
            Assert.Equal(new CaptchaResult(token, error), TurnstilePage.FromScript(value));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("\"\"")]
        [InlineData("null")]
        [InlineData("undefined")]
        public void Script_NoResultYet(string? value)
        {
            Assert.Null(TurnstilePage.FromScript(value));
        }

        [Fact]
        public void EmptyToken_IsAnError()
        {
            Assert.False(TurnstilePage.Parse("token=").IsToken);
            Assert.Equal("empty", TurnstilePage.Parse("").Error);
        }
    }
}
