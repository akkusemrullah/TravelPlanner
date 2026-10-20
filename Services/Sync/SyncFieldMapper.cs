using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SQLite;
using TravelPlanner.Models;

namespace TravelPlanner.Services.Sync
{
    /// <summary>
    /// Converts a local row to the server's column/value shape and back (G4).
    /// - property names become snake_case columns (AmountMinor → amount_minor);
    /// - integer relations become the related row's SyncId (PaidByParticipantId → paid_by_participant_id = uuid);
    /// - TripId is never a field: it travels as the change's top-level trip_id;
    /// - bookkeeping (Id, SyncId, versions, who/when) and device-only data (an attachment's local path) stay local;
    /// - day values travel as yyyy-MM-dd, times as HH:mm:ss, other DateTimes as a timestamp without zone.
    /// </summary>
    public static class SyncFieldMapper
    {
        public enum FieldKind { Text, Bool, Int, Long, Double, Enum, Date, Timestamp, Time, ForeignKey }

        public sealed record Field(PropertyInfo Property, string Column, FieldKind Kind, SyncForeignKey? ForeignKey);

        private static readonly HashSet<string> LocalOnly = new(StringComparer.Ordinal)
        {
            nameof(ISyncEntity.Id), nameof(ISyncEntity.SyncId), nameof(ISyncEntity.CreatedBy), nameof(ISyncEntity.UpdatedBy),
            nameof(ISyncEntity.UpdatedAtUtc), nameof(ISyncEntity.Version), nameof(ISyncEntity.ChangeSeq),
            "TripId",
        };

        /// <summary>Device-specific columns per entity type.</summary>
        private static readonly Dictionary<Type, HashSet<string>> DeviceOnly = new()
        {
            [typeof(TripAttachment)] = new(StringComparer.Ordinal) { nameof(TripAttachment.RelativePath) },
            [typeof(Trip)] = new(StringComparer.Ordinal) { nameof(Trip.PendingCoverId) },
        };

        /// <summary>Properties whose server column isn't simply their snake_case name.</summary>
        private static readonly Dictionary<(Type, string), string> ColumnOverrides = new()
        {
            [(typeof(TripAttachment), nameof(TripAttachment.RemotePath))] = "storage_path",
        };

        /// <summary>DateTime properties that hold a calendar day (server type date).</summary>
        private static readonly HashSet<string> DayProperties = new(StringComparer.Ordinal) { "Date", "StartDate", "EndDate" };

        private static readonly Dictionary<Type, IReadOnlyList<Field>> Cache = new();

        public static IReadOnlyList<Field> FieldsFor(SyncEntityInfo info)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(info.ClrType, out var cached))
                    return cached;

                var mapping = new TableMapping(info.ClrType);
                var deviceOnly = DeviceOnly.TryGetValue(info.ClrType, out var set) ? set : null;
                var fields = new List<Field>();

                foreach (var column in mapping.Columns)
                {
                    var name = column.PropertyName;
                    if (column.IsPK || LocalOnly.Contains(name) || deviceOnly?.Contains(name) == true)
                        continue;

                    var property = info.ClrType.GetProperty(name)!;
                    var foreignKey = info.ForeignKeys.FirstOrDefault(k => k.PropertyName == name);
                    var columnName = ColumnOverrides.TryGetValue((info.ClrType, name), out var overridden) ? overridden : SnakeCase(name);
                    fields.Add(new Field(property, columnName, KindOf(property, foreignKey), foreignKey));
                }

                Cache[info.ClrType] = fields;
                return fields;
            }
        }

        private static FieldKind KindOf(PropertyInfo property, SyncForeignKey? foreignKey)
        {
            if (foreignKey is not null)
                return FieldKind.ForeignKey;

            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type == typeof(string)) return FieldKind.Text;
            if (type == typeof(bool)) return FieldKind.Bool;
            if (type.IsEnum) return FieldKind.Enum;
            if (type == typeof(int)) return FieldKind.Int;
            if (type == typeof(long)) return FieldKind.Long;
            if (type == typeof(double) || type == typeof(float)) return FieldKind.Double;
            if (type == typeof(TimeSpan)) return FieldKind.Time;
            if (type == typeof(DateTime)) return DayProperties.Contains(property.Name) ? FieldKind.Date : FieldKind.Timestamp;

            throw new NotSupportedException($"{property.DeclaringType?.Name}.{property.Name}: {type.Name} is not mapped for sync.");
        }

        public static string SnakeCase(string name)
        {
            var builder = new System.Text.StringBuilder(name.Length + 8);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c))
                {
                    if (i > 0)
                        builder.Append('_');
                    builder.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    builder.Append(c);
                }
            }

            return builder.ToString();
        }

        // ------------------------------------------------------------------ local → server

        /// <summary>
        /// The row as server fields. <paramref name="onlyProperties"/> limits it to the changed properties
        /// (null = all). Relations whose target no longer exists locally are sent as null.
        /// </summary>
        public static JsonObject ToFields(SQLiteConnection conn, SyncEntityInfo info, ISyncEntity entity, IReadOnlyCollection<string>? onlyProperties)
        {
            var result = new JsonObject();
            foreach (var field in FieldsFor(info))
            {
                if (onlyProperties is not null && !onlyProperties.Contains(field.Property.Name))
                    continue;

                result[field.Column] = ToJson(conn, field, field.Property.GetValue(entity));
            }

            return result;
        }

        private static JsonNode? ToJson(SQLiteConnection conn, Field field, object? value)
        {
            if (value is null)
                return null;

            switch (field.Kind)
            {
                case FieldKind.ForeignKey:
                    var targetTable = SyncSchema.For(field.ForeignKey!.Target).EntityType;
                    var syncId = conn.ExecuteScalar<string>($"SELECT SyncId FROM \"{targetTable}\" WHERE Id = ?", value);
                    return string.IsNullOrEmpty(syncId) ? null : JsonValue.Create(syncId);
                case FieldKind.Text:
                    return JsonValue.Create((string)value);
                case FieldKind.Bool:
                    return JsonValue.Create((bool)value);
                case FieldKind.Enum:
                    return JsonValue.Create(Convert.ToInt32(value, CultureInfo.InvariantCulture));
                case FieldKind.Int:
                    return JsonValue.Create((int)value);
                case FieldKind.Long:
                    return JsonValue.Create((long)value);
                case FieldKind.Double:
                    return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                case FieldKind.Date:
                    return JsonValue.Create(((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                case FieldKind.Timestamp:
                    return JsonValue.Create(((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));
                case FieldKind.Time:
                    var time = (TimeSpan)value;
                    return JsonValue.Create(new TimeSpan(time.Hours, time.Minutes, time.Seconds).ToString("c", CultureInfo.InvariantCulture));
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ server → local

        /// <summary>
        /// Copies a server row onto <paramref name="target"/>. Properties for which <paramref name="keepLocal"/> answers
        /// true (edits not uploaded yet) are left alone. Sets TripId from trip_id. Returns false when a required
        /// relation (trip, expense, participant) doesn't exist locally - the row can't be stored yet.
        /// </summary>
        public static bool ApplyRow(SQLiteConnection conn, SyncEntityInfo info, JsonElement row, ISyncEntity target, Func<string, bool> keepLocal)
        {
            // Scope first: a trip-scoped row needs its trip.
            var tripProperty = info.ClrType.GetProperty("TripId");
            if (tripProperty is not null)
            {
                var tripId = LocalIdOf(conn, "Trip", GetString(row, "trip_id"));
                if (tripId is null)
                    return false;
                if (!keepLocal("TripId"))
                    tripProperty.SetValue(target, tripId.Value);
            }

            foreach (var field in FieldsFor(info))
            {
                if (keepLocal(field.Property.Name) || !row.TryGetProperty(field.Column, out var value))
                    continue;

                if (field.Kind == FieldKind.ForeignKey)
                {
                    var targetTable = SyncSchema.For(field.ForeignKey!.Target).EntityType;
                    var localId = value.ValueKind == JsonValueKind.String ? LocalIdOf(conn, targetTable, value.GetString()) : null;
                    if (localId is null && !field.ForeignKey.IsNullable)
                        return false;
                    field.Property.SetValue(target, localId);
                    continue;
                }

                field.Property.SetValue(target, FromJson(field, value));
            }

            return true;
        }

        /// <summary>Sets the bookkeeping columns from a server row (version, change_seq, who/when).</summary>
        public static void ApplyMetadata(JsonElement row, ISyncEntity target)
        {
            target.SyncId = GetString(row, "id") ?? target.SyncId;
            if (row.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number)
                target.Version = version.GetInt64();
            if (row.TryGetProperty("change_seq", out var seq) && seq.ValueKind == JsonValueKind.Number)
                target.ChangeSeq = seq.GetInt64();
            target.CreatedBy = GetString(row, "created_by") ?? target.CreatedBy;
            target.UpdatedBy = GetString(row, "updated_by") ?? target.UpdatedBy;
            if (GetString(row, "updated_at") is { } updatedAt &&
                DateTimeOffset.TryParse(updatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                target.UpdatedAtUtc = parsed.UtcDateTime;
        }

        private static object? FromJson(Field field, JsonElement value)
        {
            var type = Nullable.GetUnderlyingType(field.Property.PropertyType) ?? field.Property.PropertyType;
            var nullable = !field.Property.PropertyType.IsValueType || Nullable.GetUnderlyingType(field.Property.PropertyType) is not null;

            if (value.ValueKind == JsonValueKind.Null)
                return nullable ? null : Activator.CreateInstance(type);

            switch (field.Kind)
            {
                case FieldKind.Text:
                    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                case FieldKind.Bool:
                    return value.ValueKind == JsonValueKind.True;
                case FieldKind.Enum:
                    return Enum.ToObject(type, value.GetInt32());
                case FieldKind.Int:
                    return value.GetInt32();
                case FieldKind.Long:
                    return value.GetInt64();
                case FieldKind.Double:
                    return value.GetDouble();
                case FieldKind.Date:
                    return DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                case FieldKind.Timestamp:
                    var text = value.GetString()!;
                    return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withZone) && HasZone(text)
                        ? withZone.LocalDateTime
                        : DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None);
                case FieldKind.Time:
                    return TimeSpan.Parse(value.GetString()!, CultureInfo.InvariantCulture);
                default:
                    return null;
            }
        }

        private static bool HasZone(string text) =>
            text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d\d:?\d\d$");

        public static int? LocalIdOf(SQLiteConnection conn, string table, string? syncId)
        {
            if (string.IsNullOrEmpty(syncId))
                return null;
            var id = conn.ExecuteScalar<long>($"SELECT Id FROM \"{table}\" WHERE SyncId = ?", syncId);
            return id > 0 ? (int)id : null;
        }

        public static string? GetString(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
