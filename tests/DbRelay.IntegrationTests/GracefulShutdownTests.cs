using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 7: graceful shutdown and shutdown-timeout cancellation against PostgreSQL.</summary>
public sealed class GracefulShutdownTests(PostgreSqlFixture fixture)
{
    private const int Messages = 20;
    private const int Concurrency = 4;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StopAsync_finishes_in_flight_deliveries_and_releases_the_rest_with_attempts_restored()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        var ids = await EnqueueAsync(schema);
        var appName = "dbrelay-it-shutdown-" + Guid.NewGuid().ToString("N")[..12];

        var started = new ConcurrentQueue<Guid>();
        var cancelled = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RelayHost.Build(
            schema,
            appName,
            async (message, ct) =>
            {
                started.Enqueue(message.Id);
                if (started.Count >= Concurrency)
                {
                    allStarted.TrySetResult();
                }

                await finish.Task;
                if (ct.IsCancellationRequested)
                {
                    Interlocked.Increment(ref cancelled);
                }

                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.BatchSize = Messages;
                d.MaxConcurrency = Concurrency;
                d.LeaseDuration = TimeSpan.FromSeconds(60);
                d.DeliveryTimeout = TimeSpan.FromSeconds(20);
            },
            shutdownTimeout: TimeSpan.FromSeconds(30));

        await host.StartAsync(Ct);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // One claim took the whole batch: 4 deliveries in flight, 16 claimed and waiting for a slot.
        var before = await schema.CountByStatusAsync();
        Assert.Equal(Messages, before.GetValueOrDefault(OutboxMessageStatus.Processing));
        Assert.All(await schema.GetRowsAsync(), r => Assert.Equal(1, r.AttemptCount));

        // Invariant 3: no database transaction (and no row lock) is held while handlers run.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            var sessions = await Dapper.SqlMapper.QueryAsync<(int Pid, string? State, DateTime? XactStart)>(
                connection,
                "SELECT pid, state, xact_start FROM pg_stat_activity WHERE application_name = @appName",
                new { appName });
            Assert.All(sessions, s => Assert.True(s.XactStart is null, $"Dispatcher session {s.Pid} holds an open transaction ({s.State}) during delivery."));
            var locks = await Dapper.SqlMapper.ExecuteScalarAsync<long>(
                connection,
                "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE a.application_name = @appName AND l.locktype IN ('relation', 'tuple', 'transactionid')",
                new { appName });
            Assert.Equal(0, locks);
        }

        var stopping = host.StopAsync(Ct);

        // While StopAsync waits for in-flight work, the unstarted claims are already back in the queue.
        await Wait.UntilAsync(
            async () => (await schema.CountByStatusAsync()).GetValueOrDefault(OutboxMessageStatus.Pending) == Messages - Concurrency,
            TimeSpan.FromSeconds(15),
            "unstarted messages to be released");
        Assert.False(stopping.IsCompleted, "StopAsync returned before in-flight deliveries finished.");
        var inFlight = started.ToHashSet();
        Assert.Equal(Concurrency, inFlight.Count);
        Assert.All(
            (await schema.GetRowsAsync()).Where(r => inFlight.Contains(r.Id)),
            r => Assert.Equal(OutboxMessageStatus.Processing, r.State));

        finish.SetResult();
        await stopping.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var rows = await schema.GetRowsAsync();
        Assert.Equal(ids.Count, rows.Count);
        Assert.DoesNotContain(rows, r => r.State == OutboxMessageStatus.Processing);
        Assert.All(rows.Where(r => inFlight.Contains(r.Id)), r =>
        {
            Assert.Equal(OutboxMessageStatus.Delivered, r.State);
            Assert.Equal(1, r.AttemptCount);
        });
        Assert.All(rows.Where(r => !inFlight.Contains(r.Id)), r =>
        {
            Assert.Equal(OutboxMessageStatus.Pending, r.State);
            Assert.Equal(0, r.AttemptCount); // the release gave the attempt back
            Assert.Null(r.LockToken);
            Assert.Null(r.LockedBy);
            Assert.True(r.AvailableAt <= r.DbNow, "A released message must be immediately claimable.");
        });
        Assert.Equal(0, cancelled); // a graceful stop does not cancel in-flight handlers
        Assert.Equal(Concurrency, started.Count);
    }

    [Fact]
    public async Task Shutdown_timeout_cancels_in_flight_deliveries_and_returns_every_claim()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        var ids = await EnqueueAsync(schema);
        var started = 0;
        var observedCancellation = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-shutdown-timeout",
            async (_, ct) =>
            {
                if (Interlocked.Increment(ref started) >= Concurrency)
                {
                    allStarted.TrySetResult();
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref observedCancellation);
                    throw;
                }

                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.BatchSize = Messages;
                d.MaxConcurrency = Concurrency;
                d.LeaseDuration = TimeSpan.FromSeconds(60);
                d.DeliveryTimeout = TimeSpan.FromSeconds(20);
            },
            shutdownTimeout: TimeSpan.FromSeconds(1));

        await host.StartAsync(Ct);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.StopAsync(Ct);

        var rows = await schema.GetRowsAsync();
        Assert.Equal(ids.Count, rows.Count);
        Assert.Equal(Concurrency, observedCancellation);
        Assert.All(rows, r =>
        {
            Assert.Equal(OutboxMessageStatus.Pending, r.State);
            Assert.Null(r.LockToken);
        });

        // Revised after review finding CR-06. In-flight deliveries interrupted by the shutdown timeout ran their handler, so they keep
        // the attempt (otherwise a handler that always outlasts the shutdown window would never reach dead-letter) and record why.
        var interrupted = rows.Where(r => r.AttemptCount == 1).ToList();
        Assert.Equal(Concurrency, interrupted.Count);
        Assert.All(interrupted, r => Assert.Equal("Delivery was interrupted by host shutdown.", r.LastError));

        // Claimed but never started: released with the attempt given back and no error.
        var unstarted = rows.Where(r => r.AttemptCount == 0).ToList();
        Assert.Equal(ids.Count - Concurrency, unstarted.Count);
        Assert.All(unstarted, r => Assert.Null(r.LastError));
    }

    [Fact]
    public async Task Shutdown_is_prompt_even_when_the_claim_is_blocked_by_the_database()
    {
        // FU-04: the claim is not cancelled by the stopping token (CR-09), but it must be cancelled by the shutdown timeout. A hung
        // database must not keep the dispatcher running after StopAsync returns.
        await using var schema = await TestSchema.CreateAsync(fixture);
        var logs = new CapturingLoggerProvider();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-blocked-claim",
            (_, _) => Task.FromResult(DeliveryResult.Delivered()),
            d => d.PollingInterval = TimeSpan.FromMilliseconds(50),
            logs: logs,
            shutdownTimeout: TimeSpan.FromSeconds(2));

        await using var blocker = await fixture.DataSource.OpenConnectionAsync(Ct);
        await using var lockTransaction = await blocker.BeginTransactionAsync(Ct);
        await blocker.ExecuteAsync($"LOCK TABLE {schema.Outbox} IN ACCESS EXCLUSIVE MODE", transaction: lockTransaction);

        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () =>
            {
                await using var probe = await fixture.DataSource.OpenConnectionAsync(Ct);
                return await probe.ExecuteScalarAsync<int>(
                    "SELECT count(*) FROM pg_stat_activity WHERE application_name LIKE 'dbrelay-it-blocked-claim%' AND wait_event_type = 'Lock'") > 0;
            },
            TimeSpan.FromSeconds(30),
            "the dispatcher's claim to block on the table lock");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await host.StopAsync(Ct);
        var stopReturned = stopwatch.Elapsed;
        await Wait.UntilAsync(() => logs.Count(1001) == 1, TimeSpan.FromSeconds(10), "the dispatcher to log that it stopped");
        var dispatcherStopped = stopwatch.Elapsed;
        await lockTransaction.RollbackAsync(Ct);

        // 2 s shutdown timeout + 5 s release grace period, with generous slack for a loaded machine. Before the fix the dispatcher
        // kept running for the full 15 s claim timeout after StopAsync returned.
        Assert.True(stopReturned < TimeSpan.FromSeconds(10), $"StopAsync took {stopReturned}");
        Assert.True(dispatcherStopped < TimeSpan.FromSeconds(10), $"the dispatcher stopped after {dispatcherStopped}");
    }

    private static async Task<List<Guid>> EnqueueAsync(TestSchema schema)
    {
        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();
        var ids = new List<Guid>();
        for (var i = 0; i < Messages; i++)
        {
            ids.Add(await schema.EnqueueOrderAsync(outbox, i, cancellationToken: Ct));
        }

        return ids;
    }
}
