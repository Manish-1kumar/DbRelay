using System.Data.Common;
using System.Text;
using System.Xml.Linq;
using Dapper;
using DbRelay.SqlServer.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DbRelay.SqlServer.Tests;

/// <summary>SQL Server-specific behaviour that the provider-neutral conformance suite does not cover.</summary>
public sealed class SqlServerProviderTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private readonly List<(ServiceProvider Services, string ConnectionString, string Schema)> _created = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var (services, connectionString, schema) in _created)
        {
            await services.DisposeAsync();
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            await TestDatabase.DropSchemaAsync(connection, schema);
        }
    }

    // ---- Configuration -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("dbo]; DROP TABLE x; --")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("x'y")]
    [InlineData("[dbrelay]")]
    [InlineData("dbrelay.outbox")]
    [InlineData("schéma")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")] // 64 characters
    public void Hostile_or_invalid_identifiers_are_rejected_when_registering(string identifier)
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddDbRelay(r => r.UseSqlServer("Server=.;Database=x", o => o.Schema = identifier)));

        services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddDbRelay(r => r.UseSqlServer("Server=.;Database=x", o => o.TableName = identifier)));

        services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddDbRelay(r => r.UseSqlServer(_ => new SqlConnection(), o => o.Schema = identifier)));
    }

    [Fact]
    public void Reserved_table_name_and_invalid_timeouts_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbRelay(r => r.UseSqlServer("Server=.", o => o.TableName = "Schema_Version")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddDbRelay(r => r.UseSqlServer("Server=.", o => o.CommandTimeout = TimeSpan.Zero)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddDbRelay(r => r.UseSqlServer("Server=.", o => o.CommandTimeout = TimeSpan.FromHours(2))));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbRelay(r => r.UseSqlServer(" ")));
    }

    [Fact]
    public void Create_script_quotes_configured_names_and_is_never_destructive()
    {
        var services = new ServiceCollection();
        services.AddDbRelay(r =>
        {
            r.UseSqlServer("Server=.", o =>
            {
                o.Schema = "Sales_Outbox";
                o.TableName = "Messages_1";
            });
            r.DisableDispatcher();
        });
        using var provider = services.BuildServiceProvider();
        var script = provider.GetRequiredService<IOutboxSchemaManager>().GetCreateScript();

        Assert.Contains("CREATE TABLE [Sales_Outbox].[Messages_1]", script, StringComparison.Ordinal);
        Assert.Contains("[ix_Messages_1_claim]", script, StringComparison.Ordinal);
        Assert.Contains("WHERE [status] IN (0, 1)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER ", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[dbrelay]", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connection_factory_connections_are_opened_by_DbRelay_and_open_ones_are_refused()
    {
        RequireDatabase(rcsi: false);
        var schema = NewSchema();
        var created = 0;
        var services = Build(fixture.ConnectionString!, schema, relay => relay.UseSqlServer(
            _ =>
            {
                Interlocked.Increment(ref created);
                return new SqlConnection(fixture.ConnectionString);
            },
            o => o.Schema = schema));
        await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
        await services.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct);
        Assert.True(created >= 2);

        await using var open = new SqlConnection(fixture.ConnectionString);
        await open.OpenAsync(Ct);
        var badSchema = NewSchema();
        var bad = Build(fixture.ConnectionString!, badSchema, relay => relay.UseSqlServer(_ => open, o => o.Schema = badSchema));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct));
        Assert.Equal(System.Data.ConnectionState.Open, open.State); // not disposed by DbRelay
    }

    // ---- Schema ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureCreated_is_safe_under_concurrent_execution()
    {
        RequireDatabase(rcsi: false);
        var schema = NewSchema();
        var managers = Enumerable.Range(0, 8)
            .Select(i => Build(fixture.ConnectionString!, schema, relay => relay.UseSqlServer(fixture.ConnectionString!, o =>
            {
                o.Schema = schema;
                o.TableName = i % 2 == 0 ? "outbox_a" : "outbox_b";
            })))
            .Select(sp => sp.GetRequiredService<IOutboxSchemaManager>())
            .ToList();

        for (var round = 0; round < 3; round++)
        {
            await Task.WhenAll(managers.Select(m => Task.Run(() => m.EnsureCreatedAsync(Ct), Ct)));
        }

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        foreach (var table in new[] { "outbox_a", "outbox_b" })
        {
            var indexes = (await connection.QueryAsync<string>(
                "SELECT name FROM sys.indexes WHERE object_id = OBJECT_ID(QUOTENAME(@schema) + N'.' + QUOTENAME(@table)) AND name IS NOT NULL ORDER BY name;",
                new { schema, table })).ToList();
            Assert.Equal(
                [$"ix_{table}_claim", $"ix_{table}_dead_lettered", $"ix_{table}_delivered", $"pk_{table}", $"ux_{table}_id"],
                indexes);
        }

        var versions = (await connection.QueryAsync<(string Component, int Version)>(
            $"SELECT [component], [version] FROM [{schema}].[schema_version] ORDER BY [component];")).ToList();
        Assert.Equal([("outbox:outbox_a", 1), ("outbox:outbox_b", 1)], versions);

        var filter = await connection.ExecuteScalarAsync<string>(
            "SELECT filter_definition FROM sys.indexes WHERE object_id = OBJECT_ID(QUOTENAME(@schema) + N'.[outbox_a]') AND name = N'ix_outbox_a_claim';",
            new { schema });
        Assert.Equal("([status] IN ((0), (1)))", filter);
    }

    // ---- Insert ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnqueueMany_with_1000_messages_is_chunked_below_the_2100_parameter_limit()
    {
        RequireDatabase(rcsi: false);
        var (services, _) = await CreateAsync(rcsi: false);
        var outbox = services.GetRequiredService<IOutbox>();
        var admin = services.GetRequiredService<IOutboxAdmin>();

        Assert.True(SqlServerOutboxSql.MaxInsertRowsPerStatement * SqlServerOutboxSql.InsertParametersPerRow <= 2100);

        IReadOnlyList<Guid> ids;
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(Ct);
            ids = await outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, 1000).Select(i => new TestMessage($"bulk{i}")), Ct);
            Assert.NotNull(transaction.Connection); // still active: not committed by DbRelay
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1000, ids.Distinct().Count());
        var stored = await admin.QueryAsync(new OutboxQuery { Take = 1000 }, Ct);
        // Chunks run in order, so each 200-row chunk is created before the next; order within one statement is not guaranteed.
        Assert.Equal(ids.Order(), stored.Select(m => m.Id).Order());
        Assert.Equal(1000, stored.Select(m => m.Id).Distinct().Count());
        Assert.Equal("{\"text\":\"bulk999\"}", Encoding.UTF8.GetString(stored[^1].Payload.Span));

        // Without a caller transaction the 1000 rows are still inserted (in DbRelay's own transaction).
        await using (var connection = new SqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            var more = await outbox.EnqueueManyAsync(connection, null, Enumerable.Range(0, 1000).Select(i => new TestMessage($"auto{i}")), null, Ct);
            Assert.Equal(1000, more.Count);
            Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        }

        Assert.Equal(2000, (await admin.GetStatisticsAsync(Ct)).Pending);
    }

    [Fact]
    public async Task Multi_statement_insert_without_caller_transaction_is_all_or_nothing()
    {
        RequireDatabase(rcsi: false);
        var (services, _) = await CreateAsync(rcsi: false);
        var store = services.GetRequiredService<IOutboxStore>();
        var admin = services.GetRequiredService<IOutboxAdmin>();

        var records = Enumerable.Range(0, 450).Select(i => Record(Guid.NewGuid(), $"r{i}")).ToList();
        records[420] = Record(records[3].Id, "duplicate in the third statement");

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);
        await Assert.ThrowsAnyAsync<DbException>(() => store.InsertAsync(connection, null, records, Ct));

        var stats = await admin.GetStatisticsAsync(Ct);
        Assert.Equal(0, stats.Pending);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Delay_of_3650_days_does_not_overflow_DATEADD()
    {
        RequireDatabase(rcsi: false);
        var (services, _) = await CreateAsync(rcsi: false);
        var outbox = services.GetRequiredService<IOutbox>();
        var admin = services.GetRequiredService<IOutboxAdmin>();

        var id = await outbox.EnqueueStandaloneAsync(new TestMessage("far"), new EnqueueOptions { Delay = TimeSpan.FromDays(3649) + TimeSpan.FromMilliseconds(1234) }, Ct);
        var stored = await admin.GetAsync(id, Ct);
        Assert.NotNull(stored);
        var expected = stored.CreatedAt + TimeSpan.FromDays(3649) + TimeSpan.FromMilliseconds(1234);
        Assert.Equal(expected, stored.ScheduledAt);
        Assert.Equal(stored.ScheduledAt, stored.AvailableAt);
        Assert.Equal(TimeSpan.Zero, stored.ScheduledAt.Offset);
    }

    // ---- Locking -----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claim_skips_rows_locked_by_an_in_flight_claim_without_blocking(bool rcsi)
    {
        RequireDatabase(rcsi);
        var (services, schema) = await CreateAsync(rcsi);
        var outbox = services.GetRequiredService<IOutbox>();
        var store = services.GetRequiredService<IOutboxStore>();
        var first = await outbox.EnqueueStandaloneAsync(new TestMessage("first"), new EnqueueOptions { DeliverAt = DateTimeOffset.UtcNow.AddMinutes(-2) }, Ct);
        var second = await outbox.EnqueueStandaloneAsync(new TestMessage("second"), new EnqueueOptions { DeliverAt = DateTimeOffset.UtcNow.AddMinutes(-1) }, Ct);
        var sql = new SqlServerOutboxSql(schema, "outbox_messages");

        // Another worker's claim of the oldest row, still uncommitted (as if its statement were still running).
        await using var holder = new SqlConnection(fixture.GetConnectionString(rcsi));
        await holder.OpenAsync(Ct);
        await using var transaction = (SqlTransaction)await holder.BeginTransactionAsync(Ct);
        var held = new List<Guid>();
        await using (var command = new SqlCommand(sql.Claim, holder, transaction))
        {
            command.Parameters.AddWithValue("@batch_size", 1);
            command.Parameters.AddWithValue("@lock_token", Guid.NewGuid());
            command.Parameters.AddWithValue("@worker_id", "holder");
            command.Parameters.AddWithValue("@lease_ms", 60000L);
            command.Parameters.AddWithValue("@max_payload_bytes", (long)OutboxLimits.BatchPayloadBudgetBytes);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                held.Add(reader.GetGuid(0));
            }
        }

        Assert.Equal([first], held);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var started = System.Diagnostics.Stopwatch.StartNew();
        var claimed = await store.ClaimAsync(new ClaimRequest(10, TimeSpan.FromMinutes(1), Guid.NewGuid(), "w"), timeout.Token);
        started.Stop();

        Assert.Equal([second], claimed.Select(m => m.Id)); // the locked row is skipped, not returned twice
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5), $"Claim took {started.Elapsed}; it should not wait for the other transaction.");

        await transaction.RollbackAsync(Ct);
        var afterRollback = await store.ClaimAsync(new ClaimRequest(10, TimeSpan.FromMinutes(1), Guid.NewGuid(), "w"), Ct);
        Assert.Equal([first], afterRollback.Select(m => m.Id));
        Assert.Equal(1, afterRollback[0].AttemptCount);
    }

    // ---- Execution plans -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_cleanup_requeue_and_statistics_plans_use_the_filtered_indexes()
    {
        RequireDatabase(rcsi: false);
        var (_, schema) = await CreateAsync(rcsi: false);
        var sql = new SqlServerOutboxSql(schema, "outbox_messages");
        var table = sql.QualifiedTable;

        await using var connection = new SqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Ct);

        // The four target-framework test processes run in parallel against one (Express) instance; run this heavy test one at a
        // time so plan capture is not distorted by, or timed out behind, another process's 20,000-row seed (session lock, released
        // when the connection closes).
        await connection.ExecuteAsync(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'dbrelay-sqlserver-tests-plans', @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 300000; IF @r < 0 THROW 50000, N'Timed out waiting for the plan test lock.', 1;",
            commandTimeout: 310);

        // 20,000 rows: 2.5% due pending, 2.5% dead-lettered, 95% delivered - a realistic steady state.
        await connection.ExecuteAsync(
            $"""
            WITH n AS (SELECT TOP (20000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO {table} ([id], [message_type], [payload], [content_type], [status], [attempt_count], [created_at], [scheduled_at], [available_at], [delivered_at], [dead_lettered_at])
            SELECT NEWID(), N'plan.test', 0x7B7D, N'application/json',
                   CASE WHEN i % 40 = 0 THEN 0 WHEN i % 40 = 1 THEN 3 ELSE 2 END,
                   1, DATEADD(second, -i, SYSUTCDATETIME()), DATEADD(second, -i, SYSUTCDATETIME()), DATEADD(second, -i, SYSUTCDATETIME()),
                   CASE WHEN i % 40 > 1 THEN DATEADD(second, -i, SYSUTCDATETIME()) END,
                   CASE WHEN i % 40 = 1 THEN DATEADD(second, -i, SYSUTCDATETIME()) END
            FROM n
            OPTION (MAX_GRANT_PERCENT = 1); -- test seeding only: keep the grant small on SQL Server Express
            UPDATE STATISTICS {table} WITH FULLSCAN;
            """,
            commandTimeout: 120);

        var report = new StringBuilder();
        var claim = await CaptureActualPlanAsync(connection, "claim", sql.Claim, report, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@batch_size", System.Data.SqlDbType.Int) { Value = 100 });
            cmd.Parameters.Add(new SqlParameter("@lock_token", System.Data.SqlDbType.UniqueIdentifier) { Value = Guid.NewGuid() });
            cmd.Parameters.Add(new SqlParameter("@worker_id", System.Data.SqlDbType.NVarChar, 256) { Value = "plan" });
            cmd.Parameters.Add(new SqlParameter("@lease_ms", System.Data.SqlDbType.BigInt) { Value = 60000L });
            cmd.Parameters.Add(new SqlParameter("@max_payload_bytes", System.Data.SqlDbType.BigInt) { Value = (long)OutboxLimits.BatchPayloadBudgetBytes });
        });

        var cleanup = await CaptureActualPlanAsync(connection, "delete-delivered", sql.DeleteDelivered, report, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@max_count", System.Data.SqlDbType.Int) { Value = 1000 });
            cmd.Parameters.Add(new SqlParameter("@older_than_ms", System.Data.SqlDbType.BigInt) { Value = -3_600_000L });
        });

        var requeue = await CaptureActualPlanAsync(connection, "requeue-dead-lettered", sql.RequeueDeadLettered, report, cmd =>
            cmd.Parameters.Add(new SqlParameter("@max_count", System.Data.SqlDbType.Int) { Value = 100 }));

        var statistics = await CaptureActualPlanAsync(connection, "statistics", sql.Statistics, report, _ => { });

        var records = Enumerable.Range(0, SqlServerOutboxSql.MaxInsertRowsPerStatement).Select(i => Record(Guid.NewGuid(), $"plan{i}")).ToList();
        var insert = await CaptureActualPlanAsync(connection, "insert-200", sql.GetInsert(records.Count), report, cmd =>
            SqlServerOutboxStore.AddInsertParameters(cmd.Parameters, records, 0, records.Count));

        var page = await CaptureActualPlanAsync(connection, "admin-query", sql.GetQuery(status: true, messageType: false, createdAfter: false, createdBefore: false), report, cmd =>
        {
            cmd.Parameters.Add(new SqlParameter("@status", System.Data.SqlDbType.TinyInt) { Value = (byte)2 });
            cmd.Parameters.Add(new SqlParameter("@skip", System.Data.SqlDbType.Int) { Value = 100 });
            cmd.Parameters.Add(new SqlParameter("@take", System.Data.SqlDbType.Int) { Value = 100 });
        });

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
        var directory = Path.Combine(AppContext.BaseDirectory, "plans");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "plan-operators.txt"), report.ToString(), Ct);

        AssertUsesIndex(claim, "Index Seek", "ix_outbox_messages_claim");
        Assert.DoesNotContain(claim, op => op.Index == "pk_outbox_messages" && op.PhysicalOp.Contains("Scan", StringComparison.Ordinal));
        AssertUsesIndex(cleanup, "Index Seek", "ix_outbox_messages_delivered");
        Assert.Contains(requeue, op => op.Index == "ix_outbox_messages_dead_lettered");
        Assert.Contains(statistics, op => op.Index == "ix_outbox_messages_claim");
        Assert.Contains(statistics, op => op.Index == "ix_outbox_messages_delivered");
        Assert.Contains(statistics, op => op.Index == "ix_outbox_messages_dead_lettered");
        Assert.DoesNotContain(statistics, op => op.Index == "pk_outbox_messages");
        Assert.Contains(page, op => op.PhysicalOp == "Clustered Index Seek" && op.Index == "pk_outbox_messages"); // page rows fetched by key
        Assert.Contains(insert, op => op.PhysicalOp == "Clustered Index Insert");
        Assert.DoesNotContain(insert, op => op.PhysicalOp == "Sort"); // the enqueue path needs no memory grant
    }

    private sealed record PlanOperator(int Depth, string PhysicalOp, string LogicalOp, string? Index, string? EstimatedRows, string? ActualRows, string? ActualRowsRead);

    private static void AssertUsesIndex(IReadOnlyList<PlanOperator> plan, string physicalOp, string index) =>
        Assert.Contains(plan, op => op.PhysicalOp == physicalOp && op.Index == index);

    private static async Task<IReadOnlyList<PlanOperator>> CaptureActualPlanAsync(
        SqlConnection connection, string name, string sql, StringBuilder report, Action<SqlCommand> addParameters)
    {
        await connection.ExecuteAsync("SET STATISTICS XML ON;");
        var plans = new List<string>();
        try
        {
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            addParameters(command);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            do
            {
                if (reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.OrdinalIgnoreCase))
                {
                    while (await reader.ReadAsync(Ct))
                    {
                        plans.Add(reader.GetString(0));
                    }
                }
                else
                {
                    while (await reader.ReadAsync(Ct))
                    {
                    }
                }
            }
            while (await reader.NextResultAsync(Ct));
        }
        finally
        {
            await connection.ExecuteAsync("SET STATISTICS XML OFF;");
        }

        var operators = new List<PlanOperator>();
        report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"== {name} (actual plan) ==");
        foreach (var plan in plans)
        {
            var document = XDocument.Parse(plan);
            var directory = Path.Combine(AppContext.BaseDirectory, "plans");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{name}-{plans.IndexOf(plan)}.sqlplan"), plan, Ct);
            foreach (var statement in document.Descendants(Showplan + "StmtSimple"))
            {
                var root = statement.Element(Showplan + "QueryPlan")?.Element(Showplan + "RelOp");
                if (root is null)
                {
                    continue;
                }

                report.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"-- statement: {Shorten(statement.Attribute("StatementText")?.Value)}");
                var grant = statement.Element(Showplan + "QueryPlan")?.Element(Showplan + "MemoryGrantInfo");
                if (grant is not null)
                {
                    report.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                        $"   memory grant KB: requested={grant.Attribute("RequestedMemory")?.Value} granted={grant.Attribute("GrantedMemory")?.Value} used={grant.Attribute("MaxUsedMemory")?.Value} wait_ms={grant.Attribute("GrantWaitTime")?.Value}");
                }
                Walk(root, 0);
            }
        }

        return operators;

        void Walk(XElement relOp, int depth)
        {
            var objects = relOp.Elements().SelectMany(e => e.Elements(Showplan + "Object"))
                .Select(o => o.Attribute("Index")?.Value?.Trim('[', ']')).Where(i => i is not null).Distinct().ToList();
            var runtime = relOp.Element(Showplan + "RunTimeInformation")?.Elements(Showplan + "RunTimeCountersPerThread").ToList() ?? [];
            var actualRows = runtime.Count == 0 ? null : runtime.Sum(r => long.Parse(r.Attribute("ActualRows")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var rowsRead = runtime.Count == 0 || runtime.All(r => r.Attribute("ActualRowsRead") is null)
                ? null
                : runtime.Sum(r => long.Parse(r.Attribute("ActualRowsRead")?.Value ?? "0", System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var op = new PlanOperator(
                depth,
                relOp.Attribute("PhysicalOp")?.Value ?? "?",
                relOp.Attribute("LogicalOp")?.Value ?? "?",
                objects.Count == 0 ? null : string.Join(",", objects),
                relOp.Attribute("EstimateRows")?.Value,
                actualRows,
                rowsRead);
            operators.Add(op);
            report.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"{new string(' ', depth * 2)}{op.PhysicalOp} [{op.LogicalOp}]{(op.Index is null ? string.Empty : " on " + op.Index)} est={op.EstimatedRows} actual={op.ActualRows ?? "-"}{(op.ActualRowsRead is null ? string.Empty : " read=" + op.ActualRowsRead)}");
            foreach (var child in relOp.Descendants(Showplan + "RelOp").Where(c => c.Ancestors(Showplan + "RelOp").First() == relOp))
            {
                Walk(child, depth + 1);
            }
        }

        static string? Shorten(string? text)
        {
            if (text is null)
            {
                return null;
            }

            var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return flat.Length <= 160 ? flat : flat[..160] + "...";
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private void RequireDatabase(bool rcsi) => Assert.SkipWhen(fixture.GetSkipReason(rcsi) is not null, fixture.GetSkipReason(rcsi) ?? string.Empty);

    private static string NewSchema() => DbRelay.Testing.TestSchemaNames.New("p");

    private ServiceProvider Build(string connectionString, string schema, Action<DbRelayBuilder> useStorage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            useStorage(relay);
            relay.DisableDispatcher();
        });
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        lock (_created)
        {
            _created.Add((provider, connectionString, schema));
        }

        return provider;
    }

    private async Task<(ServiceProvider Services, string Schema)> CreateAsync(bool rcsi)
    {
        var schema = NewSchema();
        var connectionString = fixture.GetConnectionString(rcsi)!;
        var services = Build(connectionString, schema, relay => relay.UseSqlServer(connectionString, o => o.Schema = schema));
        await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
        return (services, schema);
    }

    private static OutboxRecord Record(Guid id, string text) => new()
    {
        Id = id,
        MessageType = "provider.test",
        Payload = Encoding.UTF8.GetBytes($"{{\"text\":\"{text}\"}}"),
        ContentType = "application/json",
    };
}
