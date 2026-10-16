using Moq;
using Xunit;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    internal static class Fakes
    {
        public static IConnectivity Connectivity(bool online = true)
        {
            var connectivity = new Mock<IConnectivity>();
            connectivity.SetupGet(c => c.NetworkAccess).Returns(online ? NetworkAccess.Internet : NetworkAccess.None);
            return connectivity.Object;
        }

        /// <summary>A split service for a trip without participants (TP-504) - the expense form then skips splitting.</summary>
        public static Mock<ISplitService> Split()
        {
            var split = new Mock<ISplitService>();
            split.Setup(s => s.GetParticipantsAsync(It.IsAny<int>())).ReturnsAsync(new List<TravelPlanner.Models.Participant>());
            split.Setup(s => s.GetShareParticipantIdsAsync(It.IsAny<int>())).ReturnsAsync(new List<int>());
            return split;
        }

        /// <summary>A dialog service that answers every confirmation with <paramref name="confirm"/>.</summary>
        public static Mock<IDialogService> Dialogs(bool confirm = true)
        {
            var dialogs = new Mock<IDialogService>();
            dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                   .ReturnsAsync(confirm);
            dialogs.Setup(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<Exception?>()))
                   .Returns(Task.CompletedTask);
            return dialogs;
        }
    }
}
