using Moq;
using Xunit;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// A RefreshView bound to IsBusy runs its Command again the moment IsBusy turns true. That re-entrant call must
    /// join the running load, not start a second one (the members list used to show everyone twice).
    /// </summary>
    public class LoadReentryTests
    {
        private sealed class CountingViewModel : ViewModelBase
        {
            public CountingViewModel() : base(new Mock<IDialogService>().Object) { }

            public int Runs { get; private set; }

            public Task LoadAsync() => RunLoadAsync(async () =>
            {
                Runs++;
                await Task.Delay(20);
            });
        }

        [Fact]
        public async Task Load_ReenteredWhileStarting_RunsOnce()
        {
            var vm = new CountingViewModel();
            Task? reentrant = null;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ViewModelBase.IsBusy) && vm.IsBusy && reentrant is null)
                    reentrant = vm.LoadAsync();
            };

            var first = vm.LoadAsync();
            await Task.WhenAll(first, reentrant!);

            Assert.Equal(1, vm.Runs);
        }

        [Fact]
        public async Task Load_AfterPreviousFinished_RunsAgain()
        {
            var vm = new CountingViewModel();

            await vm.LoadAsync();
            await vm.LoadAsync();

            Assert.Equal(2, vm.Runs);
        }
    }
}
