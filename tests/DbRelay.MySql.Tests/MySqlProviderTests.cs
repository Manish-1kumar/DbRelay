using System.Collections.Concurrent;
using System.Data.Common;
using Dapper;
using DbRelay.MySql.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using static DbRelay.MySql.Tests.MySqlTestHelpers;

namespace DbRelay.MySql.Tests;

/// <summary>MySQL- and MariaDB-specific behaviour not covered by the provider-neutral conformance suite.</summary>
public sealed class MySqlProviderTests(MySqlFixture fixture)
{
    private const string AnyConnectionString = "Server=localhost;User ID=x;Password=y";

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

    private ServiceProvider BuildFor(string schema, string? table = null, string? connectionString = null) =>
        Build(relay => relay.UseMySql(connectionString ?? fixture.ConnectionString, o =>
        {
            o.Schema = schema;
            if (table is not null)
            {
                o.TableName = table;
            }
        }));

    private async Task<(ServiceProvider Services, string Schema)> CreateAsync(string? connectionString = null)
    {
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        var services = BuildFor(schema, connectionString: connectionString);
        await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
        return (services, schema);
    }

    private static OutboxRecord Record(Guid? id = null, byte[]? payload = null, string? destination = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        MessageType = "mysql.test",
        Payload = payload ?? "{}"u8.ToArray(),
        ContentType = "application/json",
        Destination = destination,
    };

    private static ClaimRequest Claim(int batch = 100, TimeSpan? lease = null, Guid? token = null) =>
        new(batch, lease ?? TimeSpan.FromMinutes(5), token ?? Guid.NewGuid(), "mysql-tests");

    private async Task<long> CountAsync(string schema)
    {
        await using var connection = await fixture.OpenAsync();
        return await connection.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM {Quote(schema)}.outbox_messages");
    }

    // ---- Registration (no database needed) ------------------------------------------------------------------------

    public static TheoryData<string?> HostileNames =>
    [
        "x; DROP TABLE y",
        "a`b",
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
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UseMySql(AnyConnectionString, o => o.Schema = name)));
        Assert.Equal("MySqlOutboxOptions.Schema", ex.ParamName);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void Hostile_table_names_are_rejected_at_registration(string? name)
    {
        var ex = Assert.Throws<ArgumentException>(() => Build(relay => relay.UseMySql(AnyConnectionString, o => o.TableName = name!)));
        Assert.Equal("MySqlOutboxOptions.TableName", ex.ParamName);
    }

    [Fact]
    public void Reserved_table_name_and_invalid_timeout_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UseMySql(AnyConnectionString, o => o.TableName = "dbrelay_schema_version")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build(relay => relay.UseMySql(AnyConnectionString, o => o.CommandTimeout = TimeSpan.Zero)));
        Assert.Throws<ArgumentException>(() => Build(relay => relay.UseMySql(" ")));
    }

    [Fact]
    public void Names_are_quoted_and_long_names_stay_within_the_identifier_limit()
    {
        Assert.Equal("`outbox_messages`", new MySqlOutboxSql(null, "outbox_messages").Table);
        Assert.Equal("`billing`.`outbox`", new MySqlOutboxSql("billing", "outbox").Table);

        var sql = new MySqlOutboxSql("s", "t" + new string('x', 62));
        string[] names = [sql.StatusConstraintName, sql.ClaimIndexName, sql.CleanupIndexName, sql.DeadIndexName];
        Assert.All(names, n => Assert.InRange(n.Length, 1, 64));
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_script_has_its_placeholders_replaced()
    {
        var script = new MySqlOutboxSql("billing", "outbox").CreateScript;
        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS `billing`.`outbox`", script, StringComparison.Ordinal);
        Assert.Contains("KEY `ix_outbox_claim` (claim_at, status)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Statements_stay_within_their_limits()
    {
        var sql = new MySqlOutboxSql(null, "outbox_messages");
        Assert.Throws<ArgumentOutOfRangeException>(() => sql.GetInsert(OutboxLimits.MaxEnqueueBatchSize + 1));
        Assert.True(OutboxLimits.MaxEnqueueBatchSize * 10 <= 65535, "MySQL allows at most 65,535 placeholders per statement.");
        Assert.Equal(128, MySqlOutboxSql.BucketFor(100));
        Assert.Equal(OutboxLimits.MaxClaimBatchSize, MySqlOutboxSql.BucketFor(OutboxLimits.MaxClaimBatchSize));
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
                    "SELECT DISTINCT INDEX_NAME FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = 'outbox_messages' ORDER BY INDEX_NAME",
                    new { schema })).ToArray();
                Assert.Equal(["PRIMARY", "ix_outbox_messages_claim", "ix_outbox_messages_cleanup", "ix_outbox_messages_dead"], indexes.Order(StringComparer.Ordinal));
                var version = await connection.ExecuteScalarAsync<int>(
                    $"SELECT version FROM {Quote(schema)}.dbrelay_schema_version WHERE component = 'outbox:outbox_messages'");
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
    public async Task Two_outbox_tables_can_share_a_database_including_63_character_names()
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
            var versions = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM {Quote(schema)}.dbrelay_schema_version");
            Assert.Equal(2, versions);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- Transactions, inserts and stored values ------------------------------------------------------------------------

    [Fact]
    public async Task Null_transaction_while_a_transaction_is_in_progress_is_rejected_with_guidance()
    {
        // Documented provider-specific behaviour (as on SQL Server, CR-11): MySqlConnector rejects a command without the connection's
        // active transaction, and DbRelay cannot adopt it, so the error says to pass it.
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var outbox = services.GetRequiredService<IOutbox>();
            await using var connection = await fixture.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                outbox.EnqueueAsync(connection, null, new TestMessage("no transaction passed"), cancellationToken: Ct));
            Assert.Contains("Pass that transaction", ex.Message, StringComparison.Ordinal);
            await transaction.RollbackAsync(Ct);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Theory]
    [InlineData(10, 5)]
    [InlineData(1000, 999)]
    public async Task A_multi_row_insert_is_one_atomic_statement(int count, int duplicateAt)
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            var records = Enumerable.Range(0, count).Select(_ => Record()).ToList();
            records[duplicateAt] = Record(records[0].Id); // primary key violation in the last part of the statement

            await using (var connection = await fixture.OpenAsync())
            {
                await Assert.ThrowsAnyAsync<DbException>(() => store.InsertAsync(connection, null, records, Ct));

                // Inside a transaction the caller then commits, the failed statement leaves nothing either.
                await using var transaction = await connection.BeginTransactionAsync(Ct);
                await Assert.ThrowsAnyAsync<DbException>(() => store.InsertAsync(connection, transaction, records, Ct));
                await transaction.CommitAsync(Ct);
            }

            await Assert.ThrowsAnyAsync<DbException>(() => store.InsertStandaloneAsync(records, Ct));
            Assert.Equal(0, await CountAsync(schema));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Theory]
    [InlineData("Binary16")]
    [InlineData("LittleEndianBinary16")]
    [InlineData("Char36")]
    public async Task Stored_ids_do_not_depend_on_the_GuidFormat_connection_option(string guidFormat)
    {
        RequireDatabase();
        var connectionString = new MySqlConnectionStringBuilder(fixture.ConnectionString) { GuidFormat = Enum.Parse<MySqlGuidFormat>(guidFormat) }.ConnectionString;
        var (services, schema) = await CreateAsync(connectionString);
        await using var _ = services;
        try
        {
            var outbox = services.GetRequiredService<IOutbox>();
            var store = services.GetRequiredService<IOutboxStore>();
            Guid id;
            await using (var connection = new MySqlConnection(connectionString))
            {
                await connection.OpenAsync(Ct);
                await using var transaction = await connection.BeginTransactionAsync(Ct);
                id = await outbox.EnqueueAsync(transaction, new TestMessage("guid format"), Ct);
                await transaction.CommitAsync(Ct);
            }

            // Stored as UNHEX of the "N" format, whatever GuidFormat says.
            await using (var check = await fixture.OpenAsync())
            {
                Assert.Equal(Hex(id).ToUpperInvariant(), await check.ExecuteScalarAsync<string>($"SELECT HEX(id) FROM {Quote(schema)}.outbox_messages"));
            }

            var token = Guid.NewGuid();
            var claimed = Assert.Single(await store.ClaimAsync(Claim(token: token), Ct));
            Assert.Equal(id, claimed.Id);
            Assert.True(await store.MarkDeliveredAsync(id, token, Ct));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task Empty_strings_and_an_empty_payload_round_trip()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var record = Record(payload: [], destination: string.Empty);
            await services.GetRequiredService<IOutboxStore>().InsertStandaloneAsync([record], Ct);
            var stored = await services.GetRequiredService<IOutboxAdmin>().GetAsync(record.Id, Ct);
            Assert.NotNull(stored);
            Assert.Equal(0, stored.Payload.Length);
            Assert.Equal(string.Empty, stored.Destination);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task Large_payloads_round_trip_through_insert_and_claim()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            var random = new Random(42);
            byte[] Payload(int size)
            {
                var bytes = new byte[size];
                random.NextBytes(bytes);
                return bytes;
            }

            // Within MariaDB's default max_allowed_packet (16 MiB): 80 x 128 KiB in one statement, and one 8 MiB payload.
            var records = Enumerable.Range(0, 80).Select(_ => Record(payload: Payload(128 * 1024))).ToList();
            await store.InsertStandaloneAsync(records, Ct);
            var large = Record(payload: Payload(8 * 1024 * 1024));
            await store.InsertStandaloneAsync([large], Ct);

            var expected = records.Append(large).ToDictionary(r => r.Id, r => r.Payload.ToArray());
            var claimed = await store.ClaimAsync(Claim(batch: 200), Ct);
            Assert.Equal(expected.Count, claimed.Count);
            Assert.All(claimed, message => Assert.Equal(expected[message.Id], message.Payload.ToArray()));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- Plans ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_cleanup_dead_letter_and_statistics_plans_use_the_generated_column_indexes()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var sql = new MySqlOutboxSql(schema, "outbox_messages");
            await using var connection = await fixture.OpenAsync();

            // 20,000 rows: 2.5% due pending, 2.5% dead-lettered, 95% delivered.
            await connection.ExecuteAsync(
                $"""
                INSERT INTO {sql.Table} (id, message_type, payload, content_type, status, attempt_count, created_at, scheduled_at, available_at, delivered_at, dead_lettered_at)
                SELECT UNHEX(REPLACE(UUID(), '-', '')), 'plan.test', 0x7B7D, 'application/json',
                       CASE WHEN n.i % 40 = 0 THEN 0 WHEN n.i % 40 = 1 THEN 3 ELSE 2 END, 1,
                       UTC_TIMESTAMP(6) - INTERVAL n.i SECOND, UTC_TIMESTAMP(6) - INTERVAL n.i SECOND, UTC_TIMESTAMP(6) - INTERVAL n.i SECOND,
                       CASE WHEN n.i % 40 > 1 THEN UTC_TIMESTAMP(6) - INTERVAL n.i SECOND END,
                       CASE WHEN n.i % 40 = 1 THEN UTC_TIMESTAMP(6) - INTERVAL n.i SECOND END
                FROM (SELECT a.d + b.d * 10 + c.d * 100 + d.d * 1000 + e.d * 10000 AS i
                      FROM (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) a,
                           (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) b,
                           (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) c,
                           (SELECT 0 AS d UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) d,
                           (SELECT 0 AS d UNION ALL SELECT 1) e) n
                WHERE n.i BETWEEN 1 AND 20000
                """);
            await connection.ExecuteAsync($"ANALYZE TABLE {sql.Table}");

            var claim = await PlanAsync(connection, sql.ClaimCandidates.Replace("@batch_size", "50", StringComparison.Ordinal));
            AssertUses(claim, sql.ClaimIndexName);
            AssertUses(await PlanAsync(connection, sql.DeleteDelivered.Replace("@older_than_us", "1", StringComparison.Ordinal).Replace("@max_count", "50", StringComparison.Ordinal)), sql.CleanupIndexName);
            AssertUses(await PlanAsync(connection, sql.RequeueDeadLettered.Replace("@max_count", "50", StringComparison.Ordinal)), sql.DeadIndexName);

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
        Assert.True(plan.Contains("key=" + index, StringComparison.Ordinal), $"Expected the plan to use {index}:\n{plan}");
        Assert.False(plan.Contains("type=ALL", StringComparison.Ordinal), "The plan scans the whole table:\n" + plan);
    }

    /// <summary>Returns the EXPLAIN rows of <paramref name="statement"/> as "table=... type=... key=..." lines.</summary>
    private static async Task<string> PlanAsync(MySqlConnection connection, string statement)
    {
        await using var command = new MySqlCommand("EXPLAIN " + statement, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            string Column(string name) => reader.GetOrdinal(name) is var i && !reader.IsDBNull(i) ? Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)! : "-";
            lines.Add($"table={Column("table")} type={Column("type")} key={Column("key")}");
        }

        return string.Join('\n', lines);
    }

    // ---- Cancellation, stress and the dispatcher end to end ------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_statement_that_waits_for_a_lock_throws_OperationCanceledException()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            var record = Record();
            await store.InsertStandaloneAsync([record], Ct);
            var token = Guid.NewGuid();
            Assert.Single(await store.ClaimAsync(Claim(token: token), Ct));

            await using var blocker = await fixture.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync(Ct);
            await blocker.ExecuteAsync(
                $"SELECT id FROM {Quote(schema)}.outbox_messages WHERE id = UNHEX(@Id) FOR UPDATE", new { Id = Hex(record.Id) }, transaction);

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.MarkDeliveredAsync(record.Id, token, cancel.Token));

            await transaction.RollbackAsync(Ct);
            Assert.True(await store.MarkDeliveredAsync(record.Id, token, Ct)); // the cancelled statement changed nothing
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task Concurrent_claims_batches_stale_acknowledgements_and_cleanup_never_deadlock()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = (MySqlOutboxStore)services.GetRequiredService<IOutboxStore>();
            IOutboxBatchStore batchStore = store;

            const int MessageCount = 600;
            var ids = new List<Guid>();
            for (var chunk = 0; chunk < MessageCount / 200; chunk++)
            {
                var records = Enumerable.Range(0, 200).Select(_ => Record()).ToList();
                await store.InsertStandaloneAsync(records, Ct);
                ids.AddRange(records.Select(r => r.Id));
            }

            var delivered = new ConcurrentDictionary<Guid, int>();
            var errors = new ConcurrentQueue<Exception>();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            stop.CancelAfter(TimeSpan.FromSeconds(180));
            var done = 0;

            async Task WorkerAsync(int seed)
            {
                var random = new Random(seed);
                while (Volatile.Read(ref done) == 0 && !stop.IsCancellationRequested)
                {
                    try
                    {
                        var token = Guid.NewGuid();
                        var claimed = await store.ClaimAsync(new ClaimRequest(random.Next(5, 40), TimeSpan.FromMilliseconds(800), token, $"w{seed}"), stop.Token);
                        if (claimed.Count == 0)
                        {
                            await Task.Delay(20, stop.Token);
                            continue;
                        }

                        await Task.Delay(random.Next(0, 1200), stop.Token);
                        var items = claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).OrderBy(_ => random.Next()).ToList();
                        if (random.Next(4) == 0)
                        {
                            items.Insert(random.Next(items.Count + 1), new OutboxAcknowledgement(items[random.Next(items.Count)].MessageId, Guid.NewGuid()));
                        }

                        var action = random.Next(100);
                        if (action < 70)
                        {
                            foreach (var pair in await batchStore.MarkDeliveredBatchAsync(items, stop.Token))
                            {
                                delivered.AddOrUpdate(pair.MessageId, 1, (_, n) => n + 1);
                            }
                        }
                        else if (action < 85)
                        {
                            await batchStore.ReleaseBatchAsync(items, stop.Token);
                        }
                        else if (action < 95)
                        {
                            if (await store.MarkDeliveredAsync(items[0].MessageId, token, stop.Token))
                            {
                                delivered.AddOrUpdate(items[0].MessageId, 1, (_, n) => n + 1);
                            }

                            foreach (var pair in await batchStore.MarkDeliveredBatchAsync(items.Skip(1).ToList() is { Count: > 0 } rest ? rest : items, stop.Token))
                            {
                                delivered.AddOrUpdate(pair.MessageId, 1, (_, n) => n + 1);
                            }
                        }
                        else
                        {
                            foreach (var item in items)
                            {
                                await store.ScheduleRetryAsync(item.MessageId, token, TimeSpan.Zero, "stress retry", stop.Token);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }
            }

            async Task CleanupAsync()
            {
                while (Volatile.Read(ref done) == 0 && !stop.IsCancellationRequested)
                {
                    try
                    {
                        await store.DeleteDeliveredAsync(TimeSpan.FromSeconds(5), 50, stop.Token);
                        if (delivered.Count == MessageCount)
                        {
                            Volatile.Write(ref done, 1);
                        }

                        await Task.Delay(150, stop.Token);
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        errors.Enqueue(ex);
                    }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(WorkerAsync).Append(CleanupAsync()));

            Assert.Empty(errors);
            Assert.Equal(1, Volatile.Read(ref done));
            Assert.Equal(ids.Order(), delivered.Keys.Order());
            Assert.All(delivered.Values, count => Assert.Equal(1, count));
            var stats = await store.GetStatisticsAsync(Ct);
            Assert.Equal(0, stats.Pending + stats.Processing);

            var retries = store.DeadlockRetries;
            TestContext.Current.TestOutputHelper?.WriteLine($"{fixture.ServerVersion}: deadlock/lock-timeout retries: {retries}");
            Assert.Equal(0, retries);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task The_dispatcher_delivers_every_message_across_a_shutdown_without_logging_errors()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        var logs = new ErrorCountingLoggerProvider();
        var deliveries = new ConcurrentDictionary<Guid, int>();

        IHost BuildHost()
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
            builder.Services.AddDbRelay(relay =>
            {
                relay.UseMySql(fixture.ConnectionString, o => o.Schema = schema);
                relay.UseDeliveryHandler(async (message, ct) =>
                {
                    await Task.Delay(Random.Shared.Next(1, 20), ct);
                    deliveries.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
                    return DeliveryResult.Delivered();
                });
                relay.ConfigureDispatcher(d =>
                {
                    d.BatchSize = 25;
                    d.MaxConcurrency = 8;
                    d.PollingInterval = TimeSpan.FromMilliseconds(50);
                    d.LeaseDuration = TimeSpan.FromSeconds(10);
                    d.DeliveryTimeout = TimeSpan.FromSeconds(3);
                });
                relay.ConfigureMaintenance(m =>
                {
                    m.CleanupEnabled = false;
                    m.StatisticsInterval = null;
                });
            });
            return builder.Build();
        }

        try
        {
            const int Count = 500;
            var ids = new List<Guid>();
            using (var first = BuildHost())
            {
                await first.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
                await first.StartAsync(Ct);
                var outbox = first.Services.GetRequiredService<IOutbox>();
                await using (var connection = await fixture.OpenAsync())
                {
                    for (var batch = 0; batch < Count / 50; batch++)
                    {
                        await using var transaction = await connection.BeginTransactionAsync(Ct);
                        ids.AddRange(await outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, 50).Select(i => new TestMessage($"b{batch}-{i}")), Ct));
                        await transaction.CommitAsync(Ct);
                    }
                }

                await Task.Delay(300, Ct);
                await first.StopAsync(Ct);
            }

            using (var second = BuildHost())
            {
                await second.StartAsync(Ct);
                var admin = second.Services.GetRequiredService<IOutboxAdmin>();
                var deadline = DateTime.UtcNow.AddMinutes(2);
                while ((await admin.GetStatisticsAsync(Ct)) is var stats && stats.Delivered < Count && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200, Ct);
                }

                await second.StopAsync(Ct);
                var final = await admin.GetStatisticsAsync(Ct);
                Assert.Equal(Count, final.Delivered);
                Assert.Equal(0, final.Pending + final.Processing + final.DeadLettered);
            }

            Assert.All(ids, id => Assert.True(deliveries.ContainsKey(id), $"{id} was never delivered"));
            Assert.Empty(logs.Errors);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    /// <summary>Collects every entry logged at Error or above.</summary>
    private sealed class ErrorCountingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _errors = new();

        public IReadOnlyCollection<string> Errors => _errors;

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Logger(ErrorCountingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error)
                {
                    owner._errors.Enqueue($"{category} [{eventId.Id}] {formatter(state, exception)} {exception?.GetType().Name}");
                }
            }
        }
    }
}
