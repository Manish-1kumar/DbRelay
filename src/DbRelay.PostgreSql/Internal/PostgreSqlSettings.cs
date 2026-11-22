using System.Globalization;
using DbRelay.Storage;

namespace DbRelay.PostgreSql.Internal;

/// <summary>
/// Validated, immutable provider settings and the SQL text derived from them. Identifiers are validated with
/// <see cref="SqlIdentifier"/> and always quoted; every value is a parameter.
/// </summary>
internal sealed class PostgreSqlSettings
{
    internal const string ScriptResourceName = "DbRelay.PostgreSql.Scripts.001_create_outbox.sql";
    private const int MaxIdentifierLength = 63;
    private const string ReservedTableName = "schema_version";

    /// <summary>Columns read into <see cref="OutboxMessageDetails"/>, in ordinal order (see <c>PostgreSqlOutboxStore.ReadDetails</c>).</summary>
    private static readonly string[] DetailColumns =
    [
        "id", "message_type", "destination", "payload", "content_type", "headers", "correlation_id", "causation_id", "status",
        "attempt_count", "created_at", "scheduled_at", "available_at", "locked_by", "last_attempt_at", "delivered_at",
        "dead_lettered_at", "last_error",
    ];

    private PostgreSqlSettings(string schema, string tableName, TimeSpan commandTimeout)
    {
        Schema = schema;
        TableName = tableName;
        CommandTimeoutSeconds = (int)Math.Ceiling(commandTimeout.TotalSeconds);
        ClaimIndexName = IndexName(tableName, "claim");
        CleanupIndexName = IndexName(tableName, "cleanup");
        DeadIndexName = IndexName(tableName, "dead");
        StatusConstraintName = BoundedName("ck_", tableName, "_status");

        var table = Quote(schema) + "." + Quote(tableName);
        Table = table;
        var columns = string.Join(", ", DetailColumns);
        var claimedColumns = string.Join(", ", DetailColumns.Select(c => "m." + c));

        CreateScript = LoadScript()
            .Replace("{{schema}}", schema, StringComparison.Ordinal)
            .Replace("{{table}}", tableName, StringComparison.Ordinal)
            .Replace("{{ck_status}}", StatusConstraintName, StringComparison.Ordinal)
            .Replace("{{ix_claim}}", ClaimIndexName, StringComparison.Ordinal)
            .Replace("{{ix_cleanup}}", CleanupIndexName, StringComparison.Ordinal)
            .Replace("{{ix_dead}}", DeadIndexName, StringComparison.Ordinal);

        // $9 (deliver_at) and $10 (delay) are both typed parameters; now() is the transaction start time, so both columns get
        // the same value.
        InsertSql =
            $"INSERT INTO {table} (id, message_type, destination, payload, content_type, headers, correlation_id, causation_id, " +
            "status, attempt_count, scheduled_at, available_at) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, 0, 0, " +
            "COALESCE($9, now() + COALESCE($10, interval '0')), COALESCE($9, now() + COALESCE($10, interval '0')))";

        // One autocommitted statement. The locking CTE is MATERIALIZED so it is evaluated exactly once: a LIMIT ... FOR UPDATE
        // SKIP LOCKED subquery that the planner re-scans (for example under IN (...)) can return more than LIMIT rows.
        // RETURNING order is unspecified, so the final SELECT orders by the pre-claim available_at.
        ClaimSql =
            $"""
            WITH next AS MATERIALIZED (
                SELECT id, available_at
                FROM {table}
                WHERE status IN (0, 1) AND available_at <= now()
                ORDER BY available_at
                LIMIT $1
                FOR UPDATE SKIP LOCKED
            ), claimed AS (
                UPDATE {table} AS m
                SET status = 1, lock_token = $2, locked_by = $3, attempt_count = m.attempt_count + 1,
                    last_attempt_at = now(), available_at = now() + $4
                FROM next
                WHERE m.id = next.id
                RETURNING {claimedColumns}, next.available_at AS claim_order
            )
            SELECT {columns} FROM claimed ORDER BY claim_order, id
            """;

        MarkDeliveredSql =
            $"UPDATE {table} SET status = 2, delivered_at = now(), lock_token = NULL WHERE id = $1 AND lock_token = $2";

        ScheduleRetrySql =
            $"UPDATE {table} SET status = 0, available_at = now() + $3, last_error = $4, lock_token = NULL WHERE id = $1 AND lock_token = $2";

        MarkDeadLetteredSql =
            $"UPDATE {table} SET status = 3, dead_lettered_at = now(), last_error = $3, lock_token = NULL WHERE id = $1 AND lock_token = $2";

        ReleaseSql =
            $"UPDATE {table} SET status = 0, available_at = now(), attempt_count = GREATEST(attempt_count - 1, 0), " +
            "lock_token = NULL, locked_by = NULL WHERE id = $1 AND lock_token = $2";

        // ADR 0003 batch variants: each row is joined on (id, lock_token), so it is still guarded by its own token, and only rows
        // that actually changed are returned.
        MarkDeliveredBatchSql =
            $"""
            UPDATE {table} AS m SET status = 2, delivered_at = now(), lock_token = NULL
            FROM unnest($1::uuid[], $2::uuid[]) AS a(id, token)
            WHERE m.id = a.id AND m.lock_token = a.token
            RETURNING m.id, a.token
            """;

        ReleaseBatchSql =
            $"""
            UPDATE {table} AS m SET status = 0, available_at = now(), attempt_count = GREATEST(m.attempt_count - 1, 0),
                lock_token = NULL, locked_by = NULL
            FROM unnest($1::uuid[], $2::uuid[]) AS a(id, token)
            WHERE m.id = a.id AND m.lock_token = a.token
            RETURNING m.id, a.token
            """;

        DeleteDeliveredSql =
            $"""
            WITH picked AS MATERIALIZED (
                SELECT id FROM {table}
                WHERE status = 2 AND delivered_at < now() - $1
                ORDER BY delivered_at
                LIMIT $2
                FOR UPDATE SKIP LOCKED
            )
            DELETE FROM {table} AS m
            USING picked
            WHERE m.id = picked.id
            """;

        GetSql = $"SELECT {columns} FROM {table} WHERE id = $1";

        QuerySqlPrefix = $"SELECT {columns} FROM {table}";

        // One scalar subquery per status. Each predicate matches a partial index (claim: status IN (0,1); cleanup: status = 2;
        // dead: status = 3), so the planner never needs a full heap scan. Previously a single count(*) FILTER over the
        // whole table was a parallel seq scan (about 2 s on 2M rows; review finding CR-03).
        StatisticsSql =
            $"""
            SELECT
                (SELECT count(*) FROM {table} WHERE status IN (0, 1) AND status = 0),
                (SELECT count(*) FROM {table} WHERE status IN (0, 1) AND status = 1),
                (SELECT count(*) FROM {table} WHERE status = 2),
                (SELECT count(*) FROM {table} WHERE status = 3),
                (SELECT EXTRACT(EPOCH FROM now() - min(available_at))::double precision
                 FROM {table} WHERE status IN (0, 1) AND available_at <= now())
            """;

        RequeueSql =
            $"UPDATE {table} SET status = 0, available_at = now(), attempt_count = 0, dead_lettered_at = NULL, lock_token = NULL " +
            "WHERE id = $1 AND status = 3";

        RequeueDeadLetteredSql = RequeueManySql(table, filterByType: false);
        RequeueDeadLetteredByTypeSql = RequeueManySql(table, filterByType: true);

        HealthSql = $"SELECT 1 FROM {table} LIMIT 1";
    }

    public string Schema { get; }

    public string TableName { get; }

    /// <summary>The quoted, schema-qualified table name.</summary>
    public string Table { get; }

    public int CommandTimeoutSeconds { get; }

    public string ClaimIndexName { get; }

    public string CleanupIndexName { get; }

    public string DeadIndexName { get; }

    public string StatusConstraintName { get; }

    public string CreateScript { get; }

    public string InsertSql { get; }

    public string ClaimSql { get; }

    public string MarkDeliveredSql { get; }

    public string ScheduleRetrySql { get; }

    public string MarkDeadLetteredSql { get; }

    public string ReleaseSql { get; }

    public string MarkDeliveredBatchSql { get; }

    public string ReleaseBatchSql { get; }

    public string DeleteDeliveredSql { get; }

    public string GetSql { get; }

    public string QuerySqlPrefix { get; }

    public string StatisticsSql { get; }

    public string RequeueSql { get; }

    public string RequeueDeadLetteredSql { get; }

    public string RequeueDeadLetteredByTypeSql { get; }

    public string HealthSql { get; }

    /// <summary>Applies <paramref name="configure"/> to fresh options and validates them eagerly (at registration).</summary>
    public static PostgreSqlSettings Create(Action<PostgreSqlOutboxOptions>? configure)
    {
        var options = new PostgreSqlOutboxOptions();
        configure?.Invoke(options);

        var schema = SqlIdentifier.Validate(options.Schema, nameof(PostgreSqlOutboxOptions.Schema));
        var table = SqlIdentifier.Validate(options.TableName, nameof(PostgreSqlOutboxOptions.TableName));
        if (string.Equals(table, ReservedTableName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"'{nameof(PostgreSqlOutboxOptions.TableName)}' cannot be '{ReservedTableName}'; DbRelay uses that table to record the schema version.",
                nameof(configure));
        }

        if (options.CommandTimeout <= TimeSpan.Zero || options.CommandTimeout.TotalSeconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configure),
                options.CommandTimeout,
                $"'{nameof(PostgreSqlOutboxOptions.CommandTimeout)}' must be positive and at most int.MaxValue seconds.");
        }

        return new PostgreSqlSettings(schema, table, options.CommandTimeout);
    }

    private static string Quote(string identifier) => "\"" + identifier + "\"";

    private static string RequeueManySql(string table, bool filterByType) =>
        $"""
        WITH picked AS MATERIALIZED (
            SELECT id FROM {table}
            WHERE status = 3{(filterByType ? " AND message_type = $2" : string.Empty)}
            ORDER BY dead_lettered_at
            LIMIT $1
            FOR UPDATE SKIP LOCKED
        )
        UPDATE {table} AS m
        SET status = 0, available_at = now(), attempt_count = 0, dead_lettered_at = NULL, lock_token = NULL
        FROM picked
        WHERE m.id = picked.id
        """;

    private static string IndexName(string table, string purpose) => BoundedName("ix_", table, "_" + purpose);

    /// <summary>
    /// Builds <c>prefix + table + suffix</c>. PostgreSQL truncates identifiers longer than 63 bytes, which could make the names of
    /// two indexes collide (and <c>IF NOT EXISTS</c> would then silently skip one), so long names are shortened deterministically
    /// with a hash of the full table name.
    /// </summary>
    private static string BoundedName(string prefix, string table, string suffix)
    {
        var name = prefix + table + suffix;
        if (name.Length <= MaxIdentifierLength)
        {
            return name;
        }

        var hash = "_" + Fnv1a(table).ToString("x8", CultureInfo.InvariantCulture);
        var keep = MaxIdentifierLength - prefix.Length - suffix.Length - hash.Length;
        return prefix + table[..keep] + hash + suffix;
    }

    private static uint Fnv1a(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }

    private static string LoadScript()
    {
        using var stream = typeof(PostgreSqlSettings).Assembly.GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ScriptResourceName}' is missing from {typeof(PostgreSqlSettings).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
