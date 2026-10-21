using Microsoft.Maui.Networking;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-48: "Kodla katıl" on the home screen, the trip list and Settings share one flow.</summary>
    public class JoinTripFlowTests
    {
        private readonly Mock<ISharingService> _sharing = new();
        private readonly Mock<IAccountService> _account = new();
        private readonly Mock<IDialogService> _dialogs = new();
        private readonly Mock<IConnectivity> _connectivity = new();

        public JoinTripFlowTests()
        {
            _account.SetupGet(a => a.IsSignedIn).Returns(true);
            _connectivity.SetupGet(c => c.NetworkAccess).Returns(NetworkAccess.Internet);
        }

        private JoinTripFlow Flow() => new(_sharing.Object, _account.Object, _dialogs.Object, _connectivity.Object);

        private void TypeCode(string? code) =>
            _dialogs.Setup(d => d.PromptAsync(AppResources.Join_Title, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()))
                    .ReturnsAsync(code);

        [Fact]
        public async Task AValidCode_ReturnsTheJoinedTrip()
        {
            TypeCode("ABCD 2345");
            _sharing.Setup(s => s.AcceptInviteAsync("ABCD 2345")).ReturnsAsync(new JoinedTrip("sync", TripRole.Viewer, "Roma", 7));

            Assert.Equal(7, await Flow().RunAsync());
            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Offline_SaysSoBeforeAskingForTheCode()
        {
            _connectivity.SetupGet(c => c.NetworkAccess).Returns(NetworkAccess.None);

            Assert.Null(await Flow().RunAsync());

            _dialogs.Verify(d => d.ShowMessageAsync(AppResources.Common_Info, AppResources.Sharing_ErrorOffline), Times.Once);
            _dialogs.Verify(d => d.PromptAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }

        [Fact]
        public async Task SignedOut_AsksToSignIn()
        {
            _account.SetupGet(a => a.IsSignedIn).Returns(false);

            Assert.Null(await Flow().RunAsync());

            _dialogs.Verify(d => d.ShowMessageAsync(AppResources.Common_Info, AppResources.Sharing_ErrorNotSignedIn), Times.Once);
            _sharing.Verify(s => s.AcceptInviteAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task AWrongCode_ShowsTheReason()
        {
            TypeCode("XXXX0000");
            _sharing.Setup(s => s.AcceptInviteAsync("XXXX0000")).ThrowsAsync(new SharingException(SharingErrorKind.InvalidInvite));

            Assert.Null(await Flow().RunAsync());

            _dialogs.Verify(d => d.ShowMessageAsync(AppResources.Common_Info, AppResources.Sharing_ErrorInvalidInvite), Times.Once);
        }

        [Fact]
        public async Task Cancelling_DoesNothing()
        {
            TypeCode(null);

            Assert.Null(await Flow().RunAsync());

            _sharing.Verify(s => s.AcceptInviteAsync(It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task JoinedButNotSyncedYet_SaysItWillAppear()
        {
            TypeCode("ABCD2345");
            _sharing.Setup(s => s.AcceptInviteAsync("ABCD2345")).ReturnsAsync(new JoinedTrip("sync", TripRole.Viewer, "Roma", null));

            Assert.Null(await Flow().RunAsync());

            _dialogs.Verify(d => d.ShowMessageAsync(AppResources.Common_Info, string.Format(AppResources.Join_PendingFormat, "Roma")), Times.Once);
        }
    }
}
