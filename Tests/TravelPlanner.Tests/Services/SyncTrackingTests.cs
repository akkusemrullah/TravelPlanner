using Moq;
using SQLite;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;
using TravelPlanner.Services.Sync;

namespace TravelPlanner.Tests
{
    /// <summary>G1: every write keeps sync metadata, the offline outbox and the trash in step.</summary>
    public class SyncTrackingTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-sync-" + Guid.NewGuid().ToString("N"));
        private TripService _trips = null!;
        private ExpenseService _expenses = null!;
        private TodoService _todos = null!;
        private PlaceService _places = null!;
        private ActivityService _activities = null!;
        private SplitService _split = null!;
        private AttachmentService _attachments = null!;
        private TrashService _trash = null!;

        public Task InitializeAsync()
        {
            _expenses = new ExpenseService(_db.Service);
            _attachments = new AttachmentService(_db.Service, _root);
            _trips = new TripService(_db.Service, _expenses, new Mock<INotificationService>().Object, _attachments);
            _todos = new TodoService(_db.Service);
            _places = new PlaceService(_db.Service);
            _activities = new ActivityService(_db.Service);
            _split = new SplitService(_db.Service);
            _trash = new TrashService(_db.Service, _attachments);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _db.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private async Task<Trip> NewTripAsync(string name = "Roma")
        {
            var trip = new Trip { Name = name, City = name, StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            await _trips.SaveAsync(trip);
            return trip;
        }

        private async Task<List<PendingChange>> OutboxAsync() =>
            await (await _db.Connection).Table<PendingChange>().OrderBy(p => p.Sequence).ToListAsync();

        private async Task<PendingChange?> PendingForAsync(ISyncEntity entity) =>
            (await OutboxAsync()).SingleOrDefault(p => p.EntitySyncId == entity.SyncId);

        [Fact]
        public async Task Insert_AssignsASyncId_AndQueuesAFullUpsert()
        {
            var trip = await NewTripAsync();

            Assert.True(Guid.TryParse(trip.SyncId, out _));
            Assert.NotEqual(default, trip.UpdatedAtUtc);

            var pending = Assert.Single(await OutboxAsync());
            Assert.Equal("Trip", pending.EntityType);
            Assert.Equal(trip.SyncId, pending.EntitySyncId);
            Assert.Equal(trip.SyncId, pending.TripSyncId);
            Assert.Equal(ChangeOperation.Upsert, pending.Operation);
            Assert.Null(pending.ChangedFields); // new row: every field
        }

        [Fact]
        public async Task ChildRows_RecordTheirTrip()
        {
            var trip = await NewTripAsync();
            var todo = new TodoItem { TripId = trip.Id, Title = "Pasaport" };
            await _todos.SaveAsync(todo);

            var pending = await PendingForAsync(todo);
            Assert.Equal("Todo", pending!.EntityType);
            Assert.Equal(trip.SyncId, pending.TripSyncId);
        }

        [Fact]
        public async Task Update_QueuesOnlyTheChangedFields_AndMergesRepeatedEdits()
        {
            var trip = await NewTripAsync();
            var expense = new Expense { TripId = trip.Id, Amount = 10m };
            await _expenses.SaveAsync(expense);

            // Pretend the server already has the row: clear the queue.
            await (await _db.Connection).ExecuteAsync("DELETE FROM PendingChange");

            expense.Description = "Taksi";
            await _expenses.SaveAsync(expense);
            Assert.Equal("Description", (await PendingForAsync(expense))!.ChangedFields);

            expense.Amount = 12m;
            await _expenses.SaveAsync(expense);
            var merged = (await PendingForAsync(expense))!;
            // TP-11: an amount always travels with the currency it is in
            Assert.Equal(new[] { "Description", "AmountMinor", "AmountCurrencyCode" }, merged.ChangedFieldList);
            Assert.Single(await OutboxAsync());
        }

        [Fact]
        public async Task Update_WithoutChanges_QueuesNothing()
        {
            var trip = await NewTripAsync();
            await (await _db.Connection).ExecuteAsync("DELETE FROM PendingChange");

            await _trips.SaveAsync(trip);

            Assert.Empty(await OutboxAsync());
        }

        [Fact]
        public async Task Update_FromAFreshObject_KeepsTheRowsIdentity()
        {
            var trip = await NewTripAsync();
            var activity = new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Tur" };
            await _activities.SaveAsync(activity);
            var syncId = activity.SyncId;

            // What the activity form does for an edit: a new object carrying just the id.
            await _activities.SaveAsync(new TripActivity { Id = activity.Id, TripId = trip.Id, Date = trip.StartDate, Title = "Müze" });

            var stored = await _activities.GetByIdAsync(activity.Id);
            Assert.Equal(syncId, stored!.SyncId);
            Assert.Equal("Müze", stored.Title);
        }

        [Fact]
        public async Task Delete_MovesTheRowToTheTrash_AndQueuesADelete()
        {
            var trip = await NewTripAsync();
            var todo = new TodoItem { TripId = trip.Id, Title = "Pasaport" };
            await _todos.SaveAsync(todo);

            await _todos.DeleteAsync(todo);

            Assert.Empty(await _todos.GetByTripIdAsync(trip.Id));
            var pending = (await PendingForAsync(todo))!;
            Assert.Equal(ChangeOperation.Delete, pending.Operation); // replaces the pending upsert

            var entry = Assert.Single(await _trash.GetEntriesAsync(trip.SyncId));
            Assert.Equal("Todo", entry.EntityType);
            Assert.Equal("Pasaport", entry.Title);
        }

        [Fact]
        public async Task DeletingATrip_TrashesEverythingAsOneBatch_AndRestoreBringsItAllBack()
        {
            var trip = await NewTripAsync();
            var ali = await _split.SaveParticipantAsync(trip.Id, "Ali");
            var dinner = new Expense { TripId = trip.Id, Amount = 40m, Description = "Yemek" };
            await _expenses.SaveAsync(dinner);
            await _split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id });
            var place = new Place { TripId = trip.Id, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 };
            await _places.SaveAsync(place);
            await _activities.SaveAsync(new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Tur", PlaceId = place.Id });
            await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Pasaport" });
            var file = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(7, 8, 9), "bilet.pdf", "application/pdf");

            await _trips.DeleteAsync(trip);

            Assert.Null(await _trips.GetByIdAsync(trip.Id));
            Assert.False(File.Exists(file.FullPath));
            var entry = Assert.Single(await _trash.GetEntriesAsync());
            Assert.Equal("Trip", entry.EntityType);
            Assert.Equal("Roma", entry.Title);
            Assert.Equal(8, entry.ItemCount); // trip, participant, expense, share, place, activity, todo, attachment

            var restored = await _trash.RestoreAsync(entry.BatchId);

            Assert.Equal(8, restored);
            var back = await _trips.GetByIdAsync(trip.Id);
            Assert.Equal(trip.SyncId, back!.SyncId);
            Assert.Equal((1, 1, 1, 1), await _trips.GetRelatedCountsAsync(trip.Id));
            Assert.Equal(new[] { ali.Id }, await _split.GetShareParticipantIdsAsync(dinner.Id));
            Assert.Equal(place.Id, Assert.Single(await _activities.GetByTripIdAsync(trip.Id)).PlaceId);
            var attachment = Assert.Single(await _attachments.GetByTripIdAsync(trip.Id));
            Assert.Equal(TestFiles.PdfBytes(7, 8, 9), await File.ReadAllBytesAsync(attachment.FullPath));
            Assert.Empty(await _trash.GetEntriesAsync());

            // Every restored row is queued as a full upsert, parents first.
            var outbox = await OutboxAsync();
            Assert.All(outbox, p => Assert.Equal(ChangeOperation.Upsert, p.Operation));
            Assert.Equal("Trip", outbox.First(p => p.TripSyncId == trip.SyncId).EntityType);
        }

        [Fact]
        public async Task RestoringAChild_WhoseTripIsGone_IsSkipped()
        {
            var trip = await NewTripAsync();
            var todo = new TodoItem { TripId = trip.Id, Title = "Pasaport" };
            await _todos.SaveAsync(todo);
            await _todos.DeleteAsync(todo);
            var todoBatch = Assert.Single(await _trash.GetEntriesAsync()).BatchId;

            await _trips.DeleteAsync(trip);

            Assert.Equal(0, await _trash.RestoreAsync(todoBatch));
            Assert.Empty(await _todos.GetByTripIdAsync(trip.Id));
        }

        [Fact]
        public async Task DeletingAPlace_UnlinksItsActivities_AndQueuesThatChange()
        {
            var trip = await NewTripAsync();
            var place = new Place { TripId = trip.Id, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 };
            await _places.SaveAsync(place);
            var activity = new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Tur", PlaceId = place.Id };
            await _activities.SaveAsync(activity);
            await (await _db.Connection).ExecuteAsync("DELETE FROM PendingChange");

            await _places.DeleteAsync(place);

            Assert.Null((await _activities.GetByIdAsync(activity.Id))!.PlaceId);
            Assert.Equal("PlaceId", (await PendingForAsync(activity))!.ChangedFields);
            Assert.Equal(ChangeOperation.Delete, (await PendingForAsync(place))!.Operation);
        }

        [Fact]
        public async Task ChangingASplit_WritesOnlyTheDifference()
        {
            var trip = await NewTripAsync();
            var ali = await _split.SaveParticipantAsync(trip.Id, "Ali");
            var can = await _split.SaveParticipantAsync(trip.Id, "Can");
            var dinner = new Expense { TripId = trip.Id, Amount = 40m };
            await _expenses.SaveAsync(dinner);
            await _split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id });
            var db = await _db.Connection;
            var aliId = ali.Id;
            var aliShare = await db.Table<ExpenseShare>().Where(s => s.ParticipantId == aliId).FirstAsync();
            await db.ExecuteAsync("DELETE FROM PendingChange");

            await _split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id, can.Id });

            // Ali's share is untouched; only Can's is new.
            var dinnerId = dinner.Id;
            var shares = await db.Table<ExpenseShare>().Where(s => s.ExpenseId == dinnerId).ToListAsync();
            Assert.Contains(shares, s => s.Id == aliShare.Id && s.SyncId == aliShare.SyncId);
            var pending = Assert.Single(await OutboxAsync());
            Assert.Equal("ExpenseShare", pending.EntityType);
            Assert.Null(pending.ChangedFields);
        }

        [Fact]
        public async Task Purge_RemovesOnlyItemsOlderThanTheRetention()
        {
            var trip = await NewTripAsync();
            var file = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(1), "a.pdf", "application/pdf");
            await _attachments.DeleteAsync(file);
            var trashed = _attachments.GetTrashPath(file);
            Assert.True(File.Exists(trashed));

            Assert.Equal(0, await _trash.PurgeExpiredAsync());

            _trash.UtcNow = () => DateTime.UtcNow.AddDays(TrashService.RetentionDays + 1);
            Assert.Equal(1, await _trash.PurgeExpiredAsync());
            Assert.Empty(await _trash.GetEntriesAsync());
            Assert.False(File.Exists(trashed));
        }

        private async Task<string> BatchOfAsync(ISyncEntity entity)
        {
            var syncId = entity.SyncId;
            return (await (await _db.Connection).Table<TrashItem>().Where(t => t.EntitySyncId == syncId).FirstAsync()).BatchId;
        }

        [Fact]
        public async Task DeleteForever_RemovesOnlyTheChosenBatches_AndTheirFiles()
        {
            var trip = await NewTripAsync();
            var a = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(1), "a.pdf", "application/pdf");
            var b = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(2), "b.pdf", "application/pdf");
            await _attachments.DeleteAsync(a);
            await _attachments.DeleteAsync(b);
            var batchA = await BatchOfAsync(a);

            Assert.Equal(1, await _trash.DeleteForeverAsync(new[] { batchA }));

            var left = Assert.Single(await _trash.GetEntriesAsync());
            Assert.NotEqual(batchA, left.BatchId);
            Assert.False(File.Exists(_attachments.GetTrashPath(a)));
            Assert.True(File.Exists(_attachments.GetTrashPath(b)));
            Assert.Equal(0, await _trash.RestoreAsync(batchA)); // nothing left to restore
        }

        [Fact]
        public async Task DeleteForever_ATrip_RemovesEverythingDeletedWithIt()
        {
            var trip = await NewTripAsync();
            await _todos.SaveAsync(new TodoItem { TripId = trip.Id, Title = "Pasaport" });
            await _trips.DeleteAsync(trip);
            var entry = Assert.Single(await _trash.GetEntriesAsync());
            Assert.True(entry.ItemCount >= 2);
            Assert.NotEqual(0, entry.RootItemId);

            Assert.Equal(entry.ItemCount, await _trash.DeleteForeverAsync(new[] { entry.BatchId }));

            Assert.Empty(await _trash.GetEntriesAsync());
            Assert.Equal(0, await (await _db.Connection).Table<TrashItem>().CountAsync());
        }

        [Fact]
        public async Task DeleteForever_SignedIn_AlsoRemovesTheCloudCopyOfFiles()
        {
            var cloud = new Mock<ISupabaseClient>();
            var auth = new Mock<IAuthService>();
            auth.SetupGet(x => x.IsSignedIn).Returns(true);
            var trash = new TrashService(_db.Service, _attachments, cloud.Object, auth.Object);
            var trip = await NewTripAsync();
            var file = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(1), "bilet.PDF", "application/pdf");
            await _attachments.DeleteAsync(file);

            await trash.DeleteForeverAsync(new[] { await BatchOfAsync(file) });

            var expected = $"trips/{trip.SyncId}/{file.SyncId}.pdf";
            cloud.Verify(c => c.DeleteAsync(SyncService.Bucket,
                It.Is<IReadOnlyCollection<string>>(p => p.Count == 1 && p.Contains(expected)), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task DeleteForever_AsGuest_OrOffline_StillDeletesLocally()
        {
            var cloud = new Mock<ISupabaseClient>();
            cloud.Setup(c => c.DeleteAsync(It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
                 .ThrowsAsync(new HttpRequestException("offline"));
            var auth = new Mock<IAuthService>();
            auth.SetupGet(x => x.IsSignedIn).Returns(true);
            var trash = new TrashService(_db.Service, _attachments, cloud.Object, auth.Object);
            var trip = await NewTripAsync();
            var file = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(1), "a.pdf", "application/pdf");
            await _attachments.DeleteAsync(file);

            Assert.Equal(1, await trash.DeleteForeverAsync(new[] { await BatchOfAsync(file) }));
            Assert.Empty(await trash.GetEntriesAsync());

            // Guest: the cloud is never asked.
            var guestCloud = new Mock<ISupabaseClient>();
            var guestAuth = new Mock<IAuthService>();
            var guestTrash = new TrashService(_db.Service, _attachments, guestCloud.Object, guestAuth.Object);
            var other = await _attachments.AddAsync(trip.Id, TestFiles.Pdf(1), "b.pdf", "application/pdf");
            await _attachments.DeleteAsync(other);
            await guestTrash.DeleteForeverAsync(new[] { await BatchOfAsync(other) });
            guestCloud.VerifyNoOtherCalls();
        }

        [Fact]
        public void MergeFields_UnionsNames_AndAllFieldsWins()
        {
            Assert.Equal("A,B,C", ChangeTracker.MergeFields("A,B", new[] { "B", "C" }));
            Assert.Null(ChangeTracker.MergeFields(null, new[] { "A" }));
            Assert.Null(ChangeTracker.MergeFields("A", null));
        }

        [Fact]
        public void EverySyncedModel_IsInTheSchema_ParentsFirst()
        {
            var synced = typeof(Trip).Assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && typeof(ISyncEntity).IsAssignableFrom(t))
                .ToHashSet();

            Assert.Equal(synced, SyncSchema.Entities.Select(e => e.ClrType).ToHashSet());

            foreach (var entity in SyncSchema.Entities)
                foreach (var key in entity.ForeignKeys)
                    Assert.True(SyncSchema.For(key.Target).Rank < entity.Rank, $"{entity.EntityType}.{key.PropertyName}");
        }
    }

    /// <summary>G1 migration: an existing (pre-sync) database gets SyncIds and a complete outbox.</summary>
    public class SyncMigrationTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task Migration3_GivesExistingRowsASyncId_AndQueuesThemParentsFirst()
        {
            using (var legacy = new SQLiteConnection(_db.Path))
            {
                legacy.Execute("CREATE TABLE Trip (Id integer primary key autoincrement not null, Name varchar, Country varchar, City varchar, StartDate bigint, EndDate bigint, PlannedBudgetMinor bigint, Description varchar, CurrencyCode varchar)");
                legacy.Execute("CREATE TABLE Expense (Id integer primary key autoincrement not null, TripId integer, Category integer, AmountMinor bigint, OriginalAmountMinor bigint, OriginalCurrencyCode varchar, ExchangeRate float, Date bigint, PaidByParticipantId integer, Description varchar)");
                legacy.Execute("INSERT INTO Trip (Name, Country, City, StartDate, EndDate, PlannedBudgetMinor, CurrencyCode) VALUES ('Roma', 'İtalya', 'Roma', ?, ?, 0, 'EUR')",
                    new DateTime(2026, 8, 20).Ticks, new DateTime(2026, 8, 27).Ticks);
                legacy.Execute("INSERT INTO Expense (TripId, Category, AmountMinor, OriginalAmountMinor, OriginalCurrencyCode, ExchangeRate, Date) VALUES (1, 2, 1234, 1234, 'EUR', 1, ?)",
                    new DateTime(2026, 8, 21).Ticks);
                legacy.Execute("PRAGMA user_version = 2");
            }

            var connection = await _db.Connection;

            var trip = await connection.Table<Trip>().FirstAsync();
            var expense = await connection.Table<Expense>().FirstAsync();
            Assert.True(Guid.TryParse(trip.SyncId, out _));
            Assert.True(Guid.TryParse(expense.SyncId, out _));

            var outbox = await connection.Table<PendingChange>().OrderBy(p => p.Sequence).ToListAsync();
            Assert.Equal(new[] { "Trip", "Expense" }, outbox.Select(p => p.EntityType));
            Assert.All(outbox, p => Assert.Equal(trip.SyncId, p.TripSyncId));
            Assert.Equal(DatabaseService.LatestSchemaVersion, await connection.ExecuteScalarAsync<int>("PRAGMA user_version"));
        }
    }
}
