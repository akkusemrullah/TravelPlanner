namespace TravelPlanner.Tests
{
    /// <summary>IPreferences backed by a dictionary - MAUI's real Preferences needs a device.</summary>
    public sealed class InMemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object?> _values = new();

        private static string Key(string key, string? sharedName) => $"{sharedName}|{key}";

        public bool ContainsKey(string key, string? sharedName = null) => _values.ContainsKey(Key(key, sharedName));

        public void Remove(string key, string? sharedName = null) => _values.Remove(Key(key, sharedName));

        public void Clear(string? sharedName = null) => _values.Clear();

        public void Set<T>(string key, T value, string? sharedName = null) => _values[Key(key, sharedName)] = value;

        public T Get<T>(string key, T defaultValue, string? sharedName = null) =>
            _values.TryGetValue(Key(key, sharedName), out var value) && value is T typed ? typed : defaultValue;
    }
}
