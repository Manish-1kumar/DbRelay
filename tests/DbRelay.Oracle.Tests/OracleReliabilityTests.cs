using System.Collections.Concurrent;
using System.Data.Common;
using Dapper;
using DbRelay.Oracle.Internal;
using DbRelay.Storage;
using DbRelay.Storage.Conformance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using static DbRelay.Oracle.Tests.OracleTestHelpers;

namespace DbRelay.Oracle.Tests;

/// <summary>
/// Oracle tests for the review's follow-ups (OR-01, OR-04, OR-09, OR-12, OR-13): race gaps forced deterministically, cancellation,
/// index health, large LOBs, a deadlock stress test and the dispatcher end to end.
/// </summary>
public sealed class OracleReliabilityTests(OracleFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void RequireDatabase() => Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? string.Empty);

    private async Task<(ServiceProvider Services, string Schema)> CreateAsync()
    {
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            relay.UseOracle(fixture.ConnectionString, o => o.Schema = schema);
            relay.DisableDispatcher();
        });
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await provider.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct);
        return (provider, schema);
    }

    private static OutboxRecord Record(byte[]? payload = null, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        MessageType = "oracle.reliability",
        Payload = payload ?? "{}"u8.ToArray(),
        ContentType = "application/json",
    };

    private static ClaimRequest Claim(int batch = 100, TimeSpan? lease = null, Guid? token = null) =>
        new(batch, lease ?? TimeSpan.FromMinutes(5), token ?? Guid.NewGuid(), "reliability");

    private async Task<int> StatusAsync(string schema, Guid id)
    {
        await using var connection = await fixture.OpenAsync();
        return await connection.ExecuteScalarAsync<int>(
            $"SELECT STATUS FROM {Quote(schema)}.\"OUTBOX_MESSAGES\" WHERE ID = :Id", new { Id = id.ToByteArray() });
    }

    // ---- OR-01: a row changed between the claim choosing it and locking it is not claimed ------------------------------------

    [Fact]
    public async Task A_row_delivered_while_the_claim_cursor_is_open_is_not_claimed_again()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = (OracleOutboxStore)services.GetRequiredService<IOutboxStore>();
            var record = Record();
            await store.InsertStandaloneAsync([record], Ct);

            // A first worker claims and holds the message; then its lease expires, so the row is due again.
            var first = Guid.NewGuid();
            Assert.Single(await store.ClaimAsync(Claim(token: first), Ct));
            await using (var connection = await fixture.OpenAsync())
            {
                await connection.ExecuteAsync(
                    $"UPDATE {Quote(schema)}.\"OUTBOX_MESSAGES\" SET AVAILABLE_AT = SYS_EXTRACT_UTC(SYSTIMESTAMP) - INTERVAL '1' SECOND WHERE ID = :Id",
                    new { Id = record.Id.ToByteArray() });
            }

            // A second worker's claim opens its cursor (the row is a candidate in its snapshot) and pauses before fetching. In that gap,
            // the first worker's acknowledgement arrives and commits.
            store.ClaimTestPause = TimeSpan.FromSeconds(3);
            var second = store.ClaimAsync(Claim(), Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.True(await store.MarkDeliveredAsync(record.Id, first, Ct));

            // The second claim locks the row's current version, sees it is delivered, and leaves it alone.
            Assert.Empty(await second);
            store.ClaimTestPause = TimeSpan.Zero;
            Assert.Equal(2, await StatusAsync(schema, record.Id));
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- OR-09: cancellation surfaces as OperationCanceledException ----------------------------------------------------------

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

            // Another session holds the row lock, so the acknowledgement waits until it is cancelled.
            await using var blocker = await fixture.OpenAsync();
            await using var transaction = await blocker.BeginTransactionAsync(Ct);
            await blocker.ExecuteAsync(
                $"SELECT ID FROM {Quote(schema)}.\"OUTBOX_MESSAGES\" WHERE ID = :Id FOR UPDATE", new { Id = record.Id.ToByteArray() }, transaction);

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

    // ---- OR-12: the health check fails while an index is missing or unusable -------------------------------------------------

    [Fact]
    public async Task Health_check_fails_while_an_index_is_missing_or_unusable()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = services.GetRequiredService<IOutboxStore>();
            await store.CheckHealthAsync(Ct);
            await using var connection = await fixture.OpenAsync();

            await connection.ExecuteAsync($"ALTER INDEX {Quote(schema)}.\"IX_OUTBOX_MESSAGES_CLEANUP\" UNUSABLE");
            var unusable = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckHealthAsync(Ct));
            Assert.Contains("missing or unusable", unusable.Message, StringComparison.Ordinal);
            await connection.ExecuteAsync($"ALTER INDEX {Quote(schema)}.\"IX_OUTBOX_MESSAGES_CLEANUP\" REBUILD");
            await store.CheckHealthAsync(Ct);

            await connection.ExecuteAsync($"DROP INDEX {Quote(schema)}.\"IX_OUTBOX_MESSAGES_CLAIM\"");
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckHealthAsync(Ct));
            await services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync(Ct); // recreates what is missing
            await store.CheckHealthAsync(Ct);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- OR-04 / OR-13: the largest batch, and large LOBs ---------------------------------------------------------------------

    [Fact]
    public async Task The_largest_enqueue_batch_is_one_atomic_statement_and_rolls_back_with_the_caller()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var outbox = services.GetRequiredService<IOutbox>();
            var admin = services.GetRequiredService<IOutboxAdmin>();
            var messages = Enumerable.Range(0, OutboxLimits.MaxEnqueueBatchSize).Select(i => new TestMessage($"m{i}")).ToList();

            await using var connection = await fixture.OpenAsync();
            IReadOnlyList<Guid> rolledBack;
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                rolledBack = await outbox.EnqueueManyAsync(transaction, messages, Ct);
                await transaction.RollbackAsync(Ct);
            }

            IReadOnlyList<Guid> committed;
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                committed = await outbox.EnqueueManyAsync(transaction, messages, Ct);
                await transaction.CommitAsync(Ct);
            }

            Assert.Null(await admin.GetAsync(rolledBack[^1], Ct));
            Assert.Equal(OutboxLimits.MaxEnqueueBatchSize, (await admin.GetStatisticsAsync(Ct)).Pending);
            var last = await admin.GetAsync(committed[^1], Ct);
            Assert.Equal($"{{\"text\":\"m{OutboxLimits.MaxEnqueueBatchSize - 1}\"}}", System.Text.Encoding.UTF8.GetString(last!.Payload.Span));

            // A failure in the last statement of the block leaves nothing behind, even inside a transaction the caller then commits.
            var store = services.GetRequiredService<IOutboxStore>();
            var records = Enumerable.Range(0, OutboxLimits.MaxEnqueueBatchSize).Select(_ => Record()).ToList();
            records[^1] = Record(id: records[0].Id);
            await using (var transaction = await connection.BeginTransactionAsync(Ct))
            {
                await Assert.ThrowsAnyAsync<DbException>(() => store.InsertAsync(connection, transaction, records, Ct));
                await transaction.CommitAsync(Ct);
            }

            Assert.Null(await admin.GetAsync(records[1].Id, Ct));
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

            // One full INSERT ALL of 128 KiB payloads, and one 8 MiB payload on its own.
            var records = Enumerable.Range(0, OracleOutboxSql.MaxInsertRowsPerStatement).Select(_ => Record(Payload(128 * 1024))).ToList();
            await store.InsertStandaloneAsync(records, Ct);
            var large = Record(Payload(8 * 1024 * 1024));
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

    // ---- OR-13: a BA-07-style stress test for deadlocks ---------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_claims_batches_stale_acknowledgements_and_cleanup_never_deadlock()
    {
        RequireDatabase();
        var (services, schema) = await CreateAsync();
        await using var _ = services;
        try
        {
            var store = (OracleOutboxStore)services.GetRequiredService<IOutboxStore>();
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

                        // About a third of the batches outlive their 800 ms lease, so other workers reclaim them and these tokens go stale.
                        await Task.Delay(random.Next(0, 1200), stop.Token);
                        var items = claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).OrderBy(_ => random.Next()).ToList();
                        if (random.Next(4) == 0)
                        {
                            // The same message twice in one call, with a stale token.
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
                        // A retention longer than any late, stale acknowledgement (docs/configuration.md).
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
            Assert.Equal(1, Volatile.Read(ref done)); // finished, not timed out
            Assert.Equal(ids.Order(), delivered.Keys.Order()); // every message delivered exactly once by token-guarded updates
            Assert.All(delivered.Values, count => Assert.Equal(1, count));
            var stats = await store.GetStatisticsAsync(Ct);
            Assert.Equal(0, stats.Pending + stats.Processing);

            var retries = store.DeadlockRetries;
            TestContext.Current.TestOutputHelper?.WriteLine($"ORA-00060 retries: {retries}");
            Assert.Equal(0, retries);
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    // ---- OR-13: the dispatcher end to end, including a shutdown under load ---------------------------------------------------

    [Fact]
    public async Task The_dispatcher_delivers_every_message_across_a_shutdown_without_logging_errors()
    {
        RequireDatabase();
        var schema = NewSchemaName();
        await CreateSchemaAsync(fixture, schema);
        var logs = new ErrorCountingLoggerProvider();
        var deliveries = new ConcurrentDictionary<Guid, int>();

        IHost BuildHost() => OracleHost(schema, logs, async (message, ct) =>
        {
            await Task.Delay(Random.Shared.Next(1, 20), ct);
            deliveries.AddOrUpdate(message.Id, 1, (_, n) => n + 1);
            return DeliveryResult.Delivered();
        });

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

                // Stop while messages are still being delivered: in-flight claims are released or kept, never lost.
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
            Assert.Empty(logs.Errors); // no error logged, including cancelled database calls during shutdown (OR-09)
        }
        finally
        {
            await DropSchemaAsync(fixture, schema);
        }
    }

    private IHost OracleHost(string schema, ErrorCountingLoggerProvider logs, Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> handler)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Services.AddDbRelay(relay =>
        {
            relay.UseOracle(fixture.ConnectionString, o => o.Schema = schema);
            relay.UseDeliveryHandler(handler);
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
