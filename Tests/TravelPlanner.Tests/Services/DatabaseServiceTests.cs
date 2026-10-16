using Moq;
using Xunit;
using SQLite;
using TravelPlanner.Models;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    public class DatabaseServiceTests : IAsyncLifetime
    {
        private readonly TestDatabase _db = new();

        public Task InitializeAsync() => _db.InitializeAsync();

        public Task DisposeAsync() => _db.DisposeAsync();

        [Fact]
        public async Task FreshDatabase_IsAtLatestSchemaVersion()
        {
            var connection = await _db.Connection;

            var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version");

            Assert.Equal(DatabaseService.LatestSchemaVersion, version);
        }

        [Fact]
        public async Task ConcurrentFirstAccess_AllCallersGetTheSameReadyConnection()
        {
            var connections = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _db.Service.Connection));

            Assert.All(connections, c => Assert.Same(connections[0], c));
            Assert.Empty(await connections[0].Table<Trip>().ToListAsync()); // table exists - no "no such table"
        }

        [Fact]
        public async Task Migration1_CopiesLegacyRealMoneyColumnsIntoMinorUnits()
        {
            CreateLegacyVersion0Database(_db.Path);

            var connection = await _db.Connection; // runs CreateTable + migrations

            var trip = await connection.Table<Trip>().FirstAsync();
            var expense = await connection.Table<Expense>().FirstAsync();

            Assert.Equal(1500.50m, trip.PlannedBudget);
            Assert.Equal(12.34m, expense.Amount);
            Assert.Equal(DatabaseService.LatestSchemaVersion, await connection.ExecuteScalarAsync<int>("PRAGMA user_version"));
        }

        [Fact]
        public async Task Migrations_RunOnlyOnce()
        {
            CreateLegacyVersion0Database(_db.Path);
            var connection = await _db.Connection;

            // Change the migrated value; a second start-up must NOT copy the legacy REAL value over it again.
            await connection.ExecuteAsync("UPDATE Expense SET AmountMinor = 999");

            var reopened = await (await _db.ReopenAsync()).Connection;
            var expense = await reopened.Table<Expense>().FirstAsync();

            Assert.Equal(9.99m, expense.Amount);
        }

        /// <summary>Recreates the schema the app shipped before TP-109 (money stored as REAL, user_version 0).</summary>
        private static void CreateLegacyVersion0Database(string path)
        {
            using var db = new SQLiteConnection(path);
            db.Execute("CREATE TABLE Trip (Id integer primary key autoincrement not null, Name varchar, Country varchar, City varchar, StartDate bigint, EndDate bigint, PlannedBudget float, Description varchar)");
            db.Execute("CREATE TABLE Expense (Id integer primary key autoincrement not null, TripId integer, Category integer, Amount float, Date bigint, Description varchar)");
            db.Execute("INSERT INTO Trip (Name, Country, City, StartDate, EndDate, PlannedBudget) VALUES ('Roma', 'İtalya', 'Roma', ?, ?, 1500.5)",
                new DateTime(2026, 8, 20).Ticks, new DateTime(2026, 8, 27).Ticks);
            db.Execute("INSERT INTO Expense (TripId, Category, Amount, Date) VALUES (1, 2, 12.34, ?)", new DateTime(2026, 8, 21).Ticks);
        }
    }
}
