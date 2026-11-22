using System.Text;

namespace DbRelay.SqlServer.Internal;

/// <summary>
/// The fixed SQL statements for one configured outbox table. Identifiers are validated by <see cref="Storage.SqlIdentifier"/>
/// before they reach this class and are always bracket-quoted; every value is a parameter.
/// </summary>
/// <remarks>
/// <para>
/// Times: every statement captures the database clock once into <c>@_now</c>, truncated to whole milliseconds. Columns are
/// <c>datetime2(3)</c>, and converting <c>SYSUTCDATETIME()</c> to <c>datetime2(3)</c> would round, which could store a value up
/// to 0.5 ms in the future (a zero-delay retry would then not be claimable immediately). Truncating keeps every stored
/// "now" at or before the real time.
/// </para>
/// <para>
/// Intervals are passed as <c>bigint</c> milliseconds and applied as seconds plus remaining milliseconds, because
/// <c>DATEADD</c> takes an <c>int</c> (a millisecond <c>int</c> overflows after 24.8 days; delays can be up to 3650 days).
/// </para>
/// <para>
/// Filtered indexes are only matched when the query states the filter with literals, so the status predicates below are
/// literals (<c>[status] IN (0, 1)</c>, <c>[status] = 2</c>, <c>[status] = 3</c>), never parameters.
/// </para>
/// <para>
/// Claim and bulk requeue run in two phases inside one batch and one short transaction: an
/// <c>INSERT @table SELECT TOP (n) [sequence] ... WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(filtered)) ORDER BY ...</c> picks and
/// U-locks exactly n rows (the filtered index covers the predicate and <c>sequence</c> is its row locator, so this is an ordered
/// seek that stops after n rows), then an <c>UPDATE ... OUTPUT</c> joins those rows by clustered key. Measured on SQL Server 2022,
/// the single-statement form <c>WITH cte AS (SELECT TOP (n) ...) UPDATE cte</c> is compiled either as a clustered index scan plus
/// sort (without the index hint) or as a seek under an eager spool (Halloween protection, because the update changes the key of
/// the index being read). Both read and U-lock every due row before <c>TOP</c> applies: with a backlog, one claim would hold
/// locks on rows it does not return (concurrent claimers skip them) and could escalate to a table lock.
/// The second phase reads the outbox with <c>FORCESEEK</c> (so it is driven by the picked keys and never scans past rows locked
/// by an uncommitted enqueue) and <c>READPAST</c> (a picked row whose clustered row is locked by a late acknowledgement is
/// skipped instead of risking a deadlock); its <c>WHERE</c> re-checks the claim predicate.
/// The retention delete has no Halloween problem and stays a single <c>DELETE</c> through a CTE. <c>INDEX(...)</c> hints pin
/// the access path the locking design relies on.
/// </para>
/// </remarks>
internal sealed class SqlServerOutboxSql
{
    /// <summary>Parameters per inserted row. 200 rows use 2000 of SQL Server's 2100 parameters per request.</summary>
    internal const int InsertParametersPerRow = 10;

    /// <summary>The maximum number of rows inserted by one statement.</summary>
    internal const int MaxInsertRowsPerStatement = 200;

    /// <summary>Rows changed by one bulk delete/requeue statement; keeps each statement well below the 5000-lock escalation threshold.</summary>
    internal const int MaxRowsPerBulkStatement = 1000;

    internal const string ScriptResourceName = "DbRelay.SqlServer.Scripts.001_create_outbox.sql";

    private const string Now =
        "DECLARE @_sys datetime2(7) = SYSUTCDATETIME();\n" +
        "DECLARE @_now datetime2(3) = DATEADD(nanosecond, -(DATEPART(nanosecond, @_sys) % 1000000), @_sys);\n";

    /// <summary>The columns read into <see cref="OutboxMessageDetails"/>, in reader ordinal order.</summary>
    private static readonly string[] DetailColumns =
    [
        "id", "message_type", "destination", "payload", "content_type", "headers", "correlation_id", "causation_id", "status",
        "attempt_count", "created_at", "scheduled_at", "available_at", "last_attempt_at", "delivered_at", "dead_lettered_at",
        "last_error", "locked_by",
    ];

    private static readonly Lazy<string> ScriptTemplate = new(LoadScriptTemplate);

    private readonly string[] _insertByRowCount = new string[MaxInsertRowsPerStatement + 1];
    private readonly string[] _queryByFilters = new string[16];
    private readonly string _detailColumnsOfM;

    public SqlServerOutboxSql(string schema, string table)
    {
        Schema = schema;
        Table = table;
        QualifiedTable = $"[{schema}].[{table}]";
        var t = QualifiedTable;
        var columns = string.Join(", ", DetailColumns.Select(c => $"[{c}]"));

        SelectById = $"SELECT {columns} FROM {t} WHERE [id] = @id;";

        // Two phases in one batch and one short transaction (see the class remarks): pick and U-lock exactly @batch_size rows
        // through an ordered seek of the filtered index, then update those rows by clustered key.
        Claim =
            "SET NOCOUNT ON;\n" +
            "SET XACT_ABORT ON;\n" + Now +
            "DECLARE @_claimed TABLE ([sequence] bigint NOT NULL PRIMARY KEY);\n" +
            "BEGIN TRANSACTION;\n" +
            "INSERT INTO @_claimed ([sequence])\n" +
            "SELECT TOP (@batch_size) [sequence]\n" +
            $"FROM {t} WITH (UPDLOCK, READPAST, ROWLOCK, INDEX([ix_{table}_claim]))\n" +
            "WHERE [status] IN (0, 1) AND [available_at] <= @_now\n" +
            "ORDER BY [available_at];\n" +
            "UPDATE m\n" +
            "SET [status] = 1, [lock_token] = @lock_token, [locked_by] = @worker_id, [attempt_count] = m.[attempt_count] + 1,\n" +
            "    [last_attempt_at] = @_now, [available_at] = " + AddMilliseconds("@_now", "@lease_ms") + "\n" +
            "OUTPUT " + string.Join(", ", DetailColumns.Select(c => $"inserted.[{c}]")) + ", deleted.[available_at], inserted.[sequence]\n" +
            $"FROM {t} AS m WITH (ROWLOCK, READPAST, FORCESEEK)\n" +
            "INNER JOIN @_claimed AS c ON c.[sequence] = m.[sequence]\n" +
            "WHERE m.[status] IN (0, 1) AND m.[available_at] <= @_now;\n" +
            "COMMIT TRANSACTION;";

        MarkDelivered = Guarded("[status] = 2, [delivered_at] = @_now, [lock_token] = NULL");
        ScheduleRetry = Guarded("[status] = 0, [available_at] = " + AddMilliseconds("@_now", "@delay_ms") + ", [last_error] = @error, [lock_token] = NULL");
        MarkDeadLettered = Guarded("[status] = 3, [dead_lettered_at] = @_now, [last_error] = @error, [lock_token] = NULL");
        Release = Guarded(
            "[status] = 0, [available_at] = @_now, [attempt_count] = CASE WHEN [attempt_count] > 0 THEN [attempt_count] - 1 ELSE 0 END, [lock_token] = NULL, [locked_by] = NULL");

        // ADR 0003 batch variants: a parameterized VALUES list joined on (id, lock_token), so each row stays guarded by its own token
        // and only changed rows are output. Measured on SQL Server 2022 (20,000-row table), per call:
        //   single-row UPDATE 88 us; XML + table variable 2,268 us for 1 item (rejected: fixed overhead made batching slower);
        //   VALUES 8 items 339 us; VALUES 64 items 13,523 us without FORCESEEK (scan) but 1,117 us with it.
        // Statements are generated for power-of-two bucket sizes up to 64 (6 cached plans per operation). Smaller batches are
        // padded by repeating the last item, which is harmless: a duplicate join row updates and outputs the target once.
        foreach (var bucket in BatchBuckets)
        {
            _markDeliveredBatch[bucket] = GuardedBatch("[status] = 2, [delivered_at] = @_now, [lock_token] = NULL", bucket);
            _releaseBatch[bucket] = GuardedBatch(
                "[status] = 0, [available_at] = @_now, [attempt_count] = CASE WHEN m.[attempt_count] > 0 THEN m.[attempt_count] - 1 ELSE 0 END, [lock_token] = NULL, [locked_by] = NULL",
                bucket);
        }

        DeleteDelivered =
            "SET NOCOUNT ON;\n" + Now +
            "WITH [expired] AS (\n" +
            "    SELECT TOP (@max_count) [sequence]\n" +
            $"    FROM {t} WITH (READPAST, ROWLOCK, INDEX([ix_{table}_delivered]))\n" +
            "    WHERE [status] = 2 AND [delivered_at] <= " + AddMilliseconds("@_now", "@older_than_ms") + "\n" +
            ")\n" +
            "DELETE FROM [expired];\n" +
            "SELECT @@ROWCOUNT;";

        Requeue =
            "SET NOCOUNT ON;\n" + Now +
            $"UPDATE {t} SET [status] = 0, [available_at] = @_now, [attempt_count] = 0, [dead_lettered_at] = NULL, [lock_token] = NULL\n" +
            "WHERE [id] = @id AND [status] = 3;\n" +
            "SELECT @@ROWCOUNT;";

        RequeueDeadLettered = BulkRequeue(typeFilter: false);
        RequeueDeadLetteredOfType = BulkRequeue(typeFilter: true);

        Statistics =
            "SET NOCOUNT ON;\n" + Now +
            "SELECT\n" +
            $"    (SELECT COUNT_BIG(*) FROM {t} WITH (READPAST) WHERE [status] = 0),\n" +
            $"    (SELECT COUNT_BIG(*) FROM {t} WITH (READPAST) WHERE [status] = 1),\n" +
            $"    (SELECT COUNT_BIG(*) FROM {t} WITH (READPAST) WHERE [status] = 2),\n" +
            $"    (SELECT COUNT_BIG(*) FROM {t} WITH (READPAST) WHERE [status] = 3),\n" +
            $"    DATEDIFF_BIG(millisecond, (SELECT MIN([available_at]) FROM {t} WITH (READPAST) WHERE [status] IN (0, 1) AND [available_at] <= @_now), @_now);";

        HealthCheck = $"SELECT TOP (0) [sequence] FROM {t};";

        _detailColumnsOfM = string.Join(", ", DetailColumns.Select(c => $"m.[{c}]"));
        CreateScript = ScriptTemplate.Value.Replace("{{schema}}", schema, StringComparison.Ordinal).Replace("{{table}}", table, StringComparison.Ordinal);
        CreateScriptBatches = SplitBatches(CreateScript);
        SchemaLockResource = $"DbRelay.SqlServer:schema:{schema}";
    }

    public string Schema { get; }

    public string Table { get; }

    public string QualifiedTable { get; }

    public string SelectById { get; }

    public string Claim { get; }

    public string MarkDelivered { get; }

    public string ScheduleRetry { get; }

    public string MarkDeadLettered { get; }

    public string Release { get; }

    /// <summary>The largest number of items in one batch statement; larger batches are split into chunks.</summary>
    internal const int MaxBatchStatementItems = 64;

    private static readonly int[] BatchBuckets = [2, 4, 8, 16, 32, MaxBatchStatementItems];

    private readonly Dictionary<int, string> _markDeliveredBatch = [];
    private readonly Dictionary<int, string> _releaseBatch = [];

    /// <summary>The smallest bucket size that holds <paramref name="count"/> items (2 to 64).</summary>
    internal static int BucketFor(int count)
    {
        foreach (var bucket in BatchBuckets)
        {
            if (bucket >= count)
            {
                return bucket;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(count), count, $"At most {MaxBatchStatementItems} items per batch statement.");
    }

    public string GetMarkDeliveredBatch(int bucket) => _markDeliveredBatch[bucket];

    public string GetReleaseBatch(int bucket) => _releaseBatch[bucket];

    public string DeleteDelivered { get; }

    public string Requeue { get; }

    public string RequeueDeadLettered { get; }

    public string RequeueDeadLetteredOfType { get; }

    public string Statistics { get; }

    public string HealthCheck { get; }

    public string CreateScript { get; }

    public IReadOnlyList<string> CreateScriptBatches { get; }

    public string SchemaLockResource { get; }

    /// <summary>
    /// Returns the admin query for the given combination of filters (parameters <c>@status</c>, <c>@message_type</c>,
    /// <c>@created_after</c>, <c>@created_before</c>, <c>@skip</c>, <c>@take</c>).
    /// </summary>
    /// <remarks>
    /// Paging sorts only the narrow keys (<c>created_at</c>, <c>sequence</c>), then fetches the page's full rows by clustered key
    /// in that order. Sorting the full rows directly would request a memory grant sized for the <c>varbinary(max)</c> and
    /// <c>nvarchar(max)</c> columns; on SQL Server Express that grant waited on RESOURCE_SEMAPHORE for about 25 seconds.
    /// </remarks>
    public string GetQuery(bool status, bool messageType, bool createdAfter, bool createdBefore)
    {
        var key = (status ? 1 : 0) | (messageType ? 2 : 0) | (createdAfter ? 4 : 0) | (createdBefore ? 8 : 0);
        var cached = Volatile.Read(ref _queryByFilters[key]);
        if (cached is not null)
        {
            return cached;
        }

        var conditions = new List<string>(4);
        if (status)
        {
            conditions.Add("[status] = @status");
        }

        if (messageType)
        {
            conditions.Add("[message_type] = @message_type");
        }

        if (createdAfter)
        {
            conditions.Add("[created_at] >= @created_after");
        }

        if (createdBefore)
        {
            conditions.Add("[created_at] < @created_before");
        }

        var where = conditions.Count == 0 ? string.Empty : "\n    WHERE " + string.Join(" AND ", conditions);
        var sql =
            $"SELECT {_detailColumnsOfM}\n" +
            "FROM (\n" +
            $"    SELECT [sequence], [created_at] FROM {QualifiedTable}{where}\n" +
            "    ORDER BY [created_at], [sequence] OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY\n" +
            ") AS k\n" +
            $"INNER LOOP JOIN {QualifiedTable} AS m ON m.[sequence] = k.[sequence]\n" +
            "ORDER BY k.[created_at], k.[sequence];";
        Volatile.Write(ref _queryByFilters[key], sql);
        return sql;
    }

    /// <summary>Returns the INSERT statement for <paramref name="rowCount"/> rows (1 to <see cref="MaxInsertRowsPerStatement"/>).</summary>
    public string GetInsert(int rowCount)
    {
        var cached = Volatile.Read(ref _insertByRowCount[rowCount]);
        if (cached is not null)
        {
            return cached;
        }

        var sql = BuildInsert(rowCount);
        Volatile.Write(ref _insertByRowCount[rowCount], sql);
        return sql;
    }

    internal static string AddMilliseconds(string dateExpression, string millisecondsParameter) =>
        $"DATEADD(millisecond, CAST({millisecondsParameter} % 1000 AS int), DATEADD(second, CAST({millisecondsParameter} / 1000 AS int), {dateExpression}))";

    private string Guarded(string assignments) =>
        "SET NOCOUNT ON;\n" + Now +
        $"UPDATE {QualifiedTable} SET {assignments}\n" +
        "WHERE [id] = @id AND [lock_token] = @lock_token AND [status] = 1;\n" +
        "SELECT @@ROWCOUNT;";

    private string GuardedBatch(string assignments, int items) =>
        "SET NOCOUNT ON;\n" + Now +
        $"UPDATE m SET {assignments}\n" +
        "OUTPUT inserted.[id], deleted.[lock_token]\n" +
        $"FROM {QualifiedTable} AS m WITH (ROWLOCK, FORCESEEK)\n" +
        "INNER JOIN (VALUES " + string.Join(", ", Enumerable.Range(0, items).Select(i => $"(@i{i}, @t{i})")) + ") AS a([id], [token])\n" +
        "    ON a.[id] = m.[id] AND m.[lock_token] = a.[token]\n" +
        "WHERE m.[status] = 1;";

    private string BulkRequeue(bool typeFilter) =>
        "SET NOCOUNT ON;\n" +
        "SET XACT_ABORT ON;\n" + Now +
        "DECLARE @_picked TABLE ([sequence] bigint NOT NULL PRIMARY KEY);\n" +
        "DECLARE @_rows int;\n" +
        "BEGIN TRANSACTION;\n" +
        "INSERT INTO @_picked ([sequence])\n" +
        "SELECT TOP (@max_count) [sequence]\n" +
        $"FROM {QualifiedTable} WITH (UPDLOCK, READPAST, ROWLOCK, INDEX([ix_{Table}_dead_lettered]))\n" +
        "WHERE [status] = 3" + (typeFilter ? " AND [message_type] = @message_type" : string.Empty) + "\n" +
        "ORDER BY [dead_lettered_at], [sequence];\n" +
        "UPDATE m SET [status] = 0, [available_at] = @_now, [attempt_count] = 0, [dead_lettered_at] = NULL, [lock_token] = NULL\n" +
        $"FROM {QualifiedTable} AS m WITH (ROWLOCK, READPAST, FORCESEEK)\n" +
        "INNER JOIN @_picked AS p ON p.[sequence] = m.[sequence]\n" +
        "WHERE m.[status] = 3;\n" +
        "SET @_rows = @@ROWCOUNT;\n" +
        "COMMIT TRANSACTION;\n" +
        "SELECT @_rows;";

    private string BuildInsert(int rowCount)
    {
        var sb = new StringBuilder(512 + (rowCount * 96));
        // No SET statements: this runs on the caller's connection, whose session options DbRelay must not change.
        sb.Append(Now);
        sb.Append("INSERT INTO ").Append(QualifiedTable)
          .Append(" ([id], [message_type], [destination], [payload], [content_type], [headers], [correlation_id], [causation_id], ")
          .Append("[status], [attempt_count], [created_at], [scheduled_at], [available_at])\n");
        sb.Append("SELECT v.[id], v.[mt], v.[de], v.[pl], v.[ct], v.[hd], v.[co], v.[ca], 0, 0, @_now, a.[at], a.[at]\nFROM (VALUES\n");
        for (var i = 0; i < rowCount; i++)
        {
            sb.Append(i == 0 ? "    " : ",\n    ")
              .Append('(').Append(i)
              .Append(", @id").Append(i)
              .Append(", @mt").Append(i)
              .Append(", @de").Append(i)
              .Append(", @pl").Append(i)
              .Append(", @ct").Append(i)
              .Append(", @hd").Append(i)
              .Append(", @co").Append(i)
              .Append(", @ca").Append(i)
              .Append(", @da").Append(i)
              .Append(", @dl").Append(i)
              .Append(')');
        }

        sb.Append("\n) AS v([n], [id], [mt], [de], [pl], [ct], [hd], [co], [ca], [da], [dl])\n");
        sb.Append("CROSS APPLY (SELECT COALESCE(v.[da], ").Append(AddMilliseconds("@_now", "v.[dl]")).Append(", @_now)) AS a([at]);");

        // Deliberately no ORDER BY v.[n]: it adds a Sort whose memory grant (about 4 MB for 200 rows, sized for the max columns)
        // made enqueues inside callers' transactions wait on RESOURCE_SEMAPHORE under load. Without it, rows of one call share
        // created_at and their relative IDENTITY order is not guaranteed.
        return sb.ToString();
    }

    private static string LoadScriptTemplate()
    {
        using var stream = typeof(SqlServerOutboxSql).Assembly.GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ScriptResourceName}' is missing from {typeof(SqlServerOutboxSql).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string[] SplitBatches(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        foreach (var line in script.Split('\n'))
        {
            if (line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase))
            {
                AddBatch(batches, current);
                continue;
            }

            current.Append(line).Append('\n');
        }

        AddBatch(batches, current);
        return [.. batches];

        static void AddBatch(List<string> batches, StringBuilder current)
        {
            var text = current.ToString();
            current.Clear();
            var hasCode = text.Split('\n').Any(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("--", StringComparison.Ordinal));
            if (hasCode)
            {
                batches.Add(text.Trim());
            }
        }
    }
}
