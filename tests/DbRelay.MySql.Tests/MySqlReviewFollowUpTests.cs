using System.Data.Common;
using System.Diagnostics;
using Dapper;
using DbRelay.MySql.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using static DbRelay.MySql.Tests.MySqlTestHelpers;

namespace DbRelay.MySql.Tests;

/// <summary>Tests for the MySQL review's findings (CODE_REVIEW_REPORT.md, "MySQL provider review (ADR 0006)").</summary>
public sealed class MySqlReviewFollowUpTests(MySqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() => Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

    private static ServiceProvider Build(string connectionString, string? schema)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            relay.UseMySql(connectionString, o => o.Schema = schema);
            relay.DisableDispatcher();
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    private async Task<(ServiceProvider Services, string Schema)> CreateAsync(string? connectionString = null)
    {
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        var services = Build(connectionString ?? fixture.ConnectionString, schema);
        await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
        return (services, schema);
    }

    private string ConnectionTo(string database, Action<MySqlConnectionStringBuilder>? configure = null)
    {
        var builder = new MySqlConnectionStringBuilder(fixture.ConnectionString) { Database = database };
        configure?.Invoke(builder);
        return builder.ConnectionString;
    }

    private static OutboxRecord Record(byte[]? payload = null) => new()
    {
        Id = Guid.NewGuid(),
        MessageType = "mysql.review",
        Payload = payload ?? "{}"u8.ToArray(),
        ContentType = "application/json",
    };

    private static ClaimRequest Claim(int batch = 100, Guid? token = null) =>
        new(batch, TimeSpan.FromMinutes(5), token ?? Guid.NewGuid(), "mysql-review");

    // ---- MY-14: at most 1,000 records per insert (no database needed) --------------------------------------------------

    [Fact]
    public async Task The_storage_interface_rejects_more_than_the_enqueue_limit_in_one_call()
    {
        await using var services = Build("Server=localhost;User ID=x;Password=y", "s");
        var store = services.GetRequiredService<IOutboxStore>();
        var records = Enumerable.Range(0, OutboxLimits.MaxEnqueueBatchSize + 1).Select(_ => Record()).ToList();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.InsertStandaloneAsync(records, Ct));
    }

    // ---- MY-04 / ADR 0006: READ COMMITTED for DbRelay's own transactions, without touching the session ---------------------

    [Fact]
    public async Task Producers_can_insert_while_a_claim_holds_its_locks()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = (MySqlOutboxStore)services.GetRequiredService<IOutboxStore>();
            await store.InsertStandaloneAsync(Enumerable.Range(0, 5).Select(_ => Record()).ToList(), Ct);

            // The claim locks its candidates and holds them for 3 seconds. Under REPEATABLE READ its range scan would also lock the
            // gaps, and a producer inserting a due row into that range would wait; under READ COMMITTED it does not.
            store.ClaimTestPause = TimeSpan.FromSeconds(3);
            var claim = store.ClaimAsync(Claim(), Ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);

            var stopwatch = Stopwatch.StartNew();
            await store.InsertStandaloneAsync([Record()], Ct);
            stopwatch.Stop();

            Assert.Equal(5, (await claim).Count);
            store.ClaimTestPause = TimeSpan.Zero;
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1.5), $"The producer waited {stopwatch.Elapsed.TotalMilliseconds:0} ms for the claim's locks.");
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task DbRelay_transactions_leave_a_pooled_sessions_isolation_level_unchanged()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);

        // One pooled connection, not reset between uses: what DbRelay leaves on the session is what the application gets next.
        var shared = ConnectionTo(schema, b =>
        {
            b.MaximumPoolSize = 1;
            b.ConnectionReset = false;
            b.ApplicationName = "isolation-" + schema;
        });
        await using var services = Build(shared, schema);
        try
        {
            await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var store = services.GetRequiredService<IOutboxStore>();
            await using (var connection = new MySqlConnection(shared))
            {
                await connection.OpenAsync(Ct);
                await connection.ExecuteAsync("SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ");
            }

            await store.InsertStandaloneAsync([Record()], Ct);
            var token = Guid.NewGuid();
            var claimed = Assert.Single(await store.ClaimAsync(Claim(token: token), Ct)); // a READ COMMITTED transaction
            await ((IOutboxBatchStore)store).MarkDeliveredBatchAsync([new OutboxAcknowledgement(claimed.Id, token)], Ct);
            await store.DeleteDeliveredAsync(TimeSpan.Zero, 10, Ct);

            await using var after = new MySqlConnection(shared);
            await after.OpenAsync(Ct);
            Assert.Equal("REPEATABLE-READ", await IsolationAsync(after));
            Assert.Equal(0L, await after.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM information_schema.INNODB_TRX WHERE trx_mysql_thread_id = CONNECTION_ID()")); // nothing left open either
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    private static async Task<string> IsolationAsync(MySqlConnection connection)
    {
        try
        {
            return await connection.ExecuteScalarAsync<string>("SELECT @@session.transaction_isolation") ?? string.Empty;
        }
        catch (MySqlException)
        {
            return await connection.ExecuteScalarAsync<string>("SELECT @@session.tx_isolation") ?? string.Empty; // MariaDB before 11.1
        }
    }

    // ---- MY-02: a call larger than max_allowed_packet is rejected before it is sent ------------------------------------------

    [Fact]
    public async Task A_call_larger_than_max_allowed_packet_is_rejected_and_the_callers_transaction_survives()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            await using var connection = await fixture.OpenAsync();
            var limit = await connection.ExecuteScalarAsync<long>("SELECT @@max_allowed_packet");

            var size = 4 * 1024 * 1024;
            var count = (int)((limit / size) + 2); // the payloads alone exceed the limit
            var payload = new byte[size];
            var records = Enumerable.Range(0, count).Select(_ => Record(payload)).ToList();

            await using var transaction = await connection.BeginTransactionAsync(Ct);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InsertAsync(connection, transaction, records, Ct));
            Assert.Contains("max_allowed_packet", ex.Message, StringComparison.Ordinal);

            // The connection and its transaction are intact: a statement still runs in it and it commits.
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT 1", transaction: transaction));
            await transaction.CommitAsync(Ct);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- MY-07: no silent insert into another database ---------------------------------------------------------------------

    [Fact]
    public async Task An_unqualified_outbox_rejects_a_connection_to_another_database()
    {
        RequireDatabase();
        var outboxDatabase = NewSchemaName();
        var otherDatabase = NewSchemaName();
        await CreateSchemaAsync(fixture, outboxDatabase);
        await CreateSchemaAsync(fixture, otherDatabase);
        await using var services = Build(ConnectionTo(outboxDatabase), schema: null);
        try
        {
            await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
            var outbox = services.GetRequiredService<IOutbox>();

            await using (var right = new MySqlConnection(ConnectionTo(outboxDatabase)))
            {
                await right.OpenAsync(Ct);
                await using var transaction = await right.BeginTransactionAsync(Ct);
                await outbox.EnqueueAsync(transaction, new TestMessage("right database"), Ct);
                await transaction.CommitAsync(Ct);
            }

            await using var wrong = new MySqlConnection(ConnectionTo(otherDatabase));
            await wrong.OpenAsync(Ct);
            await using var wrongTransaction = await wrong.BeginTransactionAsync(Ct);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.EnqueueAsync(wrongTransaction, new TestMessage("wrong database"), Ct));
            Assert.Contains("MySqlOutboxOptions.Schema", ex.Message, StringComparison.Ordinal);
            Assert.Equal(1, (await services.GetRequiredService<IOutboxAdmin>().GetStatisticsAsync(Ct)).Pending);
        }
        finally
        {
            await DropSchemaAsync(fixture, outboxDatabase);
            await DropSchemaAsync(fixture, otherDatabase);
        }
    }

    // ---- MY-08 / MY-09: table shape ------------------------------------------------------------------------------------------

    [Fact]
    public async Task EnsureCreated_and_the_health_check_reject_a_table_of_the_wrong_shape()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        await using var services = Build(fixture.ConnectionString, schema);
        try
        {
            await using (var connection = await fixture.OpenAsync())
            {
                await connection.ExecuteAsync($"CREATE TABLE {Quote(schema)}.outbox_messages (id BINARY(16) NOT NULL PRIMARY KEY, payload LONGBLOB NOT NULL)");
            }

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct));
            Assert.Contains("does not have the shape", ex.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task The_health_check_fails_while_an_index_is_missing()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            await store.CheckHealthAsync(Ct);
            await using var connection = await fixture.OpenAsync();
            await connection.ExecuteAsync($"ALTER TABLE {Quote(schema)}.outbox_messages DROP INDEX ix_outbox_messages_dead");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckHealthAsync(Ct));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task The_table_can_be_created_again_after_the_old_one_is_renamed_aside()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            await using (var connection = await fixture.OpenAsync())
            {
                await connection.ExecuteAsync($"RENAME TABLE {Quote(schema)}.outbox_messages TO {Quote(schema)}.outbox_messages_old");
            }

            await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct); // no constraint-name collision (error 3822)
            await services.GetRequiredService<IOutboxStore>().CheckHealthAsync(Ct);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- MY-01 / MY-06 / MY-16: payload_size, token-guarded batches, connection options, plans ----------------------------

    [Fact]
    public async Task Payload_size_is_stored_at_insert()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var record = Record(new byte[12345]);
            await services.GetRequiredService<IOutboxStore>().InsertStandaloneAsync([record], Ct);
            await using var connection = await fixture.OpenAsync();
            Assert.Equal(12345L, await connection.ExecuteScalarAsync<long>(
                $"SELECT payload_size FROM {Quote(schema)}.outbox_messages WHERE id = UNHEX(@Id)", new { Id = Hex(record.Id) }));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Claims_and_acknowledgements_work_with_either_affected_rows_setting(bool useAffectedRows)
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync(ConnectionTo(string.Empty, b => b.UseAffectedRows = useAffectedRows));
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            var records = Enumerable.Range(0, 4).Select(_ => Record()).ToList();
            await store.InsertStandaloneAsync(records, Ct);
            var token = Guid.NewGuid();
            var claimed = await store.ClaimAsync(Claim(token: token), Ct);
            Assert.Equal(4, claimed.Count);

            Assert.True(await store.MarkDeliveredAsync(claimed[0].Id, token, Ct));
            Assert.False(await store.MarkDeliveredAsync(claimed[0].Id, token, Ct)); // the token is gone
            var batch = claimed.Skip(1).Select(m => new OutboxAcknowledgement(m.Id, token)).Append(new OutboxAcknowledgement(claimed[1].Id, Guid.NewGuid())).ToList();
            var updated = await ((IOutboxBatchStore)store).MarkDeliveredBatchAsync(batch, Ct);
            Assert.Equal(3, updated.Count);
            Assert.Equal(4, (await services.GetRequiredService<IOutboxAdmin>().GetStatisticsAsync(Ct)).Delivered);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    [Fact]
    public async Task Primary_key_statements_use_the_primary_key()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var sql = new MySqlOutboxSql(schema, "outbox_messages");
            var store = services.GetRequiredService<IOutboxStore>();
            var records = Enumerable.Range(0, 50).Select(_ => Record()).ToList();
            await store.InsertStandaloneAsync(records, Ct);
            var token = Guid.NewGuid();
            var leased = (await store.ClaimAsync(Claim(batch: 1, token: token), Ct))[0];
            await using var connection = await fixture.OpenAsync();
            await connection.ExecuteAsync($"ANALYZE TABLE {sql.Table}");

            // A real, leased row: on a row that does not exist, MySQL reports "Impossible WHERE noticed after reading const tables"
            // instead of the key, although it did read the primary key to find that out.
            var id = "'" + Hex(leased.Id) + "'";
            var lockToken = "'" + Hex(token) + "'";
            var other = "'" + Hex(records.First(r => r.Id != leased.Id).Id) + "'";
            string Bind(string statement) => statement
                .Replace("@id", id, StringComparison.Ordinal).Replace("@lock_token", lockToken, StringComparison.Ordinal)
                .Replace("@i0", id, StringComparison.Ordinal).Replace("@i1", other, StringComparison.Ordinal)
                .Replace("@t0", lockToken, StringComparison.Ordinal).Replace("@t1", lockToken, StringComparison.Ordinal)
                .Replace("@worker_id", "'w'", StringComparison.Ordinal).Replace("@lease_us", "1", StringComparison.Ordinal)
                .Replace("@delay_us", "1", StringComparison.Ordinal).Replace("@error", "'e'", StringComparison.Ordinal);

            foreach (var statement in new[]
            {
                sql.MarkDelivered, sql.ScheduleRetry, sql.MarkDeadLettered, sql.Release, sql.SelectById, sql.Requeue,
                sql.GetClaimUpdate(2), sql.GetClaimedRows(2), sql.GetLockForBatch(2), sql.GetBatchUpdate(2, delivered: true),
            })
            {
                var plan = await ExplainKeysAsync(connection, Bind(statement));
                var usesKey = plan.Contains("key=PRIMARY", StringComparison.Ordinal) || plan.Contains("after reading const tables", StringComparison.Ordinal);
                Assert.True(usesKey, $"Expected the primary key:\n{plan}\nfor: {statement}");
                Assert.False(plan.Contains("type=ALL", StringComparison.Ordinal), $"Full scan:\n{plan}\nfor: {statement}");
            }
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    private static async Task<string> ExplainKeysAsync(MySqlConnection connection, string statement)
    {
        // MySQL 9 changed EXPLAIN's default format; MariaDB has only the traditional one.
        var explain = connection.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? "EXPLAIN " : "EXPLAIN FORMAT=TRADITIONAL ";
        await using var command = new MySqlCommand(explain + statement, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            string Column(string name) => reader.GetOrdinal(name) is var i && !reader.IsDBNull(i) ? Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)! : "-";
            lines.Add($"type={Column("type")} key={Column("key")} extra={Column("Extra")}");
        }

        return string.Join('\n', lines);
    }
}
