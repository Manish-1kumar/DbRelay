using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using DbRelay.Storage;

namespace DbRelay.Oracle.Internal;

/// <summary>
/// The SQL text of the Oracle provider, built once from validated identifiers. Names are upper-cased and quoted; every value is
/// a <c>:name</c> parameter. All times come from the database clock (<c>SYS_EXTRACT_UTC(SYSTIMESTAMP)</c>), stored as UTC.
/// </summary>
internal sealed class OracleOutboxSql
{
    internal const string ScriptResourceName = "DbRelay.Oracle.Scripts.001_create_outbox.sql";

    /// <summary>The version table shared by every outbox table in a schema. A prefixed name, because it shares the application's schema.</summary>
    internal const string VersionTableName = "DBRELAY_SCHEMA_VERSION";

    /// <summary>
    /// Rows per multi-row insert. Oracle allows at most 999 columns across all INTO clauses of an <c>INSERT ALL</c>, and each row
    /// sets 10 columns.
    /// </summary>
    internal const int MaxInsertRowsPerStatement = 90;

    /// <summary>Upper bound for rows deleted or requeued by one statement.</summary>
    internal const int MaxRowsPerBulkStatement = 1000;

    /// <summary>Hexadecimal characters per GUID in the ID lists passed to and from PL/SQL (16 bytes).</summary>
    internal const int HexPerId = 32;

    private const int MaxIdentifierLength = 128;

    internal const string Now = "SYS_EXTRACT_UTC(SYSTIMESTAMP)";

    /// <summary>The claim index's leading expression. Statements must use exactly this text so the optimizer matches the index.</summary>
    internal const string ClaimKey = "(CASE WHEN STATUS IN (0, 1) THEN AVAILABLE_AT END)";

    internal const string ClaimStatusKey = "(CASE WHEN STATUS IN (0, 1) THEN STATUS END)";

    internal const string CleanupKey = "(CASE WHEN STATUS = 2 THEN DELIVERED_AT END)";

    internal const string DeadKey = "(CASE WHEN STATUS = 3 THEN DEAD_LETTERED_AT END)";

    /// <summary>Columns read into an <c>OutboxMessageDetails</c>, in ordinal order (see <c>OracleOutboxStore.ReadDetails</c>).</summary>
    internal static readonly string[] DetailColumns =
    [
        "ID", "MESSAGE_TYPE", "DESTINATION", "PAYLOAD", "CONTENT_TYPE", "HEADERS", "CORRELATION_ID", "CAUSATION_ID", "STATUS",
        "ATTEMPT_COUNT", "CREATED_AT", "SCHEDULED_AT", "AVAILABLE_AT", "LOCKED_BY", "LAST_ATTEMPT_AT", "DELIVERED_AT",
        "DEAD_LETTERED_AT", "LAST_ERROR",
    ];

    private readonly ConcurrentDictionary<int, string> _inserts = new();
    private readonly ConcurrentDictionary<int, string> _claimedRows = new();
    private readonly string _columns;

    public OracleOutboxSql(string? schema, string tableName)
    {
        Schema = schema?.ToUpperInvariant();
        TableName = tableName.ToUpperInvariant();
        Table = Qualify(TableName);
        VersionTable = Qualify(VersionTableName);
        PrimaryKeyName = BoundedName("PK_", TableName, string.Empty);
        StatusConstraintName = BoundedName("CK_", TableName, "_STATUS");
        IdConstraintName = BoundedName("CK_", TableName, "_ID");
        ClaimIndexName = BoundedName("IX_", TableName, "_CLAIM");
        CleanupIndexName = BoundedName("IX_", TableName, "_CLEANUP");
        DeadIndexName = BoundedName("IX_", TableName, "_DEAD");

        _columns = string.Join(", ", DetailColumns);
        var table = Table;

        CreateScript = LoadScript()
            .Replace("{{table}}", table, StringComparison.Ordinal)
            .Replace("{{table_name}}", TableName, StringComparison.Ordinal)
            .Replace("{{version_table}}", VersionTable, StringComparison.Ordinal)
            .Replace("{{pk}}", PrimaryKeyName, StringComparison.Ordinal)
            .Replace("{{ck_status}}", StatusConstraintName, StringComparison.Ordinal)
            .Replace("{{ck_id}}", IdConstraintName, StringComparison.Ordinal)
            .Replace("{{ix_claim}}", Qualify(ClaimIndexName), StringComparison.Ordinal)
            .Replace("{{ix_cleanup}}", Qualify(CleanupIndexName), StringComparison.Ordinal)
            .Replace("{{ix_dead}}", Qualify(DeadIndexName), StringComparison.Ordinal);
        CreateBlock = ToExecutableBlock(CreateScript);

        // One PL/SQL block, autocommitted as one statement. FOR UPDATE SKIP LOCKED locks rows as they are fetched, one at a time
        // (an explicit FETCH, not a cursor FOR loop, which PL/SQL would bulk-fetch 100 rows at a time and so lock rows it does not
        // claim). FETCH FIRST and ROWNUM cannot be combined with SKIP LOCKED: they limit the rows before locked ones are skipped.
        // ADR 0004 payload budget: a row is claimed only while the payloads claimed before it total less than :max_payload_bytes,
        // so the first row is always claimed. :claimed_ids returns the claimed IDs in claim order (32 hex digits per ID); the store
        // then reads those rows with GetClaimedRows (OR-11). :pause_ms is a test seam (zero in production): it holds the cursor
        // open between choosing the candidates and locking them, so a test can change a row in that gap (OR-01). DBMS_SESSION is
        // reached through EXECUTE IMMEDIATE, so the block compiles even where its EXECUTE privilege has been revoked.
        Claim =
            $"""
            DECLARE
                v_now   TIMESTAMP(6) := {Now};
                v_until TIMESTAMP(6);
                v_bytes NUMBER := 0;
                v_count PLS_INTEGER := 0;
                v_ids   VARCHAR2(32767);
                v_rid   ROWID;
                v_id    RAW(16);
                v_size  NUMBER;
                CURSOR c_due IS
                    {ClaimCandidates("v_now")};
            BEGIN
                v_until := v_now + NUMTODSINTERVAL(:lease_ms / 1000, 'SECOND');
                OPEN c_due;
                IF :pause_ms > 0 THEN
                    EXECUTE IMMEDIATE 'BEGIN DBMS_SESSION.SLEEP(:s); END;' USING :pause_ms / 1000;
                END IF;
                LOOP
                    FETCH c_due INTO v_rid, v_id, v_size;
                    EXIT WHEN c_due%NOTFOUND;
                    EXIT WHEN v_count > 0 AND v_bytes >= :max_payload_bytes;
                    -- OR-01: the cursor chose this row from its read snapshot, but locked the current version at fetch time. Re-check
                    -- the claim condition against that current version, so a row another session claimed or acknowledged in between is
                    -- never claimed twice, and count only rows actually updated.
                    UPDATE {table}
                       SET STATUS = 1, LOCK_TOKEN = :lock_token, LOCKED_BY = :worker_id, ATTEMPT_COUNT = ATTEMPT_COUNT + 1,
                           LAST_ATTEMPT_AT = v_now, AVAILABLE_AT = v_until
                     WHERE ROWID = v_rid AND STATUS IN (0, 1) AND AVAILABLE_AT <= v_now;
                    IF SQL%ROWCOUNT = 1 THEN
                        v_count := v_count + 1;
                        v_bytes := v_bytes + v_size;
                        v_ids := v_ids || RAWTOHEX(v_id);
                    END IF;
                    EXIT WHEN v_count >= :batch_size;
                END LOOP;
                CLOSE c_due;
                :claimed_ids := v_ids;
            END;
            """;

        MarkDelivered =
            $"UPDATE {table} SET STATUS = 2, DELIVERED_AT = {Now}, LOCK_TOKEN = NULL WHERE ID = :id AND LOCK_TOKEN = :lock_token";

        ScheduleRetry =
            $"UPDATE {table} SET STATUS = 0, AVAILABLE_AT = {Now} + NUMTODSINTERVAL(:delay_ms / 1000, 'SECOND'), LAST_ERROR = :error, " +
            "LOCK_TOKEN = NULL WHERE ID = :id AND LOCK_TOKEN = :lock_token";

        MarkDeadLettered =
            $"UPDATE {table} SET STATUS = 3, DEAD_LETTERED_AT = {Now}, LAST_ERROR = :error, LOCK_TOKEN = NULL " +
            "WHERE ID = :id AND LOCK_TOKEN = :lock_token";

        Release =
            $"UPDATE {table} SET STATUS = 0, AVAILABLE_AT = {Now}, ATTEMPT_COUNT = GREATEST(ATTEMPT_COUNT - 1, 0), " +
            "LOCK_TOKEN = NULL, LOCKED_BY = NULL WHERE ID = :id AND LOCK_TOKEN = :lock_token";

        MarkDeliveredBatch = GuardedBatch(table, "STATUS = 2, DELIVERED_AT = v_now, LOCK_TOKEN = NULL");
        ReleaseBatch = GuardedBatch(
            table, "STATUS = 0, AVAILABLE_AT = v_now, ATTEMPT_COUNT = GREATEST(ATTEMPT_COUNT - 1, 0), LOCK_TOKEN = NULL, LOCKED_BY = NULL");

        DeleteDelivered =
            $"""
            DECLARE
                TYPE t_rowids IS TABLE OF ROWID INDEX BY PLS_INTEGER;
                v_rowids t_rowids;
                v_cutoff TIMESTAMP(6) := {Now} - NUMTODSINTERVAL(:older_than_ms / 1000, 'SECOND');
                CURSOR c_old IS
                    {CleanupCandidates("v_cutoff")};
            BEGIN
                OPEN c_old;
                FETCH c_old BULK COLLECT INTO v_rowids LIMIT :max_count;
                CLOSE c_old;
                FORALL i IN 1 .. v_rowids.COUNT
                    DELETE FROM {table} WHERE ROWID = v_rowids(i);
                :affected := v_rowids.COUNT;
            END;
            """;

        SelectById = $"SELECT {_columns} FROM {table} WHERE ID = :id";

        // Each count reads only its function-based index (claim: STATUS IN (0, 1); cleanup: STATUS = 2; dead: STATUS = 3), hinted, so
        // no count scans the table whatever the optimizer statistics say. Pending and processing come from one scan of the claim
        // index: it holds exactly those rows, and its second key is the status (0 or 1), so its sum is the processing count.
        // (Comparing that key to a constant does not work: the optimizer simplifies "CASE ... END = 0" to "STATUS = 0", which no
        // longer matches the index, and scans the table.)
        Statistics =
            $"""
            SELECT
                c.total - c.processing,
                c.processing,
                (SELECT {Hint("INDEX_FFS", CleanupIndexName)} COUNT(*) FROM {table} t WHERE {CleanupKey} IS NOT NULL),
                (SELECT {Hint("INDEX_FFS", DeadIndexName)} COUNT(*) FROM {table} t WHERE {DeadKey} IS NOT NULL),
                (SELECT EXTRACT(DAY FROM age) * 86400000 + EXTRACT(HOUR FROM age) * 3600000 + EXTRACT(MINUTE FROM age) * 60000
                        + ROUND(EXTRACT(SECOND FROM age) * 1000)
                 FROM (SELECT {Hint("INDEX", ClaimIndexName)} {Now} - MIN({ClaimKey}) AS age FROM {table} t WHERE {ClaimKey} <= {Now}))
            FROM (SELECT {Hint("INDEX_FFS", ClaimIndexName)} COUNT(*) AS total, NVL(SUM({ClaimStatusKey}), 0) AS processing
                  FROM {table} t WHERE {ClaimKey} IS NOT NULL) c
            """;

        Requeue =
            $"UPDATE {table} SET STATUS = 0, AVAILABLE_AT = {Now}, ATTEMPT_COUNT = 0, DEAD_LETTERED_AT = NULL, LOCK_TOKEN = NULL " +
            "WHERE ID = :id AND STATUS = 3";

        RequeueDeadLettered = RequeueMany(table, filterByType: false);
        RequeueDeadLetteredOfType = RequeueMany(table, filterByType: true);

        // OR-12: the table must answer, and its three function-based indexes must exist and be usable. Oracle ignores a hint whose
        // index is missing or unusable, so DbRelay would otherwise keep working with full table scans, slower by orders of magnitude.
        var owner = Schema is null ? "SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')" : "'" + Schema + "'";
        HealthCheck =
            $"""
            SELECT (SELECT COUNT(*) FROM {table} WHERE ROWNUM = 1),
                   (SELECT COUNT(*) FROM ALL_INDEXES
                    WHERE OWNER = {owner} AND TABLE_NAME = '{TableName}'
                      AND INDEX_NAME IN ('{ClaimIndexName}', '{CleanupIndexName}', '{DeadIndexName}')
                      AND STATUS = 'VALID' AND (FUNCIDX_STATUS IS NULL OR FUNCIDX_STATUS = 'ENABLED'))
            FROM DUAL
            """;
    }

    public string? Schema { get; }

    public string TableName { get; }

    /// <summary>The quoted, optionally schema-qualified outbox table.</summary>
    public string Table { get; }

    public string VersionTable { get; }

    public string PrimaryKeyName { get; }

    public string StatusConstraintName { get; }

    public string IdConstraintName { get; }

    public string ClaimIndexName { get; }

    public string CleanupIndexName { get; }

    public string DeadIndexName { get; }

    /// <summary>The script for people and migration tools (ends with a SQL*Plus <c>/</c>).</summary>
    public string CreateScript { get; }

    /// <summary>The script as one executable PL/SQL block: from <c>DECLARE</c> to the final <c>END;</c>, so the header comments and the trailing <c>/</c> are removed, with LF line endings.</summary>
    public string CreateBlock { get; }

    public string Claim { get; }

    public string MarkDelivered { get; }

    public string ScheduleRetry { get; }

    public string MarkDeadLettered { get; }

    public string Release { get; }

    public string MarkDeliveredBatch { get; }

    public string ReleaseBatch { get; }

    public string DeleteDelivered { get; }

    public string SelectById { get; }

    public string Statistics { get; }

    public string Requeue { get; }

    public string RequeueDeadLettered { get; }

    public string RequeueDeadLetteredOfType { get; }

    public string HealthCheck { get; }

    /// <summary>
    /// The statement inserting <paramref name="rows"/> rows (1 to <see cref="OutboxLimits.MaxEnqueueBatchSize"/>), with parameters
    /// <c>:id{i}</c>, <c>:mt{i}</c>, <c>:de{i}</c>, <c>:pl{i}</c>, <c>:ct{i}</c>, <c>:hd{i}</c>, <c>:co{i}</c>, <c>:ca{i}</c>,
    /// <c>:da{i}</c> and <c>:dl{i}</c>, always stored all-or-nothing:
    /// <list type="bullet">
    /// <item>up to <see cref="MaxInsertRowsPerStatement"/> rows: one <c>INSERT ALL</c> statement (cached by row count);</item>
    /// <item>more: one anonymous PL/SQL block of <c>INSERT ALL</c> statements. Oracle runs a block called from the client as one
    /// statement, so an error rolls back everything it inserted (OR-04). DbRelay never needs to begin a transaction on the caller's
    /// connection, and a failed call never leaves part of its rows in the caller's transaction.</item>
    /// </list>
    /// </summary>
    public string GetInsert(int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, OutboxLimits.MaxEnqueueBatchSize);
        if (rows <= MaxInsertRowsPerStatement)
        {
            return _inserts.GetOrAdd(rows, static (count, sql) => sql.BuildInsertAll(0, count), this);
        }

        // Not cached: a block for 1,000 rows is about 300 KB of text, and its parse cost is small next to sending the rows.
        var block = new StringBuilder("BEGIN\n");
        for (var start = 0; start < rows; start += MaxInsertRowsPerStatement)
        {
            block.Append(BuildInsertAll(start, Math.Min(MaxInsertRowsPerStatement, rows - start))).Append(";\n");
        }

        return block.Append("END;").ToString();
    }

    /// <summary>Claimed rows are read in buckets of these sizes, padded by repeating the last ID, so few statement texts exist.</summary>
    internal static readonly int[] ClaimedRowBuckets = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, OutboxLimits.MaxClaimBatchSize];

    /// <summary>
    /// Reads the rows a claim just leased, by primary key, with parameters <c>:i0</c> to <c>:i{bucket - 1}</c>. A plain query on a
    /// DbRelay command, so the payloads and texts come back inline with the rows (<c>InitialLOBFetchSize = -1</c>), not one round trip
    /// per LOB (OR-11).
    /// </summary>
    public string GetClaimedRows(int bucket) =>
        _claimedRows.GetOrAdd(
            bucket,
            static (size, sql) =>
                $"SELECT {sql._columns} FROM {sql.Table} WHERE ID IN ({string.Join(", ", Enumerable.Range(0, size).Select(i => ":i" + i.ToString(CultureInfo.InvariantCulture)))})",
            this);

    /// <summary>The smallest bucket of <see cref="ClaimedRowBuckets"/> that holds <paramref name="count"/> IDs.</summary>
    internal static int ClaimedRowBucketFor(int count) => ClaimedRowBuckets.First(b => b >= count);

    /// <summary>
    /// The admin page query: the narrow keys are filtered, sorted and paged first, then the page's full rows are fetched by primary
    /// key (as the other providers do, CR-21).
    /// </summary>
    public string GetQuery(bool byStatus, bool byType, bool createdAfter, bool createdBefore)
    {
        var keys = new StringBuilder($"SELECT ID, CREATED_AT FROM {Table}");
        var separator = " WHERE ";
        void Filter(bool apply, string condition)
        {
            if (apply)
            {
                keys.Append(separator).Append(condition);
                separator = " AND ";
            }
        }

        Filter(byStatus, "STATUS = :status");
        Filter(byType, "MESSAGE_TYPE = :message_type");
        Filter(createdAfter, "CREATED_AT >= :created_after");
        Filter(createdBefore, "CREATED_AT < :created_before");
        keys.Append(" ORDER BY CREATED_AT, ID OFFSET :skip ROWS FETCH NEXT :take ROWS ONLY");

        var columns = string.Join(", ", DetailColumns.Select(c => "m." + c));
        return $"SELECT {columns} FROM ({keys}) k JOIN {Table} m ON m.ID = k.ID ORDER BY k.CREATED_AT, k.ID";
    }

    private string Qualify(string name) => Schema is null ? Quote(name) : Quote(Schema) + "." + Quote(name);

    private static string Quote(string identifier) => "\"" + identifier + "\"";

    /// <summary>One statement inserting rows <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> - 1.</summary>
    private string BuildInsertAll(int start, int count)
    {
        const string Columns = "(ID, MESSAGE_TYPE, DESTINATION, PAYLOAD, CONTENT_TYPE, HEADERS, CORRELATION_ID, CAUSATION_ID, SCHEDULED_AT, AVAILABLE_AT)";
        static string Values(int i)
        {
            var due = $"COALESCE(:da{i}, {Now} + NUMTODSINTERVAL(NVL(:dl{i}, 0) / 1000, 'SECOND'))";

            // An empty payload binds as NULL in Oracle; store an empty BLOB instead.
            return $"VALUES (:id{i}, :mt{i}, :de{i}, NVL(:pl{i}, EMPTY_BLOB()), :ct{i}, :hd{i}, :co{i}, :ca{i}, {due}, {due})";
        }

        if (count == 1)
        {
            return $"INSERT INTO {Table} {Columns} {Values(start)}";
        }

        var sql = new StringBuilder("INSERT ALL");
        for (var i = start; i < start + count; i++)
        {
            sql.Append('\n').Append("  INTO ").Append(Table).Append(' ').Append(Columns).Append(' ').Append(Values(i));
        }

        return sql.Append("\nSELECT 1 FROM DUAL").ToString();
    }

    /// <summary>
    /// ADR 0003 batch variant. The IDs and lock tokens arrive as two strings of 32 hex digits per item, in the canonical order the
    /// store chose. Each row is still guarded by its own token, and <c>:updated</c> returns one digit per item: 1 if it changed.
    /// </summary>
    private static string GuardedBatch(string table, string set) =>
        $"""
        DECLARE
            TYPE t_raw IS TABLE OF RAW(16) INDEX BY PLS_INTEGER;
            v_ids  t_raw;
            v_toks t_raw;
            v_n    PLS_INTEGER := LENGTH(:ids) / {HexPerId};
            v_now  TIMESTAMP(6) := {Now};
            v_done VARCHAR2(1000);
        BEGIN
            FOR i IN 1 .. v_n LOOP
                v_ids(i)  := HEXTORAW(SUBSTR(:ids, (i - 1) * {HexPerId} + 1, {HexPerId}));
                v_toks(i) := HEXTORAW(SUBSTR(:tokens, (i - 1) * {HexPerId} + 1, {HexPerId}));
            END LOOP;
            FORALL i IN 1 .. v_n
                UPDATE {table} SET {set} WHERE ID = v_ids(i) AND LOCK_TOKEN = v_toks(i);
            FOR i IN 1 .. v_n LOOP
                v_done := v_done || TO_CHAR(SQL%BULK_ROWCOUNT(i));
            END LOOP;
            :updated := v_done;
        END;
        """;

    /// <summary>
    /// The claim cursor: due pending and leased rows in AVAILABLE_AT order, hinted to the claim index so a large table is never
    /// scanned and sorted. Oracle ignores a hint whose index does not exist, so a dropped index slows the claim but cannot break it.
    /// </summary>
    internal string ClaimCandidates(string now) =>
        $"SELECT {Hint("INDEX", ClaimIndexName)} ROWID, ID, NVL(DBMS_LOB.GETLENGTH(PAYLOAD), 0) FROM {Table} t " +
        $"WHERE {ClaimKey} <= {now} ORDER BY {ClaimKey} FOR UPDATE SKIP LOCKED";

    /// <summary>The retention cleanup cursor: delivered rows older than <paramref name="cutoff"/>, oldest first.</summary>
    internal string CleanupCandidates(string cutoff) =>
        $"SELECT {Hint("INDEX", CleanupIndexName)} ROWID FROM {Table} t WHERE {CleanupKey} < {cutoff} " +
        $"ORDER BY {CleanupKey} FOR UPDATE SKIP LOCKED";

    /// <summary>The dead-letter requeue cursor, oldest first.</summary>
    internal string DeadCandidates(bool filterByType) =>
        $"SELECT {Hint("INDEX", DeadIndexName)} ROWID FROM {Table} t WHERE {DeadKey} IS NOT NULL" +
        (filterByType ? " AND MESSAGE_TYPE = :message_type" : string.Empty) +
        $" ORDER BY {DeadKey} FOR UPDATE SKIP LOCKED";

    /// <summary>An optimizer hint for table alias <c>t</c>. Index names are upper-case letters, digits and underscores.</summary>
    private static string Hint(string hint, string index) => $"/*+ {hint}(t {index}) */";

    private string RequeueMany(string table, bool filterByType) =>
        $"""
        DECLARE
            TYPE t_rowids IS TABLE OF ROWID INDEX BY PLS_INTEGER;
            v_rowids t_rowids;
            v_now    TIMESTAMP(6) := {Now};
            CURSOR c_dead IS
                {DeadCandidates(filterByType)};
        BEGIN
            OPEN c_dead;
            FETCH c_dead BULK COLLECT INTO v_rowids LIMIT :max_count;
            CLOSE c_dead;
            FORALL i IN 1 .. v_rowids.COUNT
                UPDATE {table}
                   SET STATUS = 0, AVAILABLE_AT = v_now, ATTEMPT_COUNT = 0, DEAD_LETTERED_AT = NULL, LOCK_TOKEN = NULL
                 WHERE ROWID = v_rowids(i) AND STATUS = 3;
            :affected := SQL%ROWCOUNT;
        END;
        """;

    /// <summary>
    /// Builds <c>prefix + table + suffix</c>, shortened deterministically with a hash of the full table name when it would exceed
    /// Oracle's 128-byte identifier limit, so the names of two indexes can never collide.
    /// </summary>
    private static string BoundedName(string prefix, string table, string suffix)
    {
        var name = prefix + table + suffix;
        if (name.Length <= MaxIdentifierLength)
        {
            return name;
        }

        var hash = "_" + Fnv1a(table).ToString("X8", CultureInfo.InvariantCulture);
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

    /// <summary>The executable block: from <c>DECLARE</c> to the final <c>END;</c>, without the SQL*Plus terminator.</summary>
    private static string ToExecutableBlock(string script)
    {
        var text = script.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = text.IndexOf("\nDECLARE", StringComparison.Ordinal);
        var end = text.LastIndexOf("END;", StringComparison.Ordinal);
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException($"The embedded resource '{ScriptResourceName}' is not a single PL/SQL block.");
        }

        return text[(start + 1)..(end + "END;".Length)];
    }

    private static string LoadScript()
    {
        using var stream = typeof(OracleOutboxSql).Assembly.GetManifestResourceStream(ScriptResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{ScriptResourceName}' is missing from {typeof(OracleOutboxSql).Assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
