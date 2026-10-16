namespace TravelPlanner.Services
{
    /// <summary>
    /// Tiny in-memory, time-limited cache for web API responses (weather, geocoding, autocomplete) so
    /// re-opening a page or re-typing the same text doesn't hit the network again (TP-208).
    /// </summary>
    public interface IResponseCache
    {
        bool TryGet<T>(string key, out T? value);
        void Set<T>(string key, T value, TimeSpan timeToLive);
    }
}
