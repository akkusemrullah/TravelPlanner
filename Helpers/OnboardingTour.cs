using TravelPlanner.Resources.Strings;

namespace TravelPlanner.Helpers
{
    /// <summary>One card of the tour: its text and the icons (Icons.xaml keys) its picture is drawn with.</summary>
    public sealed record OnboardingStep(string Title, string Text, string Icon, string SideIcon, string CornerIcon);

    /// <summary>
    /// TP-93: the short "how it works" tour shown once at the first start (Views/OnboardingPage). Closing it with the
    /// X, "Başla" or the back button saves <see cref="SeenKey"/>; Profil → "Tanıtımı tekrar göster" opens it again.
    /// </summary>
    public static class OnboardingTour
    {
        internal const string SeenKey = "onboarding.seen";

        /// <summary>Opens the tour over the current page; set by the app (the view models can't see the pages).</summary>
        public static Func<Task>? Presenter { get; set; }

        public static IReadOnlyList<OnboardingStep> Steps =>
        [
            new(AppResources.Onboarding_TripTitle, AppResources.Onboarding_TripText, "Ic.CalendarPlus", "Ic.Pin", "Ic.Wallet"),
            new(AppResources.Onboarding_AiTitle, AppResources.Onboarding_AiText, "Ic.Route", "Ic.Clock", "Ic.Star"),
            new(AppResources.Onboarding_ExploreTitle, AppResources.Onboarding_ExploreText, "Ic.Compass", "Ic.Plus", "Ic.Globe"),
            new(AppResources.Onboarding_TogetherTitle, AppResources.Onboarding_TogetherText, "Ic.Users", "Ic.Share", "Ic.Shield"),
        ];

        public static bool ShouldShow(IPreferences preferences) => !preferences.Get(SeenKey, false);

        public static void MarkSeen(IPreferences preferences) => preferences.Set(SeenKey, true);

        /// <summary>Profil → "Tanıtımı tekrar göster".</summary>
        public static Task ShowAgainAsync() => Presenter?.Invoke() ?? Task.CompletedTask;
    }

    /// <summary>Which card of the tour is showing, and what the main button says.</summary>
    public sealed class OnboardingProgress(int count)
    {
        public int Count { get; } = Math.Max(1, count);

        public int Index { get; private set; }

        public bool IsLast => Index == Count - 1;

        /// <summary>"İleri", or "Başla" on the last card.</summary>
        public string ActionText => IsLast ? AppResources.Onboarding_Start : AppResources.Onboarding_Next;

        /// <summary>"2 / 4" for screen readers.</summary>
        public string PositionText => string.Format(AppResources.Onboarding_PositionFormat, Index + 1, Count);

        /// <summary>The next card; false on the last one (the tour ends).</summary>
        public bool Next()
        {
            if (IsLast)
                return false;
            Index++;
            return true;
        }

        /// <summary>The previous card; false on the first one.</summary>
        public bool Back()
        {
            if (Index == 0)
                return false;
            Index--;
            return true;
        }
    }
}
