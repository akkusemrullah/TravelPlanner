namespace TravelPlanner.Services
{
    /// <summary>Applies the Light/Dark/System theme choice and keeps the colour tokens in sync with it (TP-303).</summary>
    public interface IThemeService
    {
        /// <summary>Applies the saved theme and starts following OS theme changes. Call once at start-up.</summary>
        void Initialize();

        /// <summary>Switches to <paramref name="option"/> immediately.</summary>
        void Apply(AppThemeOption option);
    }
}
