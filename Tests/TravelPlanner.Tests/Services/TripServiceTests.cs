using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class TripServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly Mock<INotificationService> _notifications = new();
        private TripService _service = null!;
        private TodoService _todos = null!;
        private PlaceService _places = null!;
        private ExpenseService _expenses = null!;

        public Task InitializeAsync()
        {
            _expenses = new ExpenseService(_db.Service);
            _todos = new TodoService(_db.Service);
            _places = new PlaceService(_db.Service);
            _service = new TripService(_db.Service, _expenses, _notifications.Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static Trip NewTrip(string name, decimal budget = 0m) => new()
        {
            Name = name,
            City = name,
            StartDate = DateTime.Today.AddDays(10),
            EndDate = DateTime.Today.AddDays(12),
            PlannedBudget = budget
        };

        [Fact]
        public async Task Save_AssignsId_AndReschedulesNotifications()
        {
            var trip = NewTrip("Roma");

            await _service.SaveAsync(trip);

            Assert.True(trip.Id > 0);
            _notifications.Verify(n => n.CancelTripNotifications(trip.Id), Times.Once);
            _notifications.Verify(n => n.ScheduleTripNotificationsAsync(trip), Times.Once);
        }

        [Fact]
        public async Task GetAll_IsOrderedByStartDate()
        {
            var later = NewTrip("Later");
            later.StartDate = DateTime.Today.AddDays(30);
            later.EndDate = later.StartDate.AddDays(1);
            await _service.SaveAsync(later);
            await _service.SaveAsync(NewTrip("Sooner"));

            var all = await _service.GetAllAsync();

            Assert.Equal(new[] { "Sooner", "Later" }, all.Select(t => t.Name));
        }

        [Fact]
        public async Task Delete_CascadesToOwnRecordsOnly()
        {
            var trip = NewTrip("Roma");
            var other = NewTrip("Paris");
            await _service.SaveAsync(trip);
            await _service.SaveAsync(other);

            await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Pasaport" });
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 10m });
            await _places.SaveAsync(new Place { TripId = trip.Id, Name = "Kolezyum" });
            await new ActivityService(_db.Service).SaveAsync(new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Tur" });
            await _todos.SaveAsync(new TodoItem { TripId = other.Id, Title = "Bilet" });

            Assert.Equal((1, 1, 1, 1), await _service.GetRelatedCountsAsync(trip.Id));

            await _service.DeleteAsync(trip);

            Assert.Null(await _service.GetByIdAsync(trip.Id));
            Assert.Equal((0, 0, 0, 0), await _service.GetRelatedCountsAsync(trip.Id));
            Assert.Single(await _todos.GetByTripIdAsync(other.Id));
            _notifications.Verify(n => n.CancelTripNotifications(trip.Id), Times.AtLeastOnce);
        }

        [Fact]
        public async Task BudgetFigures_ComeFromTheTripsPlannedBudget()
        {
            var trip = NewTrip("Roma", budget: 500m);
            await _service.SaveAsync(trip);
            await _expenses.SaveAsync(new Expense { TripId = trip.Id, Amount = 125m });

            Assert.Equal(125m, await _service.GetTotalExpensesAsync(trip.Id));
            Assert.Equal(375m, await _service.GetRemainingBudgetAsync(trip.Id));
            Assert.Equal(25d, await _service.GetBudgetUsagePercentageAsync(trip.Id), precision: 6);
        }

        [Fact]
        public async Task BudgetFigures_AreZero_ForUnknownTrip()
        {
            Assert.Equal(0m, await _service.GetRemainingBudgetAsync(999));
            Assert.Equal(0d, await _service.GetBudgetUsagePercentageAsync(999));
        }

        [Fact]
        public async Task TodoAndPlace_CrudRoundTrip()
        {
            var trip = NewTrip("Roma");
            await _service.SaveAsync(trip);

            var todo = new TodoItem { TripId = trip.Id, Title = "Pasaport" };
            await _todos.SaveAsync(todo);
            todo.IsCompleted = true;
            await _todos.SaveAsync(todo);
            Assert.True((await _todos.GetByTripIdAsync(trip.Id)).Single().IsCompleted);
            await _todos.DeleteAsync(todo);
            Assert.Empty(await _todos.GetByTripIdAsync(trip.Id));

            var place = new Place { TripId = trip.Id, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 };
            await _places.SaveAsync(place);
            place.IsVisited = true;
            await _places.SaveAsync(place);
            Assert.True((await _places.GetByIdAsync(place.Id))!.IsVisited);
            await _places.DeleteAsync(place);
            Assert.Empty(await _places.GetByTripIdAsync(trip.Id));
        }
    }
}
