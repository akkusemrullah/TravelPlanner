using Moq;
using Xunit;
using SQLite;
using TravelPlanner.Services;

namespace TravelPlanner.Tests
{
    /// <summary>
    /// A real SQLite database in a throw-away temp file, so services are tested against the same
    /// sqlite-net behaviour the app uses (SQL SUMs, transactions, migrations) instead of a mock.
    /// </summary>
    public sealed class TestDatabase : IAsyncLifetime
    {
        public TestDatabase()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"travelplanner-test-{Guid.NewGuid():N}.db3");
            Service = new DatabaseService(Path);
        }

        public string Path { get; }

        public DatabaseService Service { get; private set; }

        public Task<SQLiteAsyncConnection> Connection => Service.Connection;

        /// <summary>Opens a second DatabaseService on the same file (simulates an app restart).</summary>
        public async Task<DatabaseService> ReopenAsync()
        {
            await (await Service.Connection).CloseAsync();
            Service = new DatabaseService(Path);
            return Service;
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            try
            {
                await (await Service.Connection).CloseAsync();
            }
            catch
            {
                // Connection may never have opened (or was already closed) - nothing to release.
            }

            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
