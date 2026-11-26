using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DbRelay.Storage;

namespace DbRelay.MySql.Internal;

/// <summary>
/// The SQL text of the MySQL / MariaDB provider, built once from validated identifiers quoted with backticks. Every value is an
/// <c>@name</c> parameter. GUIDs are written with <c>UNHEX(@hex)</c> and read with <c>HEX(...)</c>, so the stored bytes never depend
/// on the caller's <c>GuidFormat</c> connection option. All times come from the database clock (<c>UTC_TIMESTAMP(6)</c>).
/// </summary>
internal sealed class MySqlOutboxSql
{
    internal const string ScriptResourceName = "DbRelay.MySql.Scripts.001_create_outbox.sql";

    /// <summary>The version table shared by every outbox table in a database. A prefixed name, because it shares the application's database.</summary>
    internal const string VersionTableName = "dbrelay_schema_version";

    /// <summary>Hexadecimal characters per GUID (16 bytes), as passed to <c>UNHEX</c> and returned by <c>HEX</c>.</summary>
    internal const int HexPerId = 32;

    /// <summary>Upper bound for rows deleted or requeued by one statement.</summary>
    internal const int MaxRowsPerBulkStatement = 1000;

    internal const string Now = "UTC_TIMESTAMP(6)";

    /// <summary>Insert statements up to this many rows are cached by row count (MY-03).</summary>
    internal const int MaxCachedInsertRows = 64;

    /// <summary>Reads the server's packet limit (MY-02).</summary>
    internal const string MaxAllowedPacket = "SELECT @@max_allowed_packet";

    /// <summary>
    /// Reads whether the binary log is statement-based (MY-05): with binlog_format = STATEMENT, InnoDB rejects writes under READ
    /// COMMITTED (error 1665), so DbRelay then keeps the session default for its own transactions.
    /// </summary>
    internal const string BinlogSettings = "SELECT @@log_bin, @@binlog_format";

    private const int MaxIdentifierLength = 64;

    /// <summary>Columns read into an <c>OutboxMessageDetails</c>, in ordinal order (see <c>MySqlOutboxStore.ReadDetails</c>).</summary>
    internal static readonly string[] DetailColumns =
    [
        "HEX(id)", "message_type", "destination", "payload", "content_type", "headers", "correlation_id", "causation_id", "status",
        "attempt_count", "created_at", "scheduled_at", "available_at", "locked_by", "last_attempt_at", "delivered_at",
        "dead_lettered_at", "last_error",
    ];

    /// <summary>Columns the provider's statements need; the health check and EnsureCreatedAsync verify they exist (MY-08).</summary>
    internal static readonly string[] RequiredColumns =
    [
        "id", "message_type", "destination", "payload", "payload_size", "content_type", "headers", "correlation_id", "causation_id",
        "status", "attempt_count", "created_at", "scheduled_at", "available_at", "lock_token", "locked_by", "last_attempt_at",
        "delivered_at", "dead_lettered_at", "last_error", "claim_at", "cleanup_at", "dead_at",
    ];

    /// <summary>IN lists are built in buckets of these sizes, padded by repeating the last value, so few statement texts exist.</summary>
    internal static readonly int[] Buckets = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, OutboxLimits.MaxClaimBatchSize];

    private readonly ConcurrentDictionary<int, string> _inserts = new();
    private readonly ConcurrentDictionary<int, string> _claimUpdates = new();
    private readonly ConcurrentDictionary<int, string> _claimedRows = new();
    private readonly ConcurrentDictionary<int, string> _lockForBatch = new();
    private readonly ConcurrentDictionary<(int Bucket, bool Delivered), string> _batchUpdates = new();
    private readonly string _columns;

    public MySqlOutboxSql(string? schema, string tableName)
    {
        Schema = schema;
        TableName = tableName;
        Table = Qualify(tableName);
        VersionTable = Qualify(VersionTableName);
        ClaimIndexName = BoundedName("ix_", tableName, "_claim");
        CleanupIndexName = BoundedName("ix_", tableName, "_cleanup");
        DeadIndexName = BoundedName("ix_", tableName, "_dead");

        _columns = string.Join(", ", DetailColumns);
        var table = Table;

        CreateScript = LoadScript()
            .Replace("{{table}}", table, StringComparison.Ordinal)
            .Replace("{{table_name}}", tableName, StringComparison.Ordinal)
            .Replace("{{version_table}}", VersionTable, StringComparison.Ordinal)
            .Replace("{{ix_claim}}", Quote(ClaimIndexName), StringComparison.Ordinal)
            .Replace("{{ix_cleanup}}", Quote(CleanupIndexName), StringComparison.Ordinal)
            .Replace("{{ix_dead}}", Quote(DeadIndexName), StringComparison.Ordinal);

        // Step 1 of a claim (ADR 0006), inside a READ COMMITTED transaction: lock up to @batch_size due rows in claim order, skipping
        // rows another claimer holds. payload_size is stored at insert, so the ADR 0004 budget needs no payload read (MY-01).
        // FORCE INDEX keeps the claim an index range scan in claim_at order (MY-12): a sort would lock every due row for the whole
        // claim. A missing index makes the claim fail loudly, as on SQL Server, and the health check reports it (MY-08).
        ClaimCandidates =
            $"SELECT HEX(id), payload_size FROM {table} FORCE INDEX ({Quote(ClaimIndexName)}) WHERE claim_at <= {Now} " +
            "ORDER BY claim_at LIMIT @batch_size FOR UPDATE SKIP LOCKED";

        MarkDelivered =
            $"UPDATE {table} SET status = 2, delivered_at = {Now}, lock_token = NULL WHERE id = UNHEX(@id) AND lock_token = UNHEX(@lock_token)";

        ScheduleRetry =
            $"UPDATE {table} SET status = 0, available_at = DATE_ADD({Now}, INTERVAL @delay_us MICROSECOND), last_error = @error, " +
            "lock_token = NULL WHERE id = UNHEX(@id) AND lock_token = UNHEX(@lock_token)";

        MarkDeadLettered =
            $"UPDATE {table} SET status = 3, dead_lettered_at = {Now}, last_error = @error, lock_token = NULL " +
            "WHERE id = UNHEX(@id) AND lock_token = UNHEX(@lock_token)";

        Release =
            $"UPDATE {table} SET status = 0, available_at = {Now}, attempt_count = GREATEST(attempt_count - 1, 0), " +
            "lock_token = NULL, locked_by = NULL WHERE id = UNHEX(@id) AND lock_token = UNHEX(@lock_token)";

        DeleteDelivered =
            $"DELETE FROM {table} WHERE cleanup_at < DATE_SUB({Now}, INTERVAL @older_than_us MICROSECOND) ORDER BY cleanup_at LIMIT @max_count";

        SelectById = $"SELECT {_columns} FROM {table} WHERE id = UNHEX(@id)";

        // Each count reads only its index: claim (claim_at, status), cleanup (cleanup_at), dead (dead_at).
        Statistics =
            $"""
            SELECT
                (SELECT COUNT(*) FROM {table} WHERE claim_at IS NOT NULL AND status = 0),
                (SELECT COUNT(*) FROM {table} WHERE claim_at IS NOT NULL AND status = 1),
                (SELECT COUNT(*) FROM {table} WHERE cleanup_at IS NOT NULL),
                (SELECT COUNT(*) FROM {table} WHERE dead_at IS NOT NULL),
                (SELECT TIMESTAMPDIFF(MICROSECOND, MIN(claim_at), {Now}) FROM {table} WHERE claim_at <= {Now})
            """;

        Requeue =
            $"UPDATE {table} SET status = 0, available_at = {Now}, attempt_count = 0, dead_lettered_at = NULL, lock_token = NULL " +
            "WHERE id = UNHEX(@id) AND status = 3";

        RequeueDeadLettered = RequeueMany(table, filterByType: false);
        RequeueDeadLetteredOfType = RequeueMany(table, filterByType: true);

        // MY-08: the table must answer and have the shape this provider needs: its three indexes and the columns its statements use.
        // CREATE TABLE IF NOT EXISTS accepts an existing table of any shape, so EnsureCreatedAsync runs the same check afterwards.
        var database = Schema is null ? "DATABASE()" : "'" + Schema + "'";
        HealthCheck =
            $"""
            SELECT (SELECT 1 FROM {table} LIMIT 1),
                   (SELECT COUNT(DISTINCT INDEX_NAME) FROM information_schema.STATISTICS
                    WHERE TABLE_SCHEMA = {database} AND TABLE_NAME = '{tableName}'
                      AND INDEX_NAME IN ('{ClaimIndexName}', '{CleanupIndexName}', '{DeadIndexName}')),
                   (SELECT COUNT(*) FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = {database} AND TABLE_NAME = '{tableName}'
                      AND COLUMN_NAME IN ({string.Join(", ", RequiredColumns.Select(c => "'" + c + "'"))}))
            """;
    }

    public string? Schema { get; }

    public string TableName { get; }

    /// <summary>The quoted, optionally database-qualified outbox table.</summary>
    public string Table { get; }

    public string VersionTable { get; }

    public string ClaimIndexName { get; }

    public string CleanupIndexName { get; }

    public string DeadIndexName { get; }

    public string CreateScript { get; }

    public string ClaimCandidates { get; }

    public string MarkDelivered { get; }

    public string ScheduleRetry { get; }

    public string MarkDeadLettered { get; }

    public string Release { get; }

    public string DeleteDelivered { get; }

    public string SelectById { get; }

    public string Statistics { get; }

    public string Requeue { get; }

    public string RequeueDeadLettered { get; }

    public string RequeueDeadLetteredOfType { get; }

    public string HealthCheck { get; }

    /// <summary>The smallest bucket of <see cref="Buckets"/> that holds <paramref name="count"/> values.</summary>
    internal static int BucketFor(int count) => Buckets.First(b => b >= count);

    /// <summary>
    /// One multi-row <c>INSERT</c> of <paramref name="rows"/> rows (1 to <see cref="OutboxLimits.MaxEnqueueBatchSize"/>, 10 parameters
    /// each), atomic on its own, with parameters <c>@id{i}</c> (32 hex digits), <c>@mt{i}</c>, <c>@de{i}</c>, <c>@pl{i}</c>,
    /// <c>@ct{i}</c>, <c>@hd{i}</c>, <c>@co{i}</c>, <c>@ca{i}</c>, <c>@da{i}</c> and <c>@dl{i}</c> (delay in microseconds).
    /// </summary>
    public string GetInsert(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, OutboxLimits.MaxEnqueueBatchSize);
        // MY-03: only small statements are cached. Every row count up to 1,000 would hold about 259 MiB of text; a large insert is
        // rare, and building its text costs little next to sending its rows.
        return rows <= MaxCachedInsertRows
            ? _inserts.GetOrAdd(rows, static (count, sql) => sql.BuildInsert(count), this)
            : BuildInsert(rows);
    }

    /// <summary>
    /// Step 2 of a claim: leases the chosen rows, all still locked by step 1, and counts the attempt (invariant 10). The status and
    /// due-time re-check matches the Oracle provider's (OR-01). Parameters <c>@i0</c>.. (hex IDs), <c>@lock_token</c>,
    /// <c>@worker_id</c>, <c>@lease_us</c>.
    /// </summary>
    public string GetClaimUpdate(int bucket) =>
        _claimUpdates.GetOrAdd(
            bucket,
            static (size, sql) =>
                $"UPDATE {sql.Table} SET status = 1, lock_token = UNHEX(@lock_token), locked_by = @worker_id, attempt_count = attempt_count + 1, " +
                $"last_attempt_at = {Now}, available_at = DATE_ADD({Now}, INTERVAL @lease_us MICROSECOND) " +
                $"WHERE id IN ({UnhexList("i", size)}) AND status IN (0, 1) AND available_at <= {Now}",
            this);

    /// <summary>Step 3 of a claim: reads the rows this claim leased. Parameters <c>@i0</c>.. and <c>@lock_token</c>.</summary>
    public string GetClaimedRows(int bucket) =>
        _claimedRows.GetOrAdd(
            bucket,
            static (size, sql) => $"SELECT {sql._columns} FROM {sql.Table} WHERE id IN ({UnhexList("i", size)}) AND lock_token = UNHEX(@lock_token)",
            this);

    /// <summary>
    /// ADR 0003 batch, step 1: locks the batch's rows in primary key order (BA-07) and returns their current tokens, so the store keeps
    /// only the items whose token still matches. Parameters <c>@i0</c>...
    /// </summary>
    public string GetLockForBatch(int bucket) =>
        _lockForBatch.GetOrAdd(
            bucket,
            static (size, sql) => $"SELECT HEX(id), HEX(lock_token) FROM {sql.Table} WHERE id IN ({UnhexList("i", size)}) ORDER BY id FOR UPDATE",
            this);

    /// <summary>
    /// ADR 0003 batch, step 2: changes exactly the matching rows, which step 1 holds locked, guarded by (id, lock_token) pairs.
    /// Parameters <c>@i0</c>.. and <c>@t0</c>...
    /// </summary>
    public string GetBatchUpdate(int bucket, bool delivered) =>
        _batchUpdates.GetOrAdd(
            (bucket, delivered),
            static (key, sql) =>
                $"UPDATE {sql.Table} SET " +
                (key.Delivered
                    ? $"status = 2, delivered_at = {Now}, lock_token = NULL"
                    : $"status = 0, available_at = {Now}, attempt_count = GREATEST(attempt_count - 1, 0), lock_token = NULL, locked_by = NULL") +
                $" WHERE (id, lock_token) IN ({PairList(key.Bucket)})",
            this);

    /// <summary><c>(UNHEX(@i0), UNHEX(@t0)), ...</c>: each batch row is guarded by its own token in SQL as well (MY-06).</summary>
    private static string PairList(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"(UNHEX(@i{i.ToString(CultureInfo.InvariantCulture)}), UNHEX(@t{i.ToString(CultureInfo.InvariantCulture)}))"));

    /// <summary>
    /// The admin page query: the narrow keys are filtered, sorted and paged first, then the page's full rows are fetched by primary
    /// key (as the other providers do, CR-21).
    /// </summary>
    public string GetQuery(bool byStatus, bool byType, bool createdAfter, bool createdBefore)
    {
        var keys = new StringBuilder($"SELECT id, created_at FROM {Table}");
        var separator = " WHERE ";
        void Filter(bool apply, string condition)
        {
            if (apply)
            {
                keys.Append(separator).Append(condition);
                separator = " AND ";
            }
        }

        Filter(byStatus, "status = @status");
        Filter(byType, "message_type = @message_type");
        Filter(createdAfter, "created_at >= @created_after");
        Filter(createdBefore, "created_at < @created_before");
        keys.Append(" ORDER BY created_at, id LIMIT @take OFFSET @skip");

        var columns = string.Join(", ", DetailColumns.Select(c => c.StartsWith("HEX(", StringComparison.Ordinal) ? "HEX(m.id)" : "m." + c));
        return $"SELECT {columns} FROM ({keys}) k JOIN {Table} m ON m.id = k.id ORDER BY k.created_at, k.id";
    }

    private static string UnhexList(string prefix, int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"UNHEX(@{prefix}{i.ToString(CultureInfo.InvariantCulture)})"));

    private string Qualify(string name) => Schema is null ? Quote(name) : Quote(Schema) + "." + Quote(name);

    private static string Quote(string identifier) => "`" + identifier + "`";

    private string BuildInsert(int rows)
    {
        var sql = new StringBuilder(
            $"INSERT INTO {Table} (id, message_type, destination, payload, content_type, headers, correlation_id, causation_id, " +
            "status, attempt_count, created_at, scheduled_at, available_at) VALUES ");
        for (var i = 0; i < rows; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            var due = $"COALESCE(@da{n}, DATE_ADD({Now}, INTERVAL COALESCE(@dl{n}, 0) MICROSECOND))";
            sql.Append(i == 0 ? string.Empty : ", ")
               .Append(CultureInfo.InvariantCulture, $"(UNHEX(@id{n}), @mt{n}, @de{n}, @pl{n}, @ct{n}, @hd{n}, @co{n}, @ca{n}, 0, 0, {Now}, {due}, {due})");
        }

        return sql.ToString();
    }

    private static string RequeueMany(string table, bool filterByType) =>
        $"UPDATE {table} SET status = 0, available_at = {Now}, attempt_count = 0, dead_lettered_at = NULL, lock_token = NULL " +
        "WHERE dead_at IS NOT NULL" + (filterByType ? " AND message_type = @message_type" : string.Empty) +
        " ORDER BY dead_at LIMIT @max_count";

    /// <summary>
    /// Builds <c>prefix + table + suffix</c>, shortened deterministically with a hash of the full table name when it would exceed
    /// MySQL's 64-character identifier limit.
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
        using var stream = typeof(MySqlOutboxSql).Assembly.GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ScriptResourceName}' is missing from {typeof(MySqlOutboxSql).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
