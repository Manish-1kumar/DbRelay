using System.Data.Common;
using Dapper;
using DbRelay.Oracle.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Extensions.DependencyInjection;
using static DbRelay.Oracle.Tests.OracleTestHelpers;

namespace DbRelay.Oracle.Tests;

/// <summary>Oracle-specific behaviour not covered by the provider-neutral conformance suite.</summary>
public sealed class OracleProviderTests(OracleFixture fixture)
{
    private const string AnyConnectionString = "User Id=x;Password=y;Data Source=localhost:1521/FREEPDB1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() => Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

    private static ServiceProvider Build(Action<DbRelayBuilder> useStorage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            useStorage(relay);
            relay.DisableDispatcher();
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private ServiceProvider BuildFor(string schema, string? table = null) =>
        Build(relay => relay.UseOracle(fixture.ConnectionString, o =>
        {
            o.Schema = schema;
            if (table is not null)
            {
                o.TableName = table;
            }
        }));

    private static OutboxRecord Record(Guid? id = null, byte[]? payload = null, string? destination = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        MessageType = "oracle.test",
        Payload = payload ?? "{}"u8.ToArray(),
        ContentType = "application/json",
        Destination = destination,
    };

    // ---- Registration (no database needed) ------------------------------------------------------------------------

    public static TheoryData<string?> HostileNames =>
    [
        "x; DROP TABLE y",
        "a\"b",
        new string('a', 64),
        string.Empty,
        " ",
        "1abc",
        "dbrelay.outbox",
        "naïve",
        "a-b",
        "a'b",
        "x\0y",
    ];

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Hostile_schema_names_are_rejected_at_registration(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UseOracle(AnyConnectionString, o => o.Schema = name)));
        Assert.Equal("OracleOutboxOptions.Schema", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Hostile_table_names_are_rejected_at_registration(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UseOracle(AnyConnectionString, o => o.TableName = name!)));
        Assert.Equal("OracleOutboxOptions.TableName", ex.ParamName);
    }

    [Fact]
    public void Reserved_table_name_and_invalid_timeout_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UseOracle(AnyConnectionString, o => o.TableName = "dbrelay_schema_version")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(relay => relay.UseOracle(AnyConnectionString, o => o.CommandTimeout = TimeSpan.Zero)));
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UseOracle(" ")));
    }

    [Fact]
    public void Names_are_upper_case_and_the_default_schema_is_the_connection_user()
    {
        var defaults = new OracleOutboxSql(null, "outbox_messages");
        Assert.Equal("\"OUTBOX_MESSAGES\"", defaults.Table);
        Assert.Equal("IX_OUTBOX_MESSAGES_CLAIM", defaults.ClaimIndexName);
        Assert.Equal("\"DBRELAY_SCHEMA_VERSION\"", defaults.VersionTable);

        var qualified = new OracleOutboxSql("billing", "Outbox");
        Assert.Equal("\"BILLING\".\"OUTBOX\"", qualified.Table);
    }

    [Fact]
    public void Long_table_names_get_distinct_names_within_the_identifier_limit()
    {
        var sql = new OracleOutboxSql("s", "t" + new string('x', 62));
        string[] names = [sql.PrimaryKeyName, sql.StatusConstraintName, sql.ClaimIndexName, sql.CleanupIndexName, sql.DeadIndexName];
        Assert.All(names, n => Assert.InRange(n.Length, 1, 128));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_script_has_its_placeholders_replaced_and_is_one_executable_block()
    {
        var sql = new OracleOutboxSql("billing", "outbox");
        Assert.DoesNotContain("{{", sql.CreateScript, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"BILLING\".\"OUTBOX\"", sql.CreateScript, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX \"BILLING\".\"IX_OUTBOX_CLAIM\"", sql.CreateScript, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", sql.CreateScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE", sql.CreateScript, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("/", sql.CreateScript.TrimEnd(), StringComparison.Ordinal);

        Assert.StartsWith("DECLARE", sql.CreateBlock, StringComparison.Ordinal);
        Assert.EndsWith("END;", sql.CreateBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", sql.CreateBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Insert_statements_respect_the_INSERT_ALL_column_limit()
    {
        var sql = new OracleOutboxSql(null, "outbox_messages");
        Assert.StartsWith("INSERT INTO", sql.GetInsert(1), StringComparison.Ordinal);
        var largest = sql.GetInsert(OracleOutboxSql.MaxInsertRowsPerStatement);
        Assert.StartsWith("INSERT ALL", largest, StringComparison.Ordinal);
        Assert.True(OracleOutboxSql.MaxInsertRowsPerStatement * 10 <= 999, "Oracle allows at most 999 columns across INSERT ALL clauses.");
        Assert.Throws<ArgumentOutOfRangeException>(() => sql.GetInsert(OracleOutboxSql.MaxInsertRowsPerStatement + 1));
    }

    // ---- Schema creation ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureCreated_is_safe_when_run_concurrently()
    {
        RequireDatabase();
        for (var round = 0; round < 3; round++)
        {
            var schema = NewSchemaName();
            await CreateSchemaAsync(fixture, schema);
            var providers = Enumerable.Range(0, 4).Select(_ => BuildFor(schema)).ToArray();
            try
            {
                using var start = new ManualResetEventSlim();
                var tasks = providers
                    .Select(sp => Task.Run(async () =>
                    {
                        start.Wait(Ct);
                        await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
                    }))
                    .ToArray();
                start.Set();
                await Task.WhenAll(tasks);

                await using var connection = await fixture.OpenAsync();
                var indexes = (await connection.QueryAsync<string>(
                    "SELECT INDEX_NAME FROM ALL_INDEXES WHERE OWNER = :Owner AND TABLE_NAME = 'OUTBOX_MESSAGES' ORDER BY INDEX_NAME",
                    new { Owner = schema.ToUpperInvariant() })).ToArray();
                Assert.Equal(["IX_OUTBOX_MESSAGES_CLAIM", "IX_OUTBOX_MESSAGES_CLEANUP", "IX_OUTBOX_MESSAGES_DEAD", "PK_OUTBOX_MESSAGES"], indexes);
                var version = await connection.ExecuteScalarAsync<int>(
                    $"SELECT VERSION FROM {Quote(schema)}.\"DBRELAY_SCHEMA_VERSION\" WHERE COMPONENT = 'outbox:OUTBOX_MESSAGES'");
                Assert.Equal(1, version);
            }
            finally
            {
                foreach (var sp in providers)
                {
                    await sp.DisposeAsync();
                }

                await DropSchemaAsync(fixture, schema);
            }
        }
    }

    [Fact]
    public async Task Two_outbox_tables_can_share_a_schema_including_63_character_names()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        var longTable = "t" + new string('x', 62);
        await CreateSchemaAsync(fixture, schema);
        await using var first = BuildFor(schema, "outbox_a");
        await using var second = BuildFor(schema, longTable);
        try
        {
            await first.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            await second.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);

            var id = await first.GetRequiredService<IOutbox>().EnqueueStandaloneAsync(new TestMessage("a"), cancellationToken: Ct);
            Assert.NotNull(await first.GetRequiredService<IOutboxAdmin>().GetAsync(id, Ct));
            Assert.Null(await second.GetRequiredService<IOutboxAdmin>().GetAsync(id, Ct));

            await using var connection = await fixture.OpenAsync();
            var longIndexes = await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ALL_INDEXES WHERE OWNER = :Owner AND TABLE_NAME = :Name",
                new { Owner = schema.ToUpperInvariant(), Name = longTable.ToUpperInvariant() });
            Assert.Equal(4, longIndexes); // primary key + claim + cleanup + dead
            var versions = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Quote(schema)}.\"DBRELAY_SCHEMA_VERSION\"");
            Assert.Equal(2, versions);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- Transactions and inserts ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Null_transaction_while_a_transaction_is_in_progress_joins_it()
    {
        // Documented provider-specific behaviour (as on PostgreSQL): ODP.NET runs every command in the connection's transaction, so
        // the row commits or rolls back with it.
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        await using var sp = BuildFor(schema);
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var outbox = sp.GetRequiredService<IOutbox>();
            var admin = sp.GetRequiredService<IOutboxAdmin>();
            await using var connection = await fixture.OpenAsync();

            Guid rolledBack;
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                rolledBack = await outbox.EnqueueAsync(connection, null, new TestMessage("rolled back"), cancellationToken: Ct);
                await transaction.RollbackAsync(Ct);
            }

            Guid committed;
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                committed = await outbox.EnqueueAsync(connection, null, new TestMessage("committed"), cancellationToken: Ct);
                await transaction.CommitAsync(Ct);
            }

            Assert.Null(await admin.GetAsync(rolledBack, Ct));
            Assert.NotNull(await admin.GetAsync(committed, Ct));

            // Several statements (more than one INSERT ALL) without a caller transaction, on a connection that has one: they join it.
            IReadOnlyList<Guid> many;
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                many = await outbox.EnqueueManyAsync(connection, null, Enumerable.Range(0, 200).Select(i => new TestMessage($"m{i}")), cancellationToken: Ct);
                await transaction.RollbackAsync(Ct);
            }

            Assert.Null(await admin.GetAsync(many[0], Ct));
            Assert.Null(await admin.GetAsync(many[^1], Ct));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Theory]
    [InlineData(10, 5)] // one INSERT ALL statement
    [InlineData(200, 150)] // three statements in a transaction DbRelay begins itself
    public async Task Multi_row_insert_without_a_transaction_is_all_or_nothing(int count, int duplicateAt)
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        await using var sp = BuildFor(schema);
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var store = sp.GetRequiredService<IOutboxStore>();
            var records = Enumerable.Range(0, count).Select(_ => Record()).ToList();
            records[duplicateAt] = Record(records[0].Id); // primary key violation part-way through

            await using (var connection = await fixture.OpenAsync())
            {
                await Assert.ThrowsAnyAsync<DbException>(() => store.InsertAsync(connection, null, records, Ct));
            }

            await Assert.ThrowsAnyAsync<DbException>(() => store.InsertStandaloneAsync(records, Ct));

            await using var check = await fixture.OpenAsync();
            Assert.Equal(0, await check.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Quote(schema)}.\"OUTBOX_MESSAGES\""));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task Empty_payload_is_stored_and_an_empty_destination_reads_back_as_null()
    {
        // Oracle stores '' as NULL in character columns and binds an empty byte array as NULL; DbRelay stores an empty BLOB.
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        await using var sp = BuildFor(schema);
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var record = Record(payload: [], destination: string.Empty);
            await sp.GetRequiredService<IOutboxStore>().InsertStandaloneAsync([record], Ct);

            var stored = await sp.GetRequiredService<IOutboxAdmin>().GetAsync(record.Id, Ct);
            Assert.NotNull(stored);
            Assert.Equal(0, stored.Payload.Length);
            Assert.Null(stored.Destination);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- Plans ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_cleanup_dead_letter_and_statistics_plans_use_the_function_based_indexes()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        await using var sp = BuildFor(schema);
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var sql = new OracleOutboxSql(schema, "outbox_messages");
            var table = sql.Table;
            await using var connection = await fixture.OpenAsync();

            // 20,000 rows: 2.5% due pending, 2.5% dead-lettered, 95% delivered, a realistic steady state.
            await connection.ExecuteAsync(
                $"""
                INSERT INTO {table} (ID, MESSAGE_TYPE, PAYLOAD, CONTENT_TYPE, STATUS, ATTEMPT_COUNT, SCHEDULED_AT, AVAILABLE_AT, DELIVERED_AT, DEAD_LETTERED_AT)
                SELECT SYS_GUID(), 'plan.test', TO_BLOB(HEXTORAW('7B7D')), 'application/json',
                       CASE WHEN MOD(LEVEL, 40) = 0 THEN 0 WHEN MOD(LEVEL, 40) = 1 THEN 3 ELSE 2 END, 1,
                       SYS_EXTRACT_UTC(SYSTIMESTAMP) - NUMTODSINTERVAL(LEVEL, 'SECOND'),
                       SYS_EXTRACT_UTC(SYSTIMESTAMP) - NUMTODSINTERVAL(LEVEL, 'SECOND'),
                       CASE WHEN MOD(LEVEL, 40) > 1 THEN SYS_EXTRACT_UTC(SYSTIMESTAMP) - NUMTODSINTERVAL(LEVEL, 'SECOND') END,
                       CASE WHEN MOD(LEVEL, 40) = 1 THEN SYS_EXTRACT_UTC(SYSTIMESTAMP) - NUMTODSINTERVAL(LEVEL, 'SECOND') END
                FROM DUAL CONNECT BY LEVEL <= 20000
                """);
            await connection.ExecuteAsync(
                "BEGIN DBMS_STATS.GATHER_TABLE_STATS(ownname => :Owner, tabname => 'OUTBOX_MESSAGES', cascade => TRUE); END;",
                new { Owner = schema.ToUpperInvariant() });

            // The exact cursor queries the PL/SQL blocks run, with their PL/SQL variables replaced by the database clock.
            var now = OracleOutboxSql.Now;
            var claim = await PlanAsync(connection, sql.ClaimCandidates(now));
            AssertUses(claim, sql.ClaimIndexName);
            Assert.False(claim.Contains("SORT ORDER BY", StringComparison.Ordinal), "The claim must stream rows in index order:\n" + claim);

            AssertUses(await PlanAsync(connection, sql.CleanupCandidates(now + " - INTERVAL '1' DAY")), sql.CleanupIndexName);
            AssertUses(await PlanAsync(connection, sql.DeadCandidates(filterByType: false)), sql.DeadIndexName);
            AssertUses(await PlanAsync(connection, sql.DeadCandidates(filterByType: true).Replace(":message_type", "'plan.test'", StringComparison.Ordinal)), sql.DeadIndexName);

            var statistics = await PlanAsync(connection, sql.Statistics);
            AssertUses(statistics, sql.ClaimIndexName);
            AssertUses(statistics, sql.CleanupIndexName);
            AssertUses(statistics, sql.DeadIndexName);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    private static void AssertUses(string plan, string index)
    {
        Assert.True(plan.Contains(index, StringComparison.Ordinal), $"Expected the plan to use {index}:\n{plan}");
        Assert.False(plan.Contains("TABLE ACCESS FULL", StringComparison.Ordinal), "The plan scans the whole table:\n" + plan);
    }

    /// <summary>Returns the plan of <paramref name="query"/> as "OPERATION OPTIONS OBJECT" lines.</summary>
    private static async Task<string> PlanAsync(DbConnection connection, string query)
    {
        var id = "dbrelay_" + Guid.NewGuid().ToString("N")[..12];
        await connection.ExecuteAsync($"EXPLAIN PLAN SET STATEMENT_ID = '{id}' FOR {query}");
        var lines = await connection.QueryAsync<string>(
            "SELECT OPERATION || ' ' || OPTIONS || ' ' || OBJECT_NAME FROM PLAN_TABLE WHERE STATEMENT_ID = :Id ORDER BY ID",
            new { Id = id });
        return string.Join('\n', lines);
    }
}
