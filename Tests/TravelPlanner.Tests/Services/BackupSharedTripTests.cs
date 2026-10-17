using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// TP-10: restoring an old backup must not overwrite the other members' changes in shared trips (or bring back
    /// what they deleted) - shared trips are skipped, restored as copies, or the restore is cancelled.
    /// </summary>
    [Collection("SignedInUser")]
    public class BackupSharedTripTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-bak-shared-" + Guid.NewGuid().ToString("N"));
        private AttachmentService _attachments = null!;
        private TripService _trips = null!;
        private BackupService _backup = null!;

        public Task InitializeAsync()
        {
            _attachments = new AttachmentService(_db.Service, Path.Combine(_root, "files"));
            _trips = new TripService(_db.Service, new ExpenseService(_db.Service), new Mock<INotificationService>().Object, _attachments);
            _backup = new BackupService(_db.Service, _attachments, Path.Combine(_root, "out"));
            ChangeTracker.CurrentUserId = SharingFakes.Me;
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            ChangeTracker.CurrentUserId = null;
            await _db.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>Own trip "Roma" and "Paris", shared with the user as a viewer; returns the backup file.</summary>
        private async Task<(Trip Own, Trip Shared, string Zip)> BackupWithViewerTripAsync()
        {
            var own = await SharingFakes.NewTripAsync(_trips, "Roma");
            var shared = await SharingFakes.NewTripAsync(_trips, "Paris");
            await new ExpenseService(_db.Service).SaveAsync(new Expense { TripId = shared.Id, Amount = 10m, Description = "Metro" });
            await SharingFakes.SetRoleAsync(_db, shared, TripRoles.Viewer);
            return (own, shared, await _backup.ExportAsync());
        }

        private async Task<BackupImportResult> RestoreAsync(string zip, Func<SharedTripsInBackup, Task<SharedTripRestoreChoice>>? decide = null)
        {
            await using var stream = File.OpenRead(zip);
            return await _backup.ImportAsync(stream, "backup.zip", decide);
        }

        [Fact]
        public async Task SharedTrips_AreSkippedByDefault_AndNothingIsQueuedForThem()
        {
            var (own, shared, zip) = await BackupWithViewerTripAsync();

            var result = await RestoreAsync(zip);

            Assert.Equal(1, result.SkippedSharedTrips);
            Assert.Equal(1, result.Trips);
            Assert.Equal(own.SyncId, Assert.Single(await _trips.GetAllAsync()).SyncId);

            var conn = await _db.Connection;
            Assert.Empty(await conn.Table<PendingChange>().Where(p => p.TripSyncId == shared.SyncId || p.EntitySyncId == shared.SyncId).ToListAsync());
            Assert.Contains(await conn.Table<PendingChange>().ToListAsync(), p => p.EntitySyncId == own.SyncId);
        }

        [Fact]
        public async Task SharedTrips_CanBeRestoredAsCopies_WithNewIdentity()
        {
            var (_, shared, zip) = await BackupWithViewerTripAsync();
            SharedTripsInBackup? asked = null;

            var result = await RestoreAsync(zip, s => { asked = s; return Task.FromResult(SharedTripRestoreChoice.Copy); });

            Assert.Equal(new[] { "Paris" }, asked!.TripNames);
            Assert.Equal(1, result.CopiedSharedTrips);

            var copy = (await _trips.GetAllAsync()).Single(t => t.Id == shared.Id);
            Assert.NotEqual(shared.SyncId, copy.SyncId);
            Assert.Equal(0, copy.ChangeSeq);
            Assert.Equal(0, copy.Version);
            Assert.EndsWith(AppResources.Backup_CopySuffix, copy.Name);

            var expense = Assert.Single(await new ExpenseService(_db.Service).GetByTripIdAsync(copy.Id));
            Assert.Equal(10m, expense.Amount);

            // The copy is the user's own trip now (no viewer role), so its upload is not rejected.
            var conn = await _db.Connection;
            var queued = await conn.Table<PendingChange>().ToListAsync();
            Assert.Contains(queued, p => p.EntitySyncId == copy.SyncId && p.Operation == ChangeOperation.Upsert);
            Assert.DoesNotContain(queued, p => p.EntitySyncId == shared.SyncId || p.TripSyncId == shared.SyncId);
            Assert.Equal(TripRole.Owner, (await new TripAccessService(_db.Service).GetAccessAsync(copy.Id)).Role);
        }

        [Fact]
        public async Task Cancel_LeavesEverythingAsItWas()
        {
            var (_, _, zip) = await BackupWithViewerTripAsync();
            await SharingFakes.NewTripAsync(_trips, "Sonradan eklenen");

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                RestoreAsync(zip, _ => Task.FromResult(SharedTripRestoreChoice.Cancel)));

            Assert.Equal(3, (await _trips.GetAllAsync()).Count);
        }

        [Fact]
        public async Task OwnTrip_EditedByAnotherMember_CountsAsShared()
        {
            var own = await SharingFakes.NewTripAsync(_trips, "Roma");
            var expense = new Expense { TripId = own.Id, Amount = 20m, Description = "Pizza" };
            await new ExpenseService(_db.Service).SaveAsync(expense);
            var conn = await _db.Connection;
            await conn.ExecuteAsync("UPDATE Expense SET UpdatedBy = ? WHERE Id = ?", SharingFakes.Ayse, expense.Id);
            var zip = await _backup.ExportAsync();

            var asked = false;
            var result = await RestoreAsync(zip, _ => { asked = true; return Task.FromResult(SharedTripRestoreChoice.Skip); });

            Assert.True(asked);
            Assert.Equal(1, result.SkippedSharedTrips);
            Assert.Empty(await _trips.GetAllAsync());
        }

        [Fact]
        public async Task OwnUnsharedTrips_AreRestoredWithoutAsking()
        {
            await SharingFakes.NewTripAsync(_trips, "Roma");
            var zip = await _backup.ExportAsync();

            var result = await RestoreAsync(zip, _ => throw new InvalidOperationException("must not ask"));

            Assert.Equal(1, result.Trips);
            Assert.Equal(0, result.SkippedSharedTrips);
        }

        [Fact]
        public async Task Guest_IsNeverAsked()
        {
            var (_, _, zip) = await BackupWithViewerTripAsync();
            ChangeTracker.CurrentUserId = null;

            var result = await RestoreAsync(zip, _ => throw new InvalidOperationException("must not ask"));

            Assert.Equal(2, result.Trips);
        }
    }
}
