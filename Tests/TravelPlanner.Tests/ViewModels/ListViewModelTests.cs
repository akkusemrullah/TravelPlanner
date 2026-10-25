using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    public class TripListViewModelTests
    {
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IExchangeRateService> _rates = new();

        private static List<Trip> SampleTrips() => new()
        {
            new Trip { Id = 1, Name = "Roma", City = "Roma" },
            new Trip { Id = 2, Name = "Paris", City = "Paris" },
        };

        [Fact]
        public async Task OverlappingLoads_RunOnce_AndDoNotDuplicateRows()
        {
            var gate = new TaskCompletionSource<List<Trip>>();
            _trips.Setup(t => t.GetAllAsync()).Returns(gate.Task);
            var vm = new TripListViewModel(_trips.Object, _rates.Object, Fakes.Dialogs().Object);

            // e.g. OnAppearing firing while a previous load is still running (TP-104)
            var first = vm.LoadTripsCommand.ExecuteAsync(null);
            var second = vm.LoadTripsCommand.ExecuteAsync(null);
            gate.SetResult(SampleTrips());
            await Task.WhenAll(first, second);

            _trips.Verify(t => t.GetAllAsync(), Times.Once);
            Assert.Equal(2, vm.Trips.Count);
            Assert.True(vm.HasTrips);
        }

        [Fact]
        public async Task LoadFailure_ShowsAnError_InsteadOfThrowing()
        {
            _trips.Setup(t => t.GetAllAsync()).ThrowsAsync(new InvalidOperationException("disk full"));
            var dialogs = Fakes.Dialogs();
            var vm = new TripListViewModel(_trips.Object, _rates.Object, dialogs.Object);

            await vm.LoadTripsCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ShowRetryAsync("Seyahatler yüklenemedi.", It.IsAny<InvalidOperationException>()), Times.Once);
            Assert.False(vm.IsBusy);
        }

        [Fact]
        public async Task TP81_LoadFailure_TryAgain_LoadsAgain()
        {
            var calls = 0;
            _trips.Setup(t => t.GetAllAsync()).Returns(() => ++calls == 1
                ? Task.FromException<List<Trip>>(new InvalidOperationException("offline"))
                : Task.FromResult(new List<Trip> { new() { Id = 1, Name = "Roma", StartDate = DateTime.Today, EndDate = DateTime.Today } }));
            var dialogs = Fakes.Dialogs();
            dialogs.Setup(d => d.ShowRetryAsync(It.IsAny<string>(), It.IsAny<Exception?>())).ReturnsAsync(true);
            var vm = new TripListViewModel(_trips.Object, _rates.Object, dialogs.Object);

            await vm.LoadTripsCommand.ExecuteAsync(null);

            Assert.Equal(2, calls);
            dialogs.Verify(d => d.ShowRetryAsync(It.IsAny<string>(), It.IsAny<Exception?>()), Times.Once);
            Assert.False(vm.IsBusy);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Delete_OnlyHappensAfterConfirmation(bool userConfirms)
        {
            _trips.Setup(t => t.GetAllAsync()).ReturnsAsync(SampleTrips());
            _trips.Setup(t => t.GetRelatedCountsAsync(It.IsAny<int>())).ReturnsAsync((2, 1, 0, 0));
            var dialogs = Fakes.Dialogs(confirm: userConfirms);
            var vm = new TripListViewModel(_trips.Object, _rates.Object, dialogs.Object);
            await vm.LoadTripsCommand.ExecuteAsync(null);

            vm.Selection.Start();
            vm.Selection.Toggle(vm.Trips[0]);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            // One trip selected: the usual message, with what goes with it.
            dialogs.Verify(d => d.ConfirmAsync("Seyahati sil", It.Is<string>(m => m.Contains("2 görev") && m.Contains("1 harcama")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _trips.Verify(t => t.DeleteAsync(It.IsAny<Trip>()), userConfirms ? Times.Once() : Times.Never());
            Assert.Equal(userConfirms ? 1 : 2, vm.Trips.Count);
        }

        [Fact]
        public async Task SeveralSelectedTrips_AreDeletedAfterOneConfirmation()
        {
            _trips.Setup(t => t.GetAllAsync()).ReturnsAsync(SampleTrips());
            var dialogs = Fakes.Dialogs();
            var vm = new TripListViewModel(_trips.Object, _rates.Object, dialogs.Object);
            await vm.LoadTripsCommand.ExecuteAsync(null);

            vm.Selection.Start();
            vm.Selection.ToggleAll();
            Assert.Equal(2, vm.Selection.Count);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            // every row is ticked: the title says "Tümünü sil" (ListSelection.DeleteTitle)
            dialogs.Verify(d => d.ConfirmAsync(AppResources.Selection_DeleteAllTitle, It.Is<string>(m => m.Contains("2 seyahat")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _trips.Verify(t => t.DeleteAsync(It.IsAny<Trip>()), Times.Exactly(2));
            Assert.Empty(vm.Trips);
            Assert.True(vm.HasNoTrips);
            Assert.False(vm.Selection.IsActive);
        }

        [Fact]
        public async Task TappingATrip_InSelectionMode_TicksItInsteadOfOpeningIt()
        {
            _trips.Setup(t => t.GetAllAsync()).ReturnsAsync(SampleTrips());
            var vm = new TripListViewModel(_trips.Object, _rates.Object, Fakes.Dialogs().Object);
            await vm.LoadTripsCommand.ExecuteAsync(null);
            vm.Selection.Start();

            await vm.TripTappedCommand.ExecuteAsync(vm.Trips[1]);

            Assert.True(vm.Trips[1].IsSelected);
            Assert.Equal(1, vm.Selection.Count);
            Assert.Equal("1 seçildi", vm.Selection.Title);
        }
    }

    public class TodoListViewModelTests
    {
        private readonly Mock<ITodoService> _todos = new();
        private readonly Mock<ITripService> _trips = new();
        private readonly Mock<IWeatherService> _weather = new();
        private readonly Mock<IPackingService> _packing = new();

        private TodoListViewModel CreateViewModel(Mock<IDialogService>? dialogs = null) =>
            new(_todos.Object, _trips.Object, _weather.Object, _packing.Object, (dialogs ?? Fakes.Dialogs()).Object);

        [Fact]
        public async Task SetCompleted_WithUnchangedValue_DoesNotSave()
        {
            // The CheckBox raises CheckedChanged while rows are being bound; those must not hit the DB (TP-105).
            var vm = CreateViewModel();
            var task = new TodoItem { Id = 1, Title = "Pasaport", IsCompleted = true };

            await vm.SetCompletedCommand.ExecuteAsync(new TodoCompletionChange(task, true));

            _todos.Verify(t => t.SaveAsync(It.IsAny<TodoItem>()), Times.Never);
        }

        [Fact]
        public async Task SetCompleted_WithNewValue_SavesIt()
        {
            var vm = CreateViewModel();
            var task = new TodoItem { Id = 1, Title = "Pasaport", IsCompleted = false };

            await vm.SetCompletedCommand.ExecuteAsync(new TodoCompletionChange(task, true));

            Assert.True(task.IsCompleted);
            _todos.Verify(t => t.SaveAsync(task), Times.Once);
        }

        [Fact]
        public async Task SetCompleted_RevertsTheFlag_WhenSavingFails()
        {
            _todos.Setup(t => t.SaveAsync(It.IsAny<TodoItem>())).ThrowsAsync(new IOException());
            var dialogs = Fakes.Dialogs();
            var vm = CreateViewModel(dialogs);
            var task = new TodoItem { Id = 1, Title = "Pasaport", IsCompleted = false };

            await vm.SetCompletedCommand.ExecuteAsync(new TodoCompletionChange(task, true));

            Assert.False(task.IsCompleted);
            dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception?>()), Times.Once);
        }

        [Fact]
        public async Task AddTask_TrimsTitle_AndClearsTheInput()
        {
            var vm = CreateViewModel();
            vm.TripId = 7;
            vm.NewTaskTitle = "  Pasaport  ";

            await vm.AddTaskCommand.ExecuteAsync(null);

            var added = Assert.Single(vm.Tasks);
            Assert.Equal("Pasaport", added.Title);
            Assert.Equal(7, added.TripId);
            Assert.Equal(string.Empty, vm.NewTaskTitle);
            Assert.True(vm.HasTasks);
        }

        [Fact]
        public async Task SelectAll_ThenDelete_DeletesEveryTask_AfterOneConfirmation()
        {
            _todos.Setup(t => t.GetByTripIdAsync(7)).ReturnsAsync(new List<TodoItem>
            {
                new() { Id = 1, TripId = 7, Title = "Pasaport" },
                new() { Id = 2, TripId = 7, Title = "Şarj aleti" },
                new() { Id = 3, TripId = 7, Title = "Adaptör" },
            });
            var dialogs = Fakes.Dialogs();
            var vm = CreateViewModel(dialogs);
            vm.TripId = 7;
            await vm.LoadTasksCommand.ExecuteAsync(null);

            // the trash button goes straight into selection mode - no "what to delete?" question
            vm.Selection.StartCommand.Execute(null);
            Assert.True(vm.Selection.IsActive);
            dialogs.Verify(d => d.ChooseAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<string?>()), Times.Never);

            vm.Selection.ToggleAllCommand.Execute(null);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ConfirmAsync(AppResources.Selection_DeleteAllTitle, It.Is<string>(m => m.Contains("3 görev")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _todos.Verify(t => t.DeleteAsync(It.IsAny<TodoItem>()), Times.Exactly(3));
            Assert.Empty(vm.Tasks);
            Assert.True(vm.HasNoTasks);
        }

        [Fact]
        public async Task TickingSomeTasks_ThenDelete_DeletesOnlyThose()
        {
            _todos.Setup(t => t.GetByTripIdAsync(7)).ReturnsAsync(new List<TodoItem>
            {
                new() { Id = 1, TripId = 7, Title = "Pasaport" },
                new() { Id = 2, TripId = 7, Title = "Şarj aleti" },
                new() { Id = 3, TripId = 7, Title = "Adaptör" },
            });
            var dialogs = Fakes.Dialogs();
            var vm = CreateViewModel(dialogs);
            vm.TripId = 7;
            await vm.LoadTasksCommand.ExecuteAsync(null);

            vm.Selection.StartCommand.Execute(null);
            vm.TaskTappedCommand.Execute(vm.Tasks[0]);
            vm.TaskTappedCommand.Execute(vm.Tasks[2]);
            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ConfirmAsync(AppResources.Selection_DeleteSelectedTitle, It.Is<string>(m => m.Contains("2 görev")), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            _todos.Verify(t => t.DeleteAsync(It.Is<TodoItem>(x => x.Id == 1 || x.Id == 3)), Times.Exactly(2));
            _todos.Verify(t => t.DeleteAsync(It.Is<TodoItem>(x => x.Id == 2)), Times.Never);
            Assert.Equal("Şarj aleti", Assert.Single(vm.Tasks).Title);
            Assert.False(vm.Selection.IsActive);
        }

        [Fact]
        public void StartingSelection_OnAnEmptyList_DoesNothing()
        {
            var vm = CreateViewModel();

            vm.Selection.StartCommand.Execute(null);

            Assert.False(vm.Selection.IsActive);
        }

        [Fact]
        public async Task DeleteSelected_WithNothingTicked_DoesNothing()
        {
            var dialogs = Fakes.Dialogs();
            var vm = CreateViewModel(dialogs);
            vm.Selection.Start();

            await vm.DeleteSelectedCommand.ExecuteAsync(null);

            dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            _todos.Verify(t => t.DeleteAsync(It.IsAny<TodoItem>()), Times.Never);
        }
    }

    public class ListSelectionTests
    {
        private static List<Place> Rows(params int[] ids) => ids.Select(id => new Place { Id = id, Name = $"Yer {id}" }).ToList();

        [Fact]
        public void Toggle_OnlyWorksInSelectionMode()
        {
            var rows = Rows(1, 2);
            var selection = new ListSelection<Place>(() => rows);

            selection.Toggle(rows[0]);
            Assert.Equal(0, selection.Count);

            selection.Start();
            selection.Toggle(rows[0]);
            Assert.True(rows[0].IsSelected);
            selection.Toggle(rows[0]);
            Assert.False(rows[0].IsSelected);
            Assert.False(selection.HasSelection);
        }

        [Fact]
        public void ToggleAll_SelectsEverything_ThenNothing()
        {
            var rows = Rows(1, 2, 3);
            var selection = new ListSelection<Place>(() => rows);
            selection.Start();

            selection.ToggleAll();
            Assert.True(selection.AllSelected);
            Assert.Equal(AppResources.Selection_ClearAll, selection.ToggleAllText);
            Assert.All(rows, r => Assert.True(r.IsSelected));

            selection.ToggleAll();
            Assert.Equal(0, selection.Count);
            Assert.Equal(AppResources.Selection_SelectAll, selection.ToggleAllText);
        }

        [Fact]
        public void Sync_KeepsTheSelectionAcrossAReload_AndDropsVanishedRows()
        {
            var rows = Rows(1, 2, 3);
            var selection = new ListSelection<Place>(() => rows);
            selection.Start();
            selection.Toggle(rows[0]);
            selection.Toggle(rows[2]);

            rows = Rows(1, 2); // reloaded: new objects, row 3 is gone
            selection.Sync();

            Assert.Equal(1, selection.Count);
            Assert.True(rows[0].IsSelected);
            Assert.False(rows[1].IsSelected);
        }

        [Fact]
        public void Cancel_ClearsTheTicks_AndLeavesTheMode()
        {
            var rows = Rows(1, 2);
            var selection = new ListSelection<Place>(() => rows);
            selection.Start(rows[1]);

            selection.Cancel();

            Assert.False(selection.IsActive);
            Assert.False(rows[1].IsSelected);
            Assert.Equal(0, selection.Count);
        }
    }
}
