namespace TravelPlanner.Services
{
    /// <summary>The app going to the background and coming back (raised from App.OnSleep / App.OnResume).</summary>
    public interface IAppLifecycle
    {
        event EventHandler? Sleeping;
        event EventHandler? Resumed;
    }

    public sealed class AppLifecycle : IAppLifecycle
    {
        public event EventHandler? Sleeping;
        public event EventHandler? Resumed;

        public void RaiseSleeping() => Sleeping?.Invoke(this, EventArgs.Empty);
        public void RaiseResumed() => Resumed?.Invoke(this, EventArgs.Empty);
    }
}
