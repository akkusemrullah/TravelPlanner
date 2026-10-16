using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class BudgetAlertServiceTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IExpenseService> _expenses = new();
        private readonly Mock<INotificationService> _notifications = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();
        private readonly InMemoryPreferences _preferences = new();
        private double _usage;

        public BudgetAlertServiceTests()
        {
            var trip = new Trip { Id = 5, Name = "Roma", City = "Roma", PlannedBudget = 1000m };
            _trips.Setup(t => t.GetByIdAsync(5)).ReturnsAsync(trip);
            _expenses.Setup(e => e.GetBudgetUsagePercentageAsync(5, 1000m)).ReturnsAsync(() => _usage);
            _notifications.Setup(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), It.IsAny<int>(), It.IsAny<double>())).ReturnsAsync(true);
        }

        private BudgetAlertService CreateService() =>
            new(_trips.Object, _expenses.Object, _notifications.Object, _dialogs.Object, _preferences);

        [Theory]
        [InlineData(0, 0)]
        [InlineData(79.9, 0)]
        [InlineData(80, 80)]
        [InlineData(99.99, 80)]
        [InlineData(100, 100)]
        [InlineData(250, 100)]
        public void Thresholds(double usage, int expected)
        {
            Assert.Equal(expected, BudgetAlertService.GetThreshold(usage));
        }

        [Fact]
        public async Task EachThreshold_NotifiesOnce()
        {
            var service = CreateService();

            _usage = 50; await service.CheckAsync(5);
            _usage = 85; await service.CheckAsync(5);
            _usage = 90; await service.CheckAsync(5);
            _usage = 120; await service.CheckAsync(5);
            _usage = 130; await service.CheckAsync(5);

            _notifications.Verify(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), 80, It.IsAny<double>()), Times.Once);
            _notifications.Verify(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), 100, It.IsAny<double>()), Times.Once);
        }

        [Fact]
        public async Task DroppingBelow_ReArmsTheAlert()
        {
            var service = CreateService();

            _usage = 85; await service.CheckAsync(5);
            _usage = 40; await service.CheckAsync(5); // an expense was deleted
            _usage = 82; await service.CheckAsync(5);

            _notifications.Verify(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), 80, It.IsAny<double>()), Times.Exactly(2));
        }

        [Fact]
        public async Task WithoutNotifications_FallsBackToAnInAppMessage()
        {
            _notifications.Setup(n => n.ShowBudgetAlertAsync(It.IsAny<Trip>(), It.IsAny<int>(), It.IsAny<double>())).ReturnsAsync(false);
            _usage = 101;

            await CreateService().CheckAsync(5);

            _dialogs.Verify(d => d.ShowMessageAsync("Bütçe aşıldı", It.Is<string>(m => m.Contains("Roma") && m.Contains("101"))), Times.Once);
        }

        [Fact]
        public async Task Failures_AreSwallowed()
        {
            _trips.Setup(t => t.GetByIdAsync(5)).ThrowsAsync(new IOException("disk"));

            await CreateService().CheckAsync(5); // must not throw

            _notifications.VerifyNoOtherCalls();
        }

        [Fact]
        public void Messages_AreLocalized()
        {
            var (title, message) = BudgetAlertService.BuildMessage("Roma", 80, 83.4);

            Assert.Equal("Bütçe uyarısı", title);
            Assert.Equal("\"Roma\" seyahatinde bütçenin %83'i harcandı.", message);
        }
    }
}
