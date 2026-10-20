using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// Trip cover photos: cropped files kept next to the trip's attachments, uploaded by the sync before the trip's
    /// cover_path is sent, downloaded on the trip's other devices, and gone again when removed.
    /// </summary>
    public class TripCoverTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-covers-" + Guid.NewGuid().ToString("N"));
        private readonly FakeSupabaseClient _client = new();
        private readonly Mock<IAuthService> _auth = new();
        private readonly Mock<INotificationService> _notifications = new();
        private readonly Mock<IAccountService> _account = new();
        private readonly Mock<ICoverImageProcessor> _processor = new();
        private readonly List<(int Width, int Height)> _crops = new();
        private readonly List<CropRegion?> _regions = new();
        private (int Width, int Height) _photoSize = (4000, 3000);
        private AttachmentService _attachments = null!;
        private TripService _trips = null!;
        private TripCoverService _covers = null!;
        private SyncService _sync = null!;
        private readonly List<string> _memberTrips = new();

        public Task InitializeAsync()
        {
            _auth.SetupGet(a => a.IsSignedIn).Returns(true);
            // the "cropped" picture: its size as bytes, so the test can tell the cover from the thumbnail
            _processor.Setup(p => p.MeasureAsync(It.IsAny<Stream>())).Returns(() => Task.FromResult(_photoSize));
            _processor.Setup(p => p.CropAsync(It.IsAny<Stream>(), It.IsAny<CropRegion?>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>()))
                .Returns((Stream _, CropRegion? region, int w, int h, float _) =>
                {
                    _crops.Add((w, h));
                    _regions.Add(region);
                    return Task.FromResult(System.Text.Encoding.ASCII.GetBytes($"{w}x{h}"));
                });
            _attachments = new AttachmentService(_db.Service, _root, _client);
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), _notifications.Object, _attachments);
            _covers = new TripCoverService(_db.Service, _attachments, _processor.Object, _client) { OnMainThread = a => a() };
            _sync = new SyncService(_auth.Object, _account.Object, _db.Service, _client, _attachments,
                                    _notifications.Object, Fakes.Connectivity(), _covers);
            _client.Handler = (fn, args) => fn == "apply_changes" ? FakeSupabaseClient.AnswerAll(args, "applied") : FakeSupabaseClient.EmptyPull(0, _memberTrips.ToArray());
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _db.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private async Task<Trip> NewTripAsync()
        {
            var trip = new Trip { Name = "Lizbon", City = "Lizbon", Country = "Portekiz", StartDate = new DateTime(2026, 11, 1), EndDate = new DateTime(2026, 11, 3) };
            await _trips.SaveAsync(trip);
            _memberTrips.Add(trip.SyncId);
            return trip;
        }

        private async Task<string> PickAsync() => await _covers.PrepareAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        [Theory]
        [InlineData("trips/11111111-1111-1111-1111-111111111111/cover-0123456789abcdef0123456789abcdef.jpg", "0123456789abcdef0123456789abcdef")]
        [InlineData("trips/22222222-2222-2222-2222-222222222222/cover-0123456789abcdef0123456789abcdef.jpg", null)] // another trip's
        [InlineData("trips/11111111-1111-1111-1111-111111111111/cover-0123456789abcdef0123456789abcdef-thumb.jpg", null)]
        [InlineData("trips/11111111-1111-1111-1111-111111111111/cover-../x.jpg", null)]
        [InlineData("trips/11111111-1111-1111-1111-111111111111/0123.pdf", null)]
        [InlineData(null, null)]
        public void CoverIdOf_AcceptsOnlyTheTripsOwnCover(string? path, string? expected) =>
            Assert.Equal(expected, TripCoverService.CoverIdOf(path, "11111111-1111-1111-1111-111111111111"));

        [Fact]
        public async Task Prepare_CropsTheCoverAndASquareThumbnail_AndKeepsThemStaged()
        {
            var id = await PickAsync();

            Assert.Equal(new[] { (1280, 720), (256, 256) }, _crops);
            Assert.Equal("1280x720", File.ReadAllText(_covers.StagedFile(id)!));
            _covers.DiscardStaged(id);
            Assert.Null(_covers.StagedFile(id));
        }

        [Fact]
        public async Task Prepare_CropsTheChosenPart_AndTheThumbnailFromTheCover()
        {
            var region = new CropRegion(0.1, 0.2, 0.5, 0.5);
            await _covers.PrepareAsync(new MemoryStream(new byte[] { 1, 2, 3 }), region);

            Assert.Equal(new CropRegion?[] { region, null }, _regions);
        }

        [Fact]
        public async Task Prepare_RefusesARegionOutsideThePhoto() =>
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                _covers.PrepareAsync(new MemoryStream(new byte[] { 1 }), new CropRegion(0.6, 0, 0.5, 0.5)));

        [Fact]
        public async Task Open_KeepsThePhotoForTheCropScreen_UntilDiscarded()
        {
            _photoSize = (1200, 1600);
            var picked = await _covers.OpenAsync(new MemoryStream(new byte[] { 7, 8, 9 }));

            Assert.Equal((1200, 1600), (picked.Width, picked.Height));
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(picked.File));
            _covers.DiscardPicked(picked);
            Assert.False(File.Exists(picked.File));
        }

        [Fact]
        public void DiscardPicked_TouchesOnlyItsOwnFiles()
        {
            var other = Path.Combine(_root, "keep.img");
            Directory.CreateDirectory(_root);
            File.WriteAllBytes(other, new byte[] { 1 });
            _covers.DiscardPicked(new PickedPhoto(other, 1, 1));
            Assert.True(File.Exists(other));
        }

        [Fact]
        public async Task Prepare_RefusesAnEmptyPhoto() =>
            await Assert.ThrowsAsync<InvalidDataException>(() => _covers.PrepareAsync(new MemoryStream()));

        [Fact]
        public async Task Set_ShowsTheCoverAtOnce_ThenSyncUploadsItAndSendsThePath()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync(); // the trip is on the server now

            var id = await PickAsync();
            await _covers.SetAsync(trip, id);

            // shown on this device right away, before any upload
            _covers.ShowThumbnails(new[] { trip });
            Assert.Equal("256x256", File.ReadAllText(trip.CoverThumbFile!));
            Assert.Null((await _trips.GetByIdAsync(trip.Id))!.CoverPath);

            var result = await _sync.SyncNowAsync();

            var cover = $"trips/{trip.SyncId}/cover-{id}.jpg";
            Assert.Equal("1280x720", System.Text.Encoding.ASCII.GetString(_client.Storage["trip-files/" + cover]));
            Assert.Equal("256x256", System.Text.Encoding.ASCII.GetString(_client.Storage[$"trip-files/trips/{trip.SyncId}/cover-{id}-thumb.jpg"]));
            Assert.Equal("image/jpeg", _client.UploadedTypes["trip-files/" + cover]);
            Assert.True(result.FilesUploaded >= 1);

            // the path is sent only after the files (the second push of the round), and nothing device-only goes along
            var sent = _client.Calls.Where(c => c.Function == "apply_changes").Last().Arguments["p_changes"]!.AsArray();
            var fields = sent.Single(c => c!["id"]!.GetValue<string>() == trip.SyncId)!["fields"]!.AsObject();
            Assert.Equal(cover, fields["cover_path"]!.GetValue<string>());
            Assert.False(fields.ContainsKey("pending_cover_id"));

            var stored = await _trips.GetByIdAsync(trip.Id);
            Assert.Equal(cover, stored!.CoverPath);
            Assert.Null(stored.PendingCoverId);
        }

        [Fact]
        public async Task NewTrip_WaitsForTheTripToReachTheServerFirst()
        {
            var trip = await NewTripAsync();
            await _covers.SetAsync(trip, await PickAsync());

            Assert.Equal(0, await _covers.UploadPendingAsync());
            Assert.Empty(_client.Storage);
        }

        [Fact]
        public async Task AnotherDevice_DownloadsTheThumbnailForTheList_AndTheCoverWhenOpened()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync();
            var id = await PickAsync();
            await _covers.SetAsync(trip, id);
            await _sync.SyncNowAsync();

            // the other device: same trip row, no files
            var otherRoot = Path.Combine(_root, "other");
            var other = new TripCoverService(_db.Service, new AttachmentService(_db.Service, otherRoot, _client), _processor.Object, _client) { OnMainThread = a => a() };
            var copy = (await _trips.GetByIdAsync(trip.Id))!;
            copy.PendingCoverId = null;

            var thumb = await other.GetFileAsync(copy, thumbnail: true);
            Assert.StartsWith(otherRoot, thumb);
            Assert.Equal("256x256", File.ReadAllText(thumb!));
            Assert.Equal("1280x720", File.ReadAllText((await other.GetFileAsync(copy, thumbnail: false))!));

            // on the phone now: no more downloads
            var downloads = _client.DownloadAttempts;
            await other.GetFileAsync(copy, thumbnail: true);
            Assert.Equal(downloads, _client.DownloadAttempts);
        }

        [Fact]
        public async Task MissingFile_IsNotAskedForOnEveryScroll()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync();
            var stored = (await _trips.GetByIdAsync(trip.Id))!;
            stored.CoverPath = $"trips/{stored.SyncId}/cover-0123456789abcdef0123456789abcdef.jpg";

            Assert.Null(await _covers.GetFileAsync(stored, thumbnail: true));
            Assert.Null(await _covers.GetFileAsync(stored, thumbnail: true));
            Assert.Equal(1, _client.DownloadAttempts);
        }

        [Fact]
        public async Task Remove_ClearsThePath_AndTheFilesOnThePhone()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync();
            var id = await PickAsync();
            await _covers.SetAsync(trip, id);
            await _sync.SyncNowAsync();
            var file = _covers.LocalFile(trip.Id, id, thumbnail: false);
            Assert.True(File.Exists(file));

            await _covers.RemoveAsync(trip);
            await _sync.SyncNowAsync();

            Assert.False(File.Exists(file));
            Assert.False(_covers.HasCover(trip));
            var sent = _client.Calls.Where(c => c.Function == "apply_changes").Last().Arguments["p_changes"]!.AsArray();
            var fields = sent.Single(c => c!["id"]!.GetValue<string>() == trip.SyncId)!["fields"]!.AsObject();
            Assert.True(fields.ContainsKey("cover_path"));
            Assert.Null(fields["cover_path"]);
        }

        [Fact]
        public async Task ANewPhoto_ReplacesTheOldOne_AfterItsUpload()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync();
            var first = await PickAsync();
            await _covers.SetAsync(trip, first);
            await _sync.SyncNowAsync();

            var second = await PickAsync();
            await _covers.SetAsync(trip, second);
            // until the upload, the new one is shown and the old one is still the trip's
            Assert.Contains(second, await _covers.GetFileAsync(trip, thumbnail: false));
            await _sync.SyncNowAsync();

            Assert.Equal($"trips/{trip.SyncId}/cover-{second}.jpg", (await _trips.GetByIdAsync(trip.Id))!.CoverPath);
            Assert.False(File.Exists(_covers.LocalFile(trip.Id, first, thumbnail: false)));
        }

        [Fact]
        public async Task PendingCover_WhoseFilesAreGone_IsForgotten()
        {
            var trip = await NewTripAsync();
            await _sync.SyncNowAsync();
            var id = await PickAsync();
            await _covers.SetAsync(trip, id);
            File.Delete(_covers.LocalFile(trip.Id, id, thumbnail: true));

            Assert.Equal(0, await _covers.UploadPendingAsync());
            Assert.Null((await _trips.GetByIdAsync(trip.Id))!.PendingCoverId);
        }
    }
}
