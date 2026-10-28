using SQLite;
using TravelPlanner.Models;

namespace TravelPlanner.Services
{
    public class DatabaseService : IDatabaseService
    {
        /// <summary>
        /// Ordered schema migrations (TP-205). The database's current version lives in SQLite's
        /// PRAGMA user_version; on startup every step with a higher version runs once, each in its own
        /// transaction, and bumps user_version. To change existing data: append a new step with the next
        /// version number - never edit or reorder a step that has already shipped.
        /// (Plain column additions need no step: CreateTableAsync adds new columns automatically.)
        /// </summary>
        internal static readonly IReadOnlyList<Migration> Migrations = new[]
        {
            new Migration(1, "Money: REAL columns -> integer minor units (kuruş/cent)", (db, _) =>
            {
                // Fresh installs never had the legacy REAL columns, so only copy when they exist.
                if (HasColumn(db, "Expense", "Amount"))
                    db.Execute("UPDATE Expense SET AmountMinor = CAST(ROUND(Amount * 100) AS INTEGER) WHERE Amount IS NOT NULL");

                if (HasColumn(db, "Trip", "PlannedBudget"))
                    db.Execute("UPDATE Trip SET PlannedBudgetMinor = CAST(ROUND(PlannedBudget * 100) AS INTEGER) WHERE PlannedBudget IS NOT NULL");
            }),

            new Migration(2, "Multi-currency: existing trips keep the currency they were planned in (TP-403)", (db, context) =>
            {
                // Before TP-403 every amount was shown in the user's default currency. Pin existing trips to it,
                // so changing the default later doesn't silently relabel their budgets and expenses.
                db.Execute("UPDATE Trip SET CurrencyCode = ? WHERE CurrencyCode IS NULL OR CurrencyCode = ''", context.DefaultCurrencyCode);

                // The new columns arrive as NULL on existing rows (CreateTableAsync just ALTERs them in).
                // Record that those expenses were typed in the trip currency at a 1:1 rate, so the edit form
                // and the "converted from" display treat them correctly.
                db.Execute("UPDATE Expense SET OriginalAmountMinor = AmountMinor WHERE OriginalCurrencyCode IS NULL");
                db.Execute("UPDATE Expense SET ExchangeRate = 1 WHERE ExchangeRate IS NULL OR ExchangeRate = 0");
            }),

            new Migration(3, "Cloud sync: every row gets a SyncId and is queued for its first upload (G1)", (db, _) =>
            {
                // CreateTableAsync has already added the sync columns (NULL on existing rows) and the outbox/trash
                // tables. Give every existing row its global identity and queue it, parents first, so the first
                // sync after signing in uploads the whole local database.
                Sync.ChangeTracker.EnqueueAllRows(db);
            }),
        };

        internal static int LatestSchemaVersion => Migrations.Count == 0 ? 0 : Migrations[^1].Version;

        private string _databasePath;
        private readonly MigrationContext _migrationContext;

        // Cache the in-flight/completed INITIALIZATION TASK itself, not just the connection, so every
        // concurrent caller awaits the same table-creation work. (Caching only the connection used to
        // let a second caller run a SELECT before CreateTableAsync had finished -> "no such table: Trip".)
        private Task<SQLiteAsyncConnection>? _initializationTask;

        /// <param name="databasePath">
        /// Full path of the SQLite file. The app passes AppConstants.DatabasePath (see MauiProgram);
        /// unit tests pass a temporary file.
        /// </param>
        /// <param name="defaultCurrencyCode">The user's default currency, used by migrations that need it (TP-403).</param>
        public DatabaseService(string databasePath, string defaultCurrencyCode = Helpers.MoneyFormatter.DefaultCurrencyCode)
        {
            _databasePath = databasePath;
            _migrationContext = new MigrationContext(Helpers.MoneyFormatter.IsSupported(defaultCurrencyCode)
                ? defaultCurrencyCode.ToUpperInvariant()
                : Helpers.MoneyFormatter.DefaultCurrencyCode);
        }

        public Task<SQLiteAsyncConnection> Connection => GetConnectionAsync();

        public string DatabasePath => _databasePath;

        public Task InitializeAsync() => GetConnectionAsync();

        public async Task CloseAsync()
        {
            var initialization = _initializationTask;
            _initializationTask = null;
            if (initialization is null)
                return;

            try
            {
                var connection = await initialization;
                await connection.CloseAsync();
            }
            catch (Exception ex)
            {
                // A connection that never opened has nothing to close.
                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Close: {ex.Message}");
            }
        }

        public async Task SwitchDatabaseAsync(string databasePath)
        {
            if (!string.Equals(_databasePath, databasePath, StringComparison.Ordinal))
            {
                await CloseAsync();
                _databasePath = databasePath;
            }

            await InitializeAsync();
        }

        private Task<SQLiteAsyncConnection> GetConnectionAsync()
        {
            return _initializationTask ??= InitializeConnectionAsync();
        }

        private async Task<SQLiteAsyncConnection> InitializeConnectionAsync()
        {
            try
            {
                var directory = Path.GetDirectoryName(_databasePath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory); // per-account databases live in their own folder (G3)

                var connection = new SQLiteAsyncConnection(_databasePath);

                // CreateTableAsync also adds any NEW columns (e.g. AmountMinor) to an existing table.
                await connection.CreateTableAsync<Trip>();
                await connection.CreateTableAsync<TodoItem>();
                await connection.CreateTableAsync<Expense>();
                await connection.CreateTableAsync<Place>();
                await connection.CreateTableAsync<TripActivity>();   // TP-401
                await connection.CreateTableAsync<PackingTemplate>(); // TP-405
                await connection.CreateTableAsync<TripAttachment>();  // TP-501
                await connection.CreateTableAsync<Participant>();     // TP-504
                await connection.CreateTableAsync<ExpenseShare>();    // TP-504
                await connection.CreateTableAsync<PendingChange>();   // G1 - offline outbox
                await connection.CreateTableAsync<TrashItem>();       // G1 - 30-day trash
                await connection.CreateTableAsync<SyncStateEntry>();  // G4 - pull cursor
                await connection.CreateTableAsync<TripMembership>();  // G4 - role per trip
                await connection.CreateTableAsync<KnownUser>();       // G6 - trip mates' names
                await connection.CreateTableAsync<AiPlanRecord>();    // TP-108 - AI plans kept on the phone

                await MigrateAsync(connection, _migrationContext);

                return connection;
            }
            catch
            {
                // Don't leave a faulted Task cached forever - the next caller gets a fresh attempt.
                _initializationTask = null;
                throw;
            }
        }

        private static async Task MigrateAsync(SQLiteAsyncConnection connection, MigrationContext context)
        {
            var version = await connection.ExecuteScalarAsync<int>("PRAGMA user_version");

            foreach (var migration in Migrations.Where(m => m.Version > version).OrderBy(m => m.Version))
            {
                await connection.RunInTransactionAsync(db =>
                {
                    migration.Apply(db, context);
                    db.Execute($"PRAGMA user_version = {migration.Version}");
                });

                System.Diagnostics.Debug.WriteLine($"[DatabaseService] Applied migration {migration.Version}: {migration.Description}");
            }
        }

        private static bool HasColumn(SQLiteConnection db, string table, string column) =>
            db.GetTableInfo(table).Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));

        internal sealed record Migration(int Version, string Description, Action<SQLiteConnection, MigrationContext> Apply);

        /// <summary>Values from outside the database that a migration may need.</summary>
        internal sealed record MigrationContext(string DefaultCurrencyCode);
    }
}
