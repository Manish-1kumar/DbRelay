using Dapper;
using DbRelay.PostgreSql.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbRelay.PostgreSql.Tests;

/// <summary>PostgreSQL-specific behaviour not covered by the provider-neutral conformance suite.</summary>
public sealed class PostgreSqlProviderTests(PostgreSqlFixture fixture)
{
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

    // ---- Identifier validation (no database needed) -----------------------------------------------------------------

    public static TheoryData<string?> HostileNames =>
    [
        "x; DROP TABLE y",
        "a\"b",
        new string('a', 64),
        string.Empty,
        " ",
        null!,
        "1abc",
        "dbrelay.outbox",
        "naïve",
        "a-b",
        "a b",
        "a'b",
        "x\0y",
    ];

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Hostile_schema_names_are_rejected_at_registration(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql("Host=localhost", pg => pg.Schema = name!)));
        Assert.Equal(nameof(PostgreSqlOutboxOptions.Schema), ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Hostile_table_names_are_rejected_at_registration(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql("Host=localhost", pg => pg.TableName = name!)));
        Assert.Equal(nameof(PostgreSqlOutboxOptions.TableName), ex.ParamName);
    }

    [Fact]
    public void Hostile_names_are_rejected_for_every_overload()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost");
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql(dataSource, pg => pg.Schema = "x; DROP TABLE y")));
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql(_ => dataSource, pg => pg.TableName = "a\"b")));
    }

    [Fact]
    public void Reserved_table_name_and_invalid_timeout_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql("Host=localhost", pg => pg.TableName = "schema_version")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(relay => relay.UsePostgreSql("Host=localhost", pg => pg.CommandTimeout = TimeSpan.Zero)));
    }

    [Fact]
    public void Malformed_connection_string_is_rejected_without_echoing_it()
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UsePostgreSql("Host=localhost;Password=s3cr3t;Bogus Keyword=1")));
        Assert.DoesNotContain("s3cr3t", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_table_names_get_distinct_index_names_within_the_identifier_limit()
    {
        var table = "t" + new string('x', 62);
        var settings = PostgreSqlSettings.Create(pg => pg.TableName = table);
        string[] names = [settings.ClaimIndexName, settings.CleanupIndexName, settings.DeadIndexName, settings.StatusConstraintName];
        Assert.All(names, n => Assert.InRange(n.Length, 1, 63));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());

        var defaults = PostgreSqlSettings.Create(null);
        Assert.Equal("ix_outbox_messages_claim", defaults.ClaimIndexName);
        Assert.Equal("\"dbrelay\".\"outbox_messages\"", defaults.Table);
    }

    [Fact]
    public void Create_script_matches_embedded_resource_with_placeholders_replaced()
    {
        var script = PostgreSqlSettings.Create(pg => { pg.Schema = "Billing"; pg.TableName = "outbox"; }).CreateScript;
        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"Billing\".\"outbox\"", script, StringComparison.Ordinal);
        Assert.Contains("\"ix_outbox_claim\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", script, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Schema creation ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureCreated_is_safe_when_run_concurrently()
    {
        RequireDatabase();
        for (var round = 0; round < 3; round++)
        {
            var schema = PostgreSqlTestHelpers.NewSchemaName();
            var providers = Enumerable.Range(0, 4)
                .Select(_ => Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => pg.Schema = schema)))
                .ToArray();
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

                await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
                var indexes = (await connection.QueryAsync<string>(
                    "SELECT indexname FROM pg_indexes WHERE schemaname = @schema AND tablename = 'outbox_messages' ORDER BY indexname",
                    new { schema })).ToArray();
                Assert.Equal(["ix_outbox_messages_claim", "ix_outbox_messages_cleanup", "ix_outbox_messages_dead", "outbox_messages_pkey"], indexes);
                var version = await connection.ExecuteScalarAsync<int>(
                    $"SELECT version FROM \"{schema}\".schema_version WHERE component = 'outbox:outbox_messages'");
                Assert.Equal(1, version);
            }
            finally
            {
                foreach (var sp in providers)
                {
                    await sp.DisposeAsync();
                }

                await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
            }
        }
    }

    [Fact]
    public async Task Two_outbox_tables_can_share_a_schema_including_63_character_names()
    {
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        var longTable = "t" + new string('x', 62);
        await using var first = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => { pg.Schema = schema; pg.TableName = "outbox_a"; }));
        await using var second = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => { pg.Schema = schema; pg.TableName = longTable; }));
        try
        {
            await first.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            await second.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);

            var id = await first.GetRequiredService<IOutbox>().EnqueueStandaloneAsync(new TestMessage("a"), cancellationToken: Ct);
            Assert.NotNull(await first.GetRequiredService<IOutboxAdmin>().GetAsync(id, Ct));
            Assert.Null(await second.GetRequiredService<IOutboxAdmin>().GetAsync(id, Ct));

            await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
            var longIndexes = await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM pg_indexes WHERE schemaname = @schema AND tablename = @longTable", new { schema, longTable });
            Assert.Equal(4, longIndexes); // primary key + claim + cleanup + dead (no truncation collisions)
            var versions = await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM \"{schema}\".schema_version");
            Assert.Equal(2, versions);
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    [Fact]
    public async Task Null_transaction_while_a_transaction_is_in_progress_joins_it()
    {
        // CR-11 / T-05: documented provider-specific behaviour. Npgsql runs every command in the connection's current transaction,
        // so the row commits or rolls back with it (SQL Server instead rejects the call; see the SQL Server provider tests).
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        await using var sp = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => pg.Schema = schema));
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var outbox = sp.GetRequiredService<IOutbox>();
            var admin = sp.GetRequiredService<IOutboxAdmin>();
            await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);

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
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    // ---- Data source ownership ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Caller_owned_data_source_is_not_disposed_with_the_service_provider()
    {
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        await using var dataSource = NpgsqlDataSource.Create(fixture.ConnectionString);
        await using var fromFactory = NpgsqlDataSource.Create(fixture.ConnectionString);
        try
        {
            var direct = Build(relay => relay.UsePostgreSql(dataSource, pg => pg.Schema = schema));
            await direct.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            await direct.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct);
            await direct.DisposeAsync();

            var factory = Build(relay => relay.UsePostgreSql(_ => fromFactory, pg => pg.Schema = schema));
            await factory.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct);
            factory.Dispose(); // synchronous disposal path

            await using (var connection = await dataSource.OpenConnectionAsync(Ct))
            {
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT 1"));
            }

            await using (var connection = await fromFactory.OpenConnectionAsync(Ct))
            {
                Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT 1"));
            }
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    [Fact]
    public async Task Owned_data_source_is_disposed_with_the_service_provider()
    {
        RequireDatabase();
        var asyncProvider = Build(relay => relay.UsePostgreSql(fixture.ConnectionString));
        var asyncHolder = asyncProvider.GetRequiredService<PostgreSqlDataSourceHolder>();
        Assert.True(asyncHolder.OwnsDataSource);
        await using (await asyncHolder.DataSource.OpenConnectionAsync(Ct))
        {
        }

        await asyncProvider.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await asyncHolder.DataSource.OpenConnectionAsync(Ct));

        var syncProvider = Build(relay => relay.UsePostgreSql(fixture.ConnectionString));
        var syncHolder = syncProvider.GetRequiredService<PostgreSqlDataSourceHolder>();
        syncProvider.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await syncHolder.DataSource.OpenConnectionAsync(Ct));
    }

    // ---- Storage behaviour --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Insert_handles_the_maximum_batch_and_non_utc_deliver_at()
    {
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        await using var sp = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => pg.Schema = schema));
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var outbox = sp.GetRequiredService<IOutbox>();
            var admin = sp.GetRequiredService<IOutboxAdmin>();

            await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                var ids = await outbox.EnqueueManyAsync(
                    transaction, Enumerable.Range(0, OutboxLimits.MaxEnqueueBatchSize).Select(i => new TestMessage($"b{i}")), Ct);
                Assert.Equal(OutboxLimits.MaxEnqueueBatchSize, ids.Count);
                await transaction.CommitAsync(Ct);
            }

            Assert.Equal(OutboxLimits.MaxEnqueueBatchSize, (await admin.GetStatisticsAsync(Ct)).Pending);

            var deliverAt = new DateTimeOffset(2031, 5, 6, 12, 30, 15, TimeSpan.FromHours(5.5));
            var id = await outbox.EnqueueStandaloneAsync(new TestMessage("ist"), new EnqueueOptions { DeliverAt = deliverAt }, Ct);
            var stored = await admin.GetAsync(id, Ct);
            Assert.NotNull(stored);
            Assert.Equal(TimeSpan.Zero, stored.AvailableAt.Offset);
            Assert.Equal(deliverAt.UtcDateTime, stored.AvailableAt.UtcDateTime);
            Assert.Equal(deliverAt.UtcDateTime, stored.ScheduledAt.UtcDateTime);
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    [Fact]
    public async Task Insert_rejects_a_non_npgsql_transaction_type()
    {
        RequireDatabase();
        await using var sp = Build(relay => relay.UsePostgreSql(fixture.DataSource));
        var store = sp.GetRequiredService<IOutboxStore>();
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        using var foreign = new FakeDbConnection();
        var ex = Assert.Throws<ArgumentException>(() => store.ValidateCallerResources(foreign, null));
        Assert.Contains("NpgsqlConnection", ex.Message, StringComparison.Ordinal);
        store.ValidateCallerResources(connection, null);
    }

    // ---- Query plans --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_query_can_use_the_partial_claim_index()
    {
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        await using var sp = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => pg.Schema = schema));
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var outbox = sp.GetRequiredService<IOutbox>();
            for (var i = 0; i < 5; i++)
            {
                await outbox.EnqueueStandaloneAsync(new TestMessage($"e{i}"), cancellationToken: Ct);
            }

            var settings = PostgreSqlSettings.Create(pg => pg.Schema = schema);
            var plans = new Dictionary<string, string>
            {
                ["claim"] = await ExplainAsync(settings.ClaimSql, 100, Guid.NewGuid(), "explain-worker", TimeSpan.FromMinutes(2), (long)OutboxLimits.BatchPayloadBudgetBytes),
                ["delete-delivered"] = await ExplainAsync(settings.DeleteDeliveredSql, TimeSpan.FromDays(7), 1000),
                ["requeue-dead-lettered"] = await ExplainAsync(settings.RequeueDeadLetteredSql, 100),
            };

            foreach (var (name, plan) in plans)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"-- {name}{Environment.NewLine}{plan}");
            }

            if (Environment.GetEnvironmentVariable("DBRELAY_EXPLAIN_OUT") is { Length: > 0 } path)
            {
                await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, plans.Select(p => $"-- {p.Key}{Environment.NewLine}{p.Value}")), Ct);
            }

            Assert.Contains($"using {settings.ClaimIndexName}", plans["claim"], StringComparison.Ordinal);
            Assert.Contains("LockRows", plans["claim"], StringComparison.Ordinal);
            Assert.Contains($"using {settings.CleanupIndexName}", plans["delete-delivered"], StringComparison.Ordinal);
            Assert.Contains($"using {settings.DeadIndexName}", plans["requeue-dead-lettered"], StringComparison.Ordinal);
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    [Fact]
    public async Task Planner_chooses_the_partial_indexes_on_a_realistic_table()
    {
        // T-03: the test above proves the indexes are usable (seqscan disabled, 5 rows). This one proves the planner CHOOSES them
        // on a steady-state table, including the statistics query (CR-03), which a sequential scan would make O(table).
        RequireDatabase();
        var schema = PostgreSqlTestHelpers.NewSchemaName();
        await using var sp = Build(relay => relay.UsePostgreSql(fixture.DataSource, pg => pg.Schema = schema));
        try
        {
            await sp.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var settings = PostgreSqlSettings.Create(pg => pg.Schema = schema);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
            {
                // 100,000 rows: 2.5% due pending, 2.5% dead-lettered, 95% delivered (as in the SQL Server plan test).
                await connection.ExecuteAsync(
                    $"""
                    INSERT INTO {settings.Table} (id, message_type, payload, content_type, status, attempt_count, created_at, scheduled_at,
                                                 available_at, delivered_at, dead_lettered_at)
                    SELECT gen_random_uuid(), 'plan.test', '\x7b7d'::bytea, 'application/json',
                           CASE WHEN i % 40 = 0 THEN 0 WHEN i % 40 = 1 THEN 3 ELSE 2 END,
                           1, now() - i * interval '1 second', now() - i * interval '1 second', now() - i * interval '1 second',
                           CASE WHEN i % 40 > 1 THEN now() - i * interval '1 second' END,
                           CASE WHEN i % 40 = 1 THEN now() - i * interval '1 second' END
                    FROM generate_series(1, 100000) AS i
                    """,
                    commandTimeout: 120);

                // Sets the visibility map (index-only scans) and the statistics, as autovacuum would on a live table.
                await connection.ExecuteAsync($"VACUUM ANALYZE {settings.Table}", commandTimeout: 120);
            }

            var plans = new Dictionary<string, string>
            {
                ["claim"] = await ExplainAsync(settings.ClaimSql, disableSeqScan: false, 100, Guid.NewGuid(), "explain-worker", TimeSpan.FromMinutes(2), (long)OutboxLimits.BatchPayloadBudgetBytes),
                ["delete-delivered"] = await ExplainAsync(settings.DeleteDeliveredSql, disableSeqScan: false, TimeSpan.FromDays(7), 1000),
                ["requeue-dead-lettered"] = await ExplainAsync(settings.RequeueDeadLetteredSql, disableSeqScan: false, 100),
                ["statistics"] = await ExplainAsync(settings.StatisticsSql, disableSeqScan: false),
                ["admin-query-dead-lettered"] = await ExplainAsync(
                    settings.QueryPage(settings.QueryKeysPrefix + " WHERE status = $1 ORDER BY created_at, id OFFSET $2 LIMIT $3"),
                    disableSeqScan: false,
                    (short)OutboxMessageStatus.DeadLettered,
                    0,
                    100),
            };

            foreach (var (name, plan) in plans)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"-- {name}{Environment.NewLine}{plan}");
            }

            Assert.Contains($"using {settings.ClaimIndexName}", plans["claim"], StringComparison.Ordinal);
            Assert.Contains("LockRows", plans["claim"], StringComparison.Ordinal);
            Assert.Contains(settings.CleanupIndexName, plans["delete-delivered"], StringComparison.Ordinal);
            Assert.Contains(settings.DeadIndexName, plans["requeue-dead-lettered"], StringComparison.Ordinal);
            Assert.Contains(settings.DeadIndexName, plans["admin-query-dead-lettered"], StringComparison.Ordinal);

            // Statistics: pending, processing and the oldest-due age use the claim index; dead-lettered uses the dead index. The
            // Delivered count is O(delivered rows) whichever plan is chosen (K12): with 95% of rows delivered the planner may
            // prefer a sequential scan to an index-only scan of the cleanup index, so exactly that one scan is allowed.
            Assert.True(Occurrences(plans["statistics"], settings.ClaimIndexName) >= 3, plans["statistics"]);
            Assert.Contains(settings.DeadIndexName, plans["statistics"], StringComparison.Ordinal);
            Assert.True(Occurrences(plans["statistics"], "Seq Scan") <= 1, plans["statistics"]);
            foreach (var (name, plan) in plans.Where(p => p.Key != "statistics"))
            {
                Assert.False(plan.Contains("Seq Scan", StringComparison.Ordinal), $"{name} uses a sequential scan:{Environment.NewLine}{plan}");
            }
        }
        finally
        {
            await PostgreSqlTestHelpers.DropSchemaAsync(fixture.DataSource, schema);
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private Task<string> ExplainAsync(string sql, params object[] parameters) => ExplainAsync(sql, disableSeqScan: true, parameters);

    private async Task<string> ExplainAsync(string sql, bool disableSeqScan, params object[] parameters)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        if (disableSeqScan)
        {
            await using var off = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction);
            await off.ExecuteNonQueryAsync(Ct);
        }

        await using var explain = new NpgsqlCommand("EXPLAIN " + sql, connection, transaction);
        foreach (var value in parameters)
        {
            explain.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        var lines = new List<string>();
        await using (var reader = await explain.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                lines.Add(reader.GetString(0));
            }
        }

        await transaction.RollbackAsync(Ct);
        return string.Join(Environment.NewLine, lines);
    }
}
