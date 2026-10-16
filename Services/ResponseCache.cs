using System.Collections.Concurrent;

namespace TravelPlanner.Services
{
    public class ResponseCache : IResponseCache
    {
        private const int MaxEntries = 200;

        private readonly ConcurrentDictionary<string, (object? Value, DateTimeOffset ExpiresAt)> _entries = new();
        private readonly Func<DateTimeOffset> _clock;

        public ResponseCache() : this(() => DateTimeOffset.UtcNow)
        {
        }

        /// <summary>Test seam: lets unit tests control "now" to check expiry.</summary>
        public ResponseCache(Func<DateTimeOffset> clock)
        {
            _clock = clock;
        }

        public bool TryGet<T>(string key, out T? value)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > _clock() && entry.Value is T typed)
                {
                    value = typed;
                    return true;
                }

                _entries.TryRemove(key, out _);
            }

            value = default;
            return false;
        }

        public void Set<T>(string key, T value, TimeSpan timeToLive)
        {
            // Keep memory bounded: when full, drop expired entries first, then the soonest-to-expire ones.
            if (_entries.Count >= MaxEntries)
            {
                var now = _clock();
                foreach (var stale in _entries.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
                    _entries.TryRemove(stale, out _);

                foreach (var oldest in _entries.OrderBy(e => e.Value.ExpiresAt).Take(_entries.Count - MaxEntries + 1).Select(e => e.Key).ToList())
                    _entries.TryRemove(oldest, out _);
            }

            _entries[key] = (value, _clock() + timeToLive);
        }
    }
}
