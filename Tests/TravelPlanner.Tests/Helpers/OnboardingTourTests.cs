using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using Xunit;

namespace TravelPlanner.Tests
{
    /// <summary>TP-93: the first-start tour - four short cards, shown once, and the İleri / Başla button.</summary>
    public class OnboardingTourTests
    {
        [Fact]
        public void Tour_HasFourShortCards()
        {
            var steps = OnboardingTour.Steps;

            Assert.Equal(4, steps.Count);
            Assert.All(steps, s =>
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Title));
                Assert.True(s.Title.Length <= 22, $"long title: {s.Title}"); // Android cuts long titles
                Assert.False(string.IsNullOrWhiteSpace(s.Text));
                Assert.StartsWith("Ic.", s.Icon);
            });
        }

        [Fact]
        public void Tour_IsShownOnce()
        {
            var preferences = new InMemoryPreferences();

            Assert.True(OnboardingTour.ShouldShow(preferences));
            OnboardingTour.MarkSeen(preferences);
            Assert.False(OnboardingTour.ShouldShow(preferences));
        }

        [Fact]
        public void Progress_WalksTheCards_AndEndsWithBasla()
        {
            var progress = new OnboardingProgress(4);

            Assert.False(progress.Back());
            Assert.Equal(AppResources.Onboarding_Next, progress.ActionText);
            Assert.True(progress.Next());
            Assert.True(progress.Next());
            Assert.True(progress.Next());
            Assert.True(progress.IsLast);
            Assert.Equal(AppResources.Onboarding_Start, progress.ActionText);
            Assert.Equal("4 / 4", progress.PositionText);
            Assert.False(progress.Next());
            Assert.True(progress.Back());
            Assert.Equal(2, progress.Index);
        }

        [Fact]
        public async Task ShowAgain_WithoutAnApp_DoesNothing()
        {
            var presenter = OnboardingTour.Presenter;
            try
            {
                var shown = 0;
                OnboardingTour.Presenter = null;
                await OnboardingTour.ShowAgainAsync();
                OnboardingTour.Presenter = () => { shown++; return Task.CompletedTask; };
                await OnboardingTour.ShowAgainAsync();
                Assert.Equal(1, shown);
            }
            finally
            {
                OnboardingTour.Presenter = presenter;
            }
        }
    }
}
