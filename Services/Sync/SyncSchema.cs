using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SQLite;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    /// <summary>An integer relation of a synced row (e.g. Expense.TripId → Trip) that the sync engine maps to SyncIds.</summary>
    public sealed record SyncForeignKey(string PropertyName, Type Target, bool IsNullable);

    /// <summary>How one synced table behaves: its name, its place in parent-first order, its relations.</summary>
    public sealed class SyncEntityInfo
    {
        public SyncEntityInfo(
            Type clrType,
            int rank,
            Func<SQLiteConnection, ISyncEntity, string?> tripSyncId,
            Func<ISyncEntity, string?> title,
            params SyncForeignKey[] foreignKeys)
        {
            ClrType = clrType;
            EntityType = clrType.GetCustomAttribute<TableAttribute>()?.Name ?? clrType.Name;
            Rank = rank;
            ResolveTripSyncId = tripSyncId;
            ResolveTitle = title;
            ForeignKeys = foreignKeys;
        }

        public Type ClrType { get; }

        /// <summary>The SQLite table name ("Trip", "Todo", "Activity" …) - also the name the outbox and the server use.</summary>
        public string EntityType { get; }

        /// <summary>Parent-first order: a row's parents always have a lower rank.</summary>
        public int Rank { get; }

        public IReadOnlyList<SyncForeignKey> ForeignKeys { get; }

        /// <summary>SyncId of the trip the row belongs to (null for personal rows such as packing templates).</summary>
        public Func<SQLiteConnection, ISyncEntity, string?> ResolveTripSyncId { get; }

        /// <summary>Short text for the trash list.</summary>
        public Func<ISyncEntity, string?> ResolveTitle { get; }
    }

    /// <summary>
    /// The synced tables (G1), in parent-first order. Every model listed here derives from
    /// <see cref="SyncEntityBase"/> and is written only through <see cref="ChangeTracker"/>.
    /// </summary>
    public static class SyncSchema
    {
        public static IReadOnlyList<SyncEntityInfo> Entities { get; } = new[]
        {
            new SyncEntityInfo(typeof(Trip), 0,
                (_, e) => e.SyncId,
                e => ((Trip)e).Name),

            new SyncEntityInfo(typeof(Participant), 1,
                (c, e) => TripSyncIdOf(c, ((Participant)e).TripId),
                e => ((Participant)e).Name,
                new SyncForeignKey(nameof(Participant.TripId), typeof(Trip), false)),

            new SyncEntityInfo(typeof(Place), 2,
                (c, e) => TripSyncIdOf(c, ((Place)e).TripId),
                e => ((Place)e).Name,
                new SyncForeignKey(nameof(Place.TripId), typeof(Trip), false)),

            new SyncEntityInfo(typeof(Expense), 3,
                (c, e) => TripSyncIdOf(c, ((Expense)e).TripId),
                e => ((Expense)e).Description is { Length: > 0 } description ? description : ((Expense)e).Category.ToString(),
                new SyncForeignKey(nameof(Expense.TripId), typeof(Trip), false),
                new SyncForeignKey(nameof(Expense.PaidByParticipantId), typeof(Participant), true)),

            new SyncEntityInfo(typeof(ExpenseShare), 4,
                (c, e) => c.ExecuteScalar<string>(
                    "SELECT t.SyncId FROM Expense e INNER JOIN Trip t ON t.Id = e.TripId WHERE e.Id = ?", ((ExpenseShare)e).ExpenseId),
                _ => null,
                new SyncForeignKey(nameof(ExpenseShare.ExpenseId), typeof(Expense), false),
                new SyncForeignKey(nameof(ExpenseShare.ParticipantId), typeof(Participant), false)),

            new SyncEntityInfo(typeof(TodoItem), 5,
                (c, e) => TripSyncIdOf(c, ((TodoItem)e).TripId),
                e => ((TodoItem)e).Title,
                new SyncForeignKey(nameof(TodoItem.TripId), typeof(Trip), false)),

            new SyncEntityInfo(typeof(TripActivity), 6,
                (c, e) => TripSyncIdOf(c, ((TripActivity)e).TripId),
                e => ((TripActivity)e).Title,
                new SyncForeignKey(nameof(TripActivity.TripId), typeof(Trip), false),
                new SyncForeignKey(nameof(TripActivity.PlaceId), typeof(Place), true)),

            new SyncEntityInfo(typeof(TripAttachment), 7,
                (c, e) => TripSyncIdOf(c, ((TripAttachment)e).TripId),
                e => ((TripAttachment)e).DisplayName,
                new SyncForeignKey(nameof(TripAttachment.TripId), typeof(Trip), false)),

            new SyncEntityInfo(typeof(PackingTemplate), 8,
                (_, _) => null,
                e => ((PackingTemplate)e).Name),
        };

        private static readonly Dictionary<Type, SyncEntityInfo> ByType = Entities.ToDictionary(e => e.ClrType);
        private static readonly Dictionary<string, SyncEntityInfo> ByName = Entities.ToDictionary(e => e.EntityType, StringComparer.Ordinal);

        public static SyncEntityInfo For(Type type) =>
            ByType.TryGetValue(type, out var info)
                ? info
                : throw new InvalidOperationException($"{type.Name} is not a synced entity (add it to SyncSchema).");

        public static SyncEntityInfo For<T>() => For(typeof(T));

        public static SyncEntityInfo ForEntityType(string entityType) =>
            ByName.TryGetValue(entityType, out var info)
                ? info
                : throw new InvalidOperationException($"Unknown synced entity type '{entityType}'.");

        public static bool TryForEntityType(string entityType, [MaybeNullWhen(false)] out SyncEntityInfo info) =>
            ByName.TryGetValue(entityType, out info);

        private static readonly Dictionary<string, string> ServerTables = new(StringComparer.Ordinal)
        {
            ["Trip"] = "trips",
            ["Participant"] = "participants",
            ["Place"] = "places",
            ["Expense"] = "expenses",
            ["ExpenseShare"] = "expense_shares",
            ["Todo"] = "todos",
            ["Activity"] = "activities",
            ["Attachment"] = "attachments",
            ["PackingTemplate"] = "packing_templates",
        };

        /// <summary>The Supabase table of a synced entity (see supabase/migrations/…01_core_schema.sql).</summary>
        public static string ServerTable(SyncEntityInfo info) => ServerTables[info.EntityType];

        public static string? TripSyncIdOf(SQLiteConnection conn, int tripId) =>
            conn.ExecuteScalar<string>("SELECT SyncId FROM Trip WHERE Id = ?", tripId);
    }
}
