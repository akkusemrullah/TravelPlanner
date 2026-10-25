using Xunit;
using TravelPlanner.Helpers;

namespace TravelPlanner.Tests
{
    public class CrashReportingConfigTests
    {
        [Fact]
        public void IsOff_UntilADsnIsSet() => Assert.False(CrashReportingConfig.IsEnabled);

        [Theory]
        [InlineData("https://api.example.com/v1/forecast?latitude=39.9&longitude=32.8", "https://api.example.com/v1/forecast")]
        [InlineData("https://x.supabase.co/auth/v1/callback#access_token=abc", "https://x.supabase.co/auth/v1/callback")]
        [InlineData("https://example.com/path", "https://example.com/path")]
        [InlineData(null, null)]
        public void StripQuery_RemovesQueryAndFragment(string? url, string? expected) =>
            Assert.Equal(expected, CrashReportingConfig.StripQuery(url));
    }
}
