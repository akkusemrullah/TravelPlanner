using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class ActivityServiceTests : IAsyncLifetime
    {
        private static readonly DateTime Day1 = new(2026, 8, 20);
        private static readonly DateTime Day2 = new(2026, 8, 21);

        private readonly TestDatabase _db = new();
        private ActivityService _service = null!;

        public Task InitializeAsync()
        {
            _service = new ActivityService(_db.Service);
            return Task.CompletedTask;
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        private static TripActivity At(string title, int? hour, DateTime? day = null) => new()
        {
            TripId = 1,
            Date = day ?? Day1,
            Title = title,
            StartTime = hour is int h ? new TimeSpan(h, 0, 0) : null
        };

        private async Task<string[]> TitlesAsync(DateTime day) =>
            (await _service.GetByDateAsync(1, day)).Select(a => a.Title).ToArray();

        [Fact]
        public async Task NewTimedActivities_AreSlottedChronologically_UntimedGoLast()
        {
            await _service.SaveAsync(At("Öğle yemeği", 13));
            await _service.SaveAsync(At("Serbest zaman", null));
            await _service.SaveAsync(At("Müze", 9));
            await _service.SaveAsync(At("Akşam yemeği", 20));

            Assert.Equal(new[] { "Müze", "Öğle yemeği", "Akşam yemeği", "Serbest zaman" }, await TitlesAsync(Day1));
        }

        [Fact]
        public async Task Reorder_PersistsTheNewOrder()
        {
            await _service.SaveAsync(At("A", 9));
            await _service.SaveAsync(At("B", 10));
            await _service.SaveAsync(At("C", 11));

            var day = await _service.GetByDateAsync(1, Day1);
            await _service.ReorderAsync(new[] { day[2], day[0], day[1] });

            Assert.Equal(new[] { "C", "A", "B" }, await TitlesAsync(Day1));
        }

        [Fact]
        public async Task EditingWithoutChangingDayOrTime_KeepsTheManualOrder()
        {
            await _service.SaveAsync(At("A", 9));
            await _service.SaveAsync(At("B", 10));
            var day = await _service.GetByDateAsync(1, Day1);
            await _service.ReorderAsync(new[] { day[1], day[0] }); // B, A

            var a = (await _service.GetByDateAsync(1, Day1)).Single(x => x.Title == "A");
            a.Note = "Bilet al";
            await _service.SaveAsync(a);

            Assert.Equal(new[] { "B", "A" }, await TitlesAsync(Day1));
        }

        [Fact]
        public async Task MovingToAnotherDay_SlotsItIntoThatDay()
        {
            await _service.SaveAsync(At("Tur", 15));
            await _service.SaveAsync(At("Kahvaltı", 8, Day2));
            await _service.SaveAsync(At("Akşam", 19, Day2));

            var tour = (await _service.GetByDateAsync(1, Day1)).Single();
            tour.Date = Day2;
            await _service.SaveAsync(tour);

            Assert.Empty(await TitlesAsync(Day1));
            Assert.Equal(new[] { "Kahvaltı", "Tur", "Akşam" }, await TitlesAsync(Day2));
        }

        [Fact]
        public async Task EndTime_IsDropped_WhenThereIsNoStartTime()
        {
            var activity = At("Plaj", null);
            activity.EndTime = new TimeSpan(18, 0, 0);

            await _service.SaveAsync(activity);

            Assert.Null((await _service.GetByIdAsync(activity.Id))!.EndTime);
        }

        [Fact]
        public async Task DeletingAPlace_UnlinksItsActivities()
        {
            var places = new PlaceService(_db.Service);
            var place = new Place { TripId = 1, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 };
            await places.SaveAsync(place);

            var activity = At("Kolezyum turu", 10);
            activity.PlaceId = place.Id;
            await _service.SaveAsync(activity);

            await places.DeleteAsync(place);

            var stored = await _service.GetByIdAsync(activity.Id);
            Assert.NotNull(stored);
            Assert.Null(stored!.PlaceId);
        }

        [Fact]
        public void TimeDisplay_ShowsStartAndEnd()
        {
            var activity = new TripActivity { StartTime = new TimeSpan(9, 30, 0), EndTime = new TimeSpan(11, 0, 0) };

            Assert.Equal("09:30 – 11:00", activity.TimeDisplay);
            Assert.Equal(string.Empty, new TripActivity().TimeDisplay);
        }

        [Theory]
        [InlineData(null, 3)]
        [InlineData(8, 0)]
        [InlineData(10, 1)]
        [InlineData(13, 2)]
        public void FindInsertIndex_FollowsTheClock(int? hour, int expected)
        {
            var day = new List<TripActivity> { At("09", 9), At("12", 12), At("free", null) };

            Assert.Equal(expected, ItineraryOrdering.FindInsertIndex(day, hour is int h ? new TimeSpan(h, 0, 0) : null));
        }
    }
}
