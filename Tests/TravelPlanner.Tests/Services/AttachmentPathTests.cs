using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// TP-5: an attachment row can only lead to its own photo/PDF file - the cloud path is checked before a download,
    /// escaped in the request, and the type a file is opened with comes from its extension, never from the server.
    /// </summary>
    public class AttachmentPathTests
    {
        private const string Trip = "11111111-1111-1111-1111-111111111111";
        private const string Id = "aaaaaaaa-0000-0000-0000-000000000001";

        [Theory]
        [InlineData("trips/" + Trip + "/" + Id + ".pdf", true)]
        [InlineData("trips/" + Trip + "/" + Id + ".jpg", true)]
        [InlineData("trips/" + Trip + "/" + Id + ".heic", true)]
        [InlineData("trips/" + Trip + "/aaaaaaaa-0000-0000-0000-000000000002.pdf", false)] // another row's file
        [InlineData("trips/22222222-2222-2222-2222-222222222222/" + Id + ".pdf", false)]     // another trip's
        [InlineData("trips/" + Trip + "/" + Id + ".apk", false)]                             // not a photo/PDF
        [InlineData("trips/" + Trip + "/" + Id + ".PDF", false)]
        [InlineData("trips/" + Trip + "/" + Id, false)]
        [InlineData("trips/" + Trip + "/" + Id + ".pdf?x=1", false)]
        [InlineData("trips/" + Trip + "/" + Id + ".pdf#x", false)]
        [InlineData("trips/" + Trip + "/../" + Id + ".pdf", false)]
        [InlineData("trips/" + Trip + "/" + Id + ".pdf\n", false)]
        [InlineData("/trips/" + Trip + "/" + Id + ".pdf", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsValidRemotePath_IsOnlyTheAttachmentsOwnPhotoOrPdf(string? path, bool expected)
        {
            Assert.Equal(expected, AttachmentService.IsValidRemotePath(path, Trip, Id));
        }

        [Fact]
        public void IsValidRemotePath_NeedsBothIds()
        {
            var path = $"trips/{Trip}/{Id}.pdf";
            Assert.False(AttachmentService.IsValidRemotePath(path, null, Id));
            Assert.False(AttachmentService.IsValidRemotePath(path, Trip, ""));
        }

        [Theory]
        [InlineData("bilet.pdf", null, ".pdf")]
        [InlineData("IMG_1.JPG", null, ".jpg")]
        [InlineData("foto.jpeg", "image/jpeg", ".jpeg")]
        [InlineData("IMG_1234", "image/heic", ".heic")]
        [InlineData("scan.jpe", "image/jpeg", ".jpg")]
        [InlineData("bilet.apk", "application/pdf", ".pdf")]
        [InlineData("bilet.apk", null, null)]
        [InlineData("notlar.docx", "application/msword", null)]
        [InlineData(null, null, null)]
        public void CloudExtension_IsAlwaysAPhotoOrPdfExtension(string? name, string? type, string? expected)
        {
            Assert.Equal(expected, AttachmentService.CloudExtension(name, type));
        }

        [Fact]
        public void OpenContentType_ComesFromTheExtension_NotFromTheStoredType()
        {
            var ticket = new TripAttachment
            {
                RelativePath = "attachments/3/x.pdf",
                ContentType = "application/vnd.android.package-archive",
            };
            Assert.Equal("application/pdf", AttachmentService.OpenContentType(ticket));

            var noExtension = new TripAttachment { RelativePath = "attachments/3/x", RemotePath = $"trips/{Trip}/{Id}.jpg", ContentType = "text/html" };
            Assert.Equal("image/jpeg", AttachmentService.OpenContentType(noExtension));

            var apk = new TripAttachment { RelativePath = "attachments/3/x.apk", RemotePath = $"trips/{Trip}/{Id}.apk", ContentType = "application/pdf" };
            Assert.Null(AttachmentService.OpenContentType(apk));
        }

        [Fact]
        public void LocalPathFor_NeverTakesAForeignExtension()
        {
            var row = new TripAttachment { TripId = 3, SyncId = Id, DisplayName = "bilet.apk", ContentType = null };
            Assert.Equal($"attachments/3/{Id}", RemoteApplier.LocalPathFor(row));

            row.ContentType = "application/pdf";
            Assert.Equal($"attachments/3/{Id}.pdf", RemoteApplier.LocalPathFor(row));

            row.RemotePath = $"trips/{Trip}/{Id}.jpg";
            Assert.Equal($"attachments/3/{Id}.jpg", RemoteApplier.LocalPathFor(row));
        }

        [Fact]
        public void Validator_DropsTypesOutsideTheList_AndBrokenPaths()
        {
            var row = new TripAttachment
            {
                DisplayName = "x", RelativePath = "attachments/1/x.pdf",
                ContentType = "application/vnd.android.package-archive", RemotePath = $"trips/{Trip}/../x",
            };
            Assert.Null(EntityValidator.Validate(row));
            Assert.Null(row.ContentType);
            Assert.Null(row.RemotePath);

            row.ContentType = "IMAGE/JPG";
            row.RemotePath = $"trips/{Trip}/{Id}.jpg";
            Assert.Null(EntityValidator.Validate(row));
            Assert.Equal("image/jpeg", row.ContentType);
            Assert.Equal($"trips/{Trip}/{Id}.jpg", row.RemotePath);
        }

        // ------------------------------------------------------------------ the request itself

        [Fact]
        public void ObjectUrl_EscapesEverySegment()
        {
            Assert.Equal($"storage/v1/object/trip-files/trips/{Trip}/{Id}.pdf", SupabaseClient.ObjectUrl("trip-files", $"trips/{Trip}/{Id}.pdf"));
            Assert.Equal("storage/v1/object/trip-files/trips/a%3Fb%23c%20d%25.pdf", SupabaseClient.ObjectUrl("trip-files", "trips/a?b#c d%.pdf"));
        }

        [Theory]
        [InlineData("trips/x/../../rest/v1/profiles")]
        [InlineData("trips/./x.pdf")]
        [InlineData("trips//x.pdf")]
        [InlineData("/trips/x.pdf")]
        [InlineData("")]
        public void ObjectUrl_RefusesDotAndEmptySegments(string path)
        {
            var ex = Assert.Throws<SyncException>(() => SupabaseClient.ObjectUrl("trip-files", path));
            Assert.Equal(SyncErrorKind.Rejected, ex.Kind);
        }

        [Fact]
        public async Task Download_StaysInsideTheObjectApi()
        {
            var http = new FakeHttpMessageHandler().Respond("storage/v1/object", "abc");
            var auth = new Mock<IAuthService>();
            auth.Setup(a => a.GetAccessTokenAsync(It.IsAny<CancellationToken>())).ReturnsAsync("token");
            var client = new SupabaseClient(() => http.CreateClient(), auth.Object);

            using var target = new MemoryStream();
            await client.DownloadAsync("trip-files", "trips/t/x.pdf?y#z", target);

            var request = Assert.Single(http.Requests);
            Assert.EndsWith("/storage/v1/object/trip-files/trips/t/x.pdf%3Fy%23z", request.AbsolutePath);
            Assert.Equal(string.Empty, request.Query);
            Assert.Equal(string.Empty, request.Fragment);

            await Assert.ThrowsAsync<SyncException>(() => client.DownloadAsync("trip-files", "trips/t/../../../rest/v1/profiles", target));
            Assert.Single(http.Requests); // nothing was sent
        }
    }

    public class AttachmentOpenTests
    {
        private readonly Mock<IAttachmentService> _attachments = new();
        private readonly Mock<ILauncher> _launcher = new();
        private readonly Mock<IDialogService> _dialogs = Fakes.Dialogs();

        private AttachmentsViewModel Create() =>
            new(_attachments.Object, new Mock<Microsoft.Maui.Media.IMediaPicker>().Object, new Mock<IFilePicker>().Object,
                _launcher.Object, _dialogs.Object) { TripId = 4 };

        [Fact]
        public async Task Open_UsesTheExtensionsType_NotTheServers()
        {
            var ticket = new TripAttachment
            {
                Id = 1, TripId = 4, DisplayName = "bilet", RelativePath = "attachments/4/x.pdf", FullPath = "/tmp/x.pdf",
                ContentType = "application/vnd.android.package-archive",
                RemotePath = "trips/11111111-1111-1111-1111-111111111111/aaaaaaaa-0000-0000-0000-000000000001.pdf",
            };
            _attachments.Setup(a => a.EnsureLocalFileAsync(ticket, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            _launcher.Setup(l => l.OpenAsync(It.IsAny<OpenFileRequest>())).ReturnsAsync(true);

            await Create().OpenCommand.ExecuteAsync(ticket);

            _launcher.Verify(l => l.OpenAsync(It.Is<OpenFileRequest>(r => r.File.ContentType == "application/pdf")), Times.Once);
        }

        [Fact]
        public async Task Open_RefusesACloudFileThatIsNoPhotoOrPdf()
        {
            var apk = new TripAttachment
            {
                Id = 1, TripId = 4, DisplayName = "bilet.pdf", RelativePath = "attachments/4/x", FullPath = "/tmp/x",
                ContentType = "application/pdf",
                RemotePath = "trips/11111111-1111-1111-1111-111111111111/aaaaaaaa-0000-0000-0000-000000000001.apk",
            };
            _attachments.Setup(a => a.EnsureLocalFileAsync(apk, It.IsAny<CancellationToken>())).ReturnsAsync(true);

            await Create().OpenCommand.ExecuteAsync(apk);

            _launcher.Verify(l => l.OpenAsync(It.IsAny<OpenFileRequest>()), Times.Never);
            _dialogs.Verify(d => d.ShowMessageAsync(It.IsAny<string>(), AppResources.Attachments_NotSafeToOpen), Times.Once);
        }
    }
}
