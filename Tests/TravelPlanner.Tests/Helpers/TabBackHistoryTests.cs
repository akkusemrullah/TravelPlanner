using TravelPlanner.Helpers;
using Xunit;

namespace TravelPlanner.Tests
{
    /// <summary>TP-90: back on a tab's root page goes to the tab opened before it; only Home leaves the app.</summary>
    public class TabBackHistoryTests
    {
        [Fact]
        public void Back_OnHomeAlone_LeavesTheApp()
        {
            var history = new TabBackHistory("Home");
            history.Show("Home");

            Assert.Null(history.Back());
        }

        [Fact]
        public void Back_WalksTheOpenedTabsBackwards_ThenLeaves()
        {
            var history = new TabBackHistory("Home");
            history.Show("Home");
            history.Show("Trips");
            history.Show("Profile");

            Assert.Equal("Trips", history.Back());
            history.Show("Trips"); // the shell reports the tab it went back to
            Assert.Equal("Home", history.Back());
            history.Show("Home");
            Assert.Null(history.Back());
        }

        [Fact]
        public void ReopenedTab_IsKeptOnce()
        {
            var history = new TabBackHistory("Home");
            history.Show("Home");
            history.Show("Trips");
            history.Show("Home");
            history.Show("Trips");
            history.Show("Explore");

            Assert.Equal("Trips", history.Back());
            Assert.Equal("Home", history.Back());
            Assert.Null(history.Back());
        }

        [Fact]
        public void Back_FromAnotherTabWithNoHistory_GoesHome()
        {
            var history = new TabBackHistory("Home");
            history.Show("Explore"); // e.g. the shell was rebuilt on this tab

            Assert.Equal("Home", history.Back());
            Assert.Null(history.Back());
        }
    }
}
