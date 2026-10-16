using System.IO.Compression;
using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class SplitCalculatorTests
    {
        private static Participant P(int id, string name) => new() { Id = id, TripId = 1, Name = name };

        [Theory]
        [InlineData(1000, 3, new long[] { 334, 333, 333 })]
        [InlineData(1001, 2, new long[] { 501, 500 })]
        [InlineData(900, 3, new long[] { 300, 300, 300 })]
        [InlineData(5, 0, new long[0])]
        public void EqualShares_AlwaysAddUpToTheTotal(long total, int count, long[] expected)
        {
            Assert.Equal(expected, SplitCalculator.EqualShares(total, count));
        }

        [Fact]
        public void Summarize_ComputesBalances_AndTheFewestTransfers()
        {
            var ali = P(1, "Ali"); var ayse = P(2, "Ayşe"); var can = P(3, "Can");
            var expenses = new List<Expense>
            {
                new() { Id = 10, TripId = 1, AmountMinor = 30000, PaidByParticipantId = 1 }, // Ali pays 300 for all three
                new() { Id = 11, TripId = 1, AmountMinor = 6000, PaidByParticipantId = 2 },  // Ayşe pays 60 for Ayşe + Can
                new() { Id = 12, TripId = 1, AmountMinor = 999 },                             // not split (no payer)
            };
            var shares = new List<ExpenseShare>
            {
                new() { ExpenseId = 10, ParticipantId = 1 }, new() { ExpenseId = 10, ParticipantId = 2 }, new() { ExpenseId = 10, ParticipantId = 3 },
                new() { ExpenseId = 11, ParticipantId = 2 }, new() { ExpenseId = 11, ParticipantId = 3 },
            };

            var summary = SplitCalculator.Summarize(new[] { ali, ayse, can }, expenses, shares);

            Assert.Equal(36000, summary.SplitTotalMinor);
            Assert.Equal(1, summary.UnsplitExpenseCount);
            Assert.Equal(20000, summary.Balances.Single(b => b.Participant.Id == 1).NetMinor);  // paid 300, share 100
            Assert.Equal(-7000, summary.Balances.Single(b => b.Participant.Id == 2).NetMinor); // paid 60, share 130
            Assert.Equal(-13000, summary.Balances.Single(b => b.Participant.Id == 3).NetMinor); // share 130
            Assert.Equal(0, summary.Balances.Sum(b => b.NetMinor));

            Assert.Equal(2, summary.Settlements.Count);
            Assert.Contains(summary.Settlements, s => s.From.Id == 3 && s.To.Id == 1 && s.AmountMinor == 13000);
            Assert.Contains(summary.Settlements, s => s.From.Id == 2 && s.To.Id == 1 && s.AmountMinor == 7000);
        }

        [Fact]
        public void Settle_NeedsAtMostNMinusOneTransfers_AndSquaresEveryone()
        {
            var people = Enumerable.Range(1, 5).Select(i => P(i, "P" + i)).ToList();
            var balances = new List<ParticipantBalance>
            {
                new(people[0], 10000, 0), new(people[1], 0, 2500), new(people[2], 3000, 4000),
                new(people[3], 0, 5000), new(people[4], 1000, 2500),
            };

            var settlements = SplitCalculator.Settle(balances);

            Assert.True(settlements.Count <= 4);
            var net = balances.ToDictionary(b => b.Participant.Id, b => b.NetMinor);
            foreach (var s in settlements)
            {
                net[s.From.Id] += s.AmountMinor;
                net[s.To.Id] -= s.AmountMinor;
            }
            Assert.All(net.Values, v => Assert.Equal(0, v));
        }
    }

    public class SplitServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private SplitService _split = null!;
        private ExpenseService _expenses = null!;
        private TripService _trips = null!;
        private Trip _trip = null!;

        public async Task InitializeAsync()
        {
            _split = new SplitService(_db.Service);
            _expenses = new ExpenseService(_db.Service);
            _trips = new TripService(_db.Service, _expenses, new Mock<INotificationService>().Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            _trip = new Trip { Name = "Roma", City = "Roma", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22) };
            await _trips.SaveAsync(_trip);
        }

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task Participants_AreUniquePerTrip()
        {
            await _split.SaveParticipantAsync(_trip.Id, " Ali ");
            await Assert.ThrowsAsync<DuplicateParticipantException>(() => _split.SaveParticipantAsync(_trip.Id, "ali"));

            var all = await _split.GetParticipantsAsync(_trip.Id);
            Assert.Equal("Ali", Assert.Single(all).Name);
        }

        [Fact]
        public async Task SaveSplit_ThenSummary_ThenDeletingAParticipant_CleansUp()
        {
            var ali = await _split.SaveParticipantAsync(_trip.Id, "Ali");
            var can = await _split.SaveParticipantAsync(_trip.Id, "Can");
            var dinner = new Expense { TripId = _trip.Id, Amount = 100m };
            await _expenses.SaveAsync(dinner);

            await _split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id, can.Id });

            var summary = await _split.GetSummaryAsync(_trip.Id);
            var settlement = Assert.Single(summary.Settlements);
            Assert.Equal("Can", settlement.From.Name);
            Assert.Equal(5000, settlement.AmountMinor);
            Assert.Equal(1, await _split.GetUsageCountAsync(ali.Id)); // paid and shares the same expense - counted once
            Assert.Equal(1, await _split.GetUsageCountAsync(can.Id));

            await _split.DeleteParticipantAsync(ali);

            Assert.Equal(new[] { can.Id }, await _split.GetShareParticipantIdsAsync(dinner.Id));
            Assert.Null((await _expenses.GetByIdAsync(dinner.Id))!.PaidByParticipantId);
        }

        [Fact]
        public async Task DeletingAnExpense_OrTheTrip_RemovesSplitRows()
        {
            var ali = await _split.SaveParticipantAsync(_trip.Id, "Ali");
            var taxi = new Expense { TripId = _trip.Id, Amount = 20m };
            await _expenses.SaveAsync(taxi);
            await _split.SaveSplitAsync(taxi, ali.Id, new[] { ali.Id });

            await _expenses.DeleteAsync(taxi);
            Assert.Empty(await _split.GetShareParticipantIdsAsync(taxi.Id));

            await _trips.DeleteAsync(_trip);
            Assert.Empty(await _split.GetParticipantsAsync(_trip.Id));
        }
    }

    public class AttachmentServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-att-" + Guid.NewGuid().ToString("N"));
        private AttachmentService _service = null!;

        public Task InitializeAsync()
        {
            _service = new AttachmentService(_db.Service, _root);
            return Task.CompletedTask;
        }

        public async Task DisposeAsync()
        {
            await _db.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static MemoryStream Bytes(int count) => new(Enumerable.Range(0, count).Select(i => (byte)i).ToArray());

        [Theory]
        [InlineData("bilet.pdf", "application/pdf", AttachmentKind.Document)]
        [InlineData("IMG_001.JPG", null, AttachmentKind.Photo)]
        [InlineData("scan", "image/heic", AttachmentKind.Photo)]
        public void DetectsTheKind(string name, string? type, AttachmentKind expected)
        {
            Assert.Equal(expected, AttachmentService.DetectKind(name, type));
        }

        [Fact]
        public async Task Add_CopiesTheFileIntoAppStorage_AndDeleteRemovesIt()
        {
            var attachment = await _service.AddAsync(3, Bytes(2048), "Uçak bileti.pdf", "application/pdf");

            Assert.True(File.Exists(attachment.FullPath));
            Assert.StartsWith(Path.Combine(_root, "attachments", "3"), attachment.FullPath);
            Assert.Equal(2048, attachment.SizeBytes);
            Assert.Equal("Uçak bileti.pdf", attachment.DisplayName);
            Assert.Equal("PDF", attachment.Extension);

            var listed = Assert.Single(await _service.GetByTripIdAsync(3));
            Assert.Equal(attachment.FullPath, listed.FullPath);

            await _service.DeleteAsync(listed);
            Assert.False(File.Exists(attachment.FullPath));
            Assert.Empty(await _service.GetByTripIdAsync(3));
        }

        [Fact]
        public async Task DeleteFilesForTrip_RemovesOnlyThatTripsFolder()
        {
            var a = await _service.AddAsync(1, Bytes(10), "a.jpg", "image/jpeg");
            var b = await _service.AddAsync(2, Bytes(10), "b.jpg", "image/jpeg");

            _service.DeleteFilesForTrip(1);

            Assert.False(File.Exists(a.FullPath));
            Assert.True(File.Exists(b.FullPath));
        }

        [Fact]
        public void SizeDisplay_IsHumanReadable()
        {
            Assert.Equal("820 B", TripAttachment.FormatSize(820));
            Assert.Equal("2 KB", TripAttachment.FormatSize(2048));
            Assert.Equal("1,5 MB", TripAttachment.FormatSize(1572864));
        }
    }

    public class BackupServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _source = new();
        private readonly TestDatabase _target = new();
        private readonly string _root = Path.Combine(Path.GetTempPath(), "tp-bak-" + Guid.NewGuid().ToString("N"));

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            await _source.DisposeAsync();
            await _target.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Fact]
        public async Task ExportThenImport_RestoresDataRelationsAndFiles()
        {
            // Source device
            var srcAttachments = new AttachmentService(_source.Service, Path.Combine(_root, "src"));
            var expenses = new ExpenseService(_source.Service);
            var trips = new TripService(_source.Service, expenses, new Mock<INotificationService>().Object, srcAttachments);
            var split = new SplitService(_source.Service);

            var unused = new Trip { Name = "Silinecek", City = "X", StartDate = DateTime.Today, EndDate = DateTime.Today };
            await trips.SaveAsync(unused);
            await trips.DeleteAsync(unused); // ids no longer start at 1 - restore must keep them anyway

            var trip = new Trip { Name = "Roma", City = "Roma", CurrencyCode = "EUR", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22), PlannedBudget = 500m };
            await trips.SaveAsync(trip);
            var ali = await split.SaveParticipantAsync(trip.Id, "Ali");
            var dinner = new Expense { TripId = trip.Id, Amount = 42.5m, Description = "Akşam yemeği" };
            await expenses.SaveAsync(dinner);
            await split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id });
            await new ActivityService(_source.Service).SaveAsync(new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Kolezyum" });
            await srcAttachments.AddAsync(trip.Id, new MemoryStream(new byte[] { 1, 2, 3 }), "bilet.pdf", "application/pdf");

            var zipPath = await new BackupService(_source.Service, srcAttachments, Path.Combine(_root, "out")).ExportAsync();

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                Assert.NotNull(zip.GetEntry(BackupService.DataEntryName));
                Assert.Contains(zip.Entries, e => e.FullName.StartsWith("attachments/"));
            }

            // Target device - already has some data that must be replaced
            var dstAttachments = new AttachmentService(_target.Service, Path.Combine(_root, "dst"));
            var dstTrips = new TripService(_target.Service, new ExpenseService(_target.Service), new Mock<INotificationService>().Object, dstAttachments);
            await dstTrips.SaveAsync(new Trip { Name = "Eski", City = "Y", StartDate = DateTime.Today, EndDate = DateTime.Today });

            await using var stream = File.OpenRead(zipPath);
            var result = await new BackupService(_target.Service, dstAttachments, Path.Combine(_root, "out2")).ImportAsync(stream, "backup.zip");

            Assert.Equal(1, result.Trips);
            Assert.Equal(1, result.Attachments);

            var restoredTrip = Assert.Single(await dstTrips.GetAllAsync());
            Assert.Equal(trip.Id, restoredTrip.Id);
            Assert.Equal("EUR", restoredTrip.CurrencyCode);
            Assert.Equal(500m, restoredTrip.PlannedBudget);

            var restoredExpense = Assert.Single(await new ExpenseService(_target.Service).GetByTripIdAsync(trip.Id));
            Assert.Equal(42.5m, restoredExpense.Amount);
            Assert.Equal(ali.Id, restoredExpense.PaidByParticipantId);

            var summary = await new SplitService(_target.Service).GetSummaryAsync(trip.Id);
            Assert.Equal(4250, summary.SplitTotalMinor);

            var file = Assert.Single(await dstAttachments.GetByTripIdAsync(trip.Id));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file.FullPath));
        }

        [Fact]
        public async Task NonBackupFiles_AreRejected_AndNothingChanges()
        {
            var attachments = new AttachmentService(_target.Service, Path.Combine(_root, "dst"));
            var trips = new TripService(_target.Service, new ExpenseService(_target.Service), new Mock<INotificationService>().Object, attachments);
            await trips.SaveAsync(new Trip { Name = "Kalacak", City = "Y", StartDate = DateTime.Today, EndDate = DateTime.Today });
            var service = new BackupService(_target.Service, attachments, _root);

            await Assert.ThrowsAsync<InvalidBackupException>(() => service.ImportAsync(new MemoryStream("{\"hello\":1}"u8.ToArray()), "x.json"));
            await Assert.ThrowsAsync<InvalidBackupException>(() => service.ImportAsync(new MemoryStream("not json"u8.ToArray()), "x.txt"));

            Assert.Equal("Kalacak", Assert.Single(await trips.GetAllAsync()).Name);
        }

        [Fact]
        public void Json_ContainsOnlyStoredColumns()
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new Expense { Amount = 1m, TripCurrencyCode = "EUR" }, BackupService.JsonOptions);

            Assert.Contains("AmountMinor", json);
            Assert.DoesNotContain("\"Amount\"", json);           // [Ignore] computed view
            Assert.DoesNotContain("TripCurrencyCode", json);
            Assert.DoesNotContain("FormattedAmount", json);
        }
    }

    public class TripReportTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task TextSummary_ContainsPlanBudgetAndWhoOwesWhom()
        {
            var expenses = new ExpenseService(_db.Service);
            var trips = new TripService(_db.Service, expenses, new Mock<INotificationService>().Object, new AttachmentService(_db.Service, Path.GetTempPath()));
            var places = new PlaceService(_db.Service);
            var activities = new ActivityService(_db.Service);
            var split = new SplitService(_db.Service);

            var trip = new Trip { Name = "Roma Tatili", City = "Roma", Country = "İtalya", CurrencyCode = "TRY", StartDate = new DateTime(2026, 8, 20), EndDate = new DateTime(2026, 8, 22), PlannedBudget = 1000m };
            await trips.SaveAsync(trip);
            var colosseum = new Place { TripId = trip.Id, Name = "Kolezyum", Latitude = 41.89, Longitude = 12.49 };
            await places.SaveAsync(colosseum);
            await activities.SaveAsync(new TripActivity { TripId = trip.Id, Date = trip.StartDate, Title = "Tur", StartTime = new TimeSpan(9, 0, 0), PlaceId = colosseum.Id });
            var ali = await split.SaveParticipantAsync(trip.Id, "Ali");
            var can = await split.SaveParticipantAsync(trip.Id, "Can");
            var dinner = new Expense { TripId = trip.Id, Amount = 100m, Category = ExpenseCategory.Food };
            await expenses.SaveAsync(dinner);
            await split.SaveSplitAsync(dinner, ali.Id, new[] { ali.Id, can.Id });

            var service = new TripReportService(trips, expenses, activities, places, new TodoService(_db.Service), split, new Mock<ITripPdfRenderer>().Object);
            var report = await service.BuildAsync(trip.Id);
            var text = service.ToText(report!);

            Assert.Contains("Roma Tatili", text);
            Assert.Contains("1. Gün", text);
            Assert.Contains("09:00  Tur (Kolezyum)", text);
            Assert.Contains("Yemek", text);
            Assert.Contains("Can, Ali için", text);
            Assert.Contains("50,00", text);
        }
    }
}
