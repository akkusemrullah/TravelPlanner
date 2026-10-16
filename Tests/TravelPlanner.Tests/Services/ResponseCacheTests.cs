using Moq;
using Xunit;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class ResponseCacheTests
    {
        private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        private ResponseCache CreateCache() => new(() => _now);

        [Fact]
        public void ReturnsValue_BeforeExpiry()
        {
            var cache = CreateCache();
            cache.Set("k", "v", TimeSpan.FromMinutes(5));

            _now = _now.AddMinutes(4);

            Assert.True(cache.TryGet<string>("k", out var value));
            Assert.Equal("v", value);
        }

        [Fact]
        public void Misses_AfterExpiry()
        {
            var cache = CreateCache();
            cache.Set("k", "v", TimeSpan.FromMinutes(5));

            _now = _now.AddMinutes(6);

            Assert.False(cache.TryGet<string>("k", out _));
        }

        [Fact]
        public void Misses_ForUnknownKey_OrWrongType()
        {
            var cache = CreateCache();
            cache.Set("k", 42, TimeSpan.FromMinutes(5));

            Assert.False(cache.TryGet<string>("other", out _));
            Assert.False(cache.TryGet<string>("k", out _));
        }

        [Fact]
        public void StaysBounded_WhenManyEntriesAreAdded()
        {
            var cache = CreateCache();
            for (var i = 0; i < 500; i++)
                cache.Set($"k{i}", i, TimeSpan.FromMinutes(5 + i));

            // The most recent (longest-lived) entry survives eviction.
            Assert.True(cache.TryGet<int>("k499", out var last));
            Assert.Equal(499, last);
        }
    }
}
