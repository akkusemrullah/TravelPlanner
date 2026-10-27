namespace TravelPlanner.Helpers
{
    /// <summary>
    /// TP-90: which tab the hardware/gesture back goes to once the open tab has no page left to pop. The tabs the
    /// user opened are kept in order (each at most once), so back walks them backwards and lands on Home last;
    /// only back on Home with nothing behind it leaves the app.
    /// </summary>
    public sealed class TabBackHistory
    {
        private readonly string _home;
        private readonly List<string> _visited = new();
        private string? _current;

        public TabBackHistory(string home) => _home = home;

        /// <summary>The tab now on screen (Shell.Navigated); a tab opened again moves to the end.</summary>
        public void Show(string tab)
        {
            if (tab == _current)
                return;
            if (_current is not null)
            {
                _visited.Remove(_current);
                _visited.Add(_current);
            }
            _visited.Remove(tab);
            _current = tab;
        }

        /// <summary>The tab to go back to, or null when back should leave the app (Home, nothing behind it).</summary>
        public string? Back()
        {
            if (_visited.Count > 0)
            {
                var previous = _visited[^1];
                _visited.RemoveAt(_visited.Count - 1);
                _current = previous;
                return previous;
            }

            if (_current is not null && _current != _home)
            {
                _current = _home;
                return _home;
            }

            return null;
        }
    }
}
