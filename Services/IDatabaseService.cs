using SQLite;

namespace TravelPlanner.Services
{
    /// <summary>
    /// Lazily opens (and migrates) the app's SQLite connection.
    /// Implemented by DatabaseService.
    /// </summary>
    public interface IDatabaseService
    {
        /// <summary>The shared connection, opened and migrated on first access.</summary>
        Task<SQLiteAsyncConnection> Connection { get; }

        /// <summary>Forces the connection/tables to be created eagerly instead of on first use.</summary>
        Task InitializeAsync();

        /// <summary>Full path of the database file currently in use.</summary>
        string DatabasePath { get; }

        /// <summary>Closes the connection; the next access opens (and migrates) the file again.</summary>
        Task CloseAsync();

        /// <summary>Closes the current database and opens <paramref name="databasePath"/> instead (G3: guest ↔ account).</summary>
        Task SwitchDatabaseAsync(string databasePath);
    }
}
