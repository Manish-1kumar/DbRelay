using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using DbRelay.Configuration;
using DbRelay.Diagnostics;
using DbRelay.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DbRelay.Tests;

/// <summary>
/// Dispatcher behaviour against the in-memory store, running inside a real generic host.
/// </summary>
public sealed class DispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IHost CreateHost(
        InMemoryOutboxStore store,
        Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> handler,
        Action<DispatcherOptions>? dispatcher = null,
        Action<RetryOptions>? retry = null,
        TimeSpan? shutdownTimeout = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10));
        builder.Services.AddDbRelay(relay =>
        {
            relay.UseStorage(_ => store);
            relay.UseDeliveryHandler(handler);
            relay.ConfigureDispatcher(d =>
            {
                d.PollingInterval = TimeSpan.FromMilliseconds(20);
                d.LeaseDuration = TimeSpan.FromSeconds(30);
                d.DeliveryTimeout = TimeSpan.FromSeconds(10);
                d.MaxErrorBackoff = TimeSpan.FromSeconds(1);
                dispatcher?.Invoke(d);
            });
            relay.ConfigureRetry(r =>
            {
                r.InitialDelay = TimeSpan.FromMilliseconds(1);
                r.MaxDelay = TimeSpan.FromMilliseconds(5);
                retry?.Invoke(r);
            });
            relay.ConfigureMaintenance(m => m.StatisticsInterval = null);
        });
        builder.Services.AddHealthChecks().AddDbRelay();
        return builder.Build();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task Delivers_messages_and_records_success()
    {
        var store = new InMemoryOutboxStore();
        var ids = Enumerable.Range(0, 25).Select(i => store.Add($"m{i}")).ToList();
        var delivered = new ConcurrentBag<Guid>();
        using var host = CreateHost(store, (m, _) =>
        {
            Assert.Equal(1, m.Attempt);
            delivered.Add(m.Id);
            return Task.FromResult(DeliveryResult.Delivered());
        }, d => d.BatchSize = 10);

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.CountByStatus().GetValueOrDefault(OutboxMessageStatus.Delivered) == 25);
        await host.StopAsync(Ct);

        Assert.Equal(ids.OrderBy(i => i), delivered.OrderBy(i => i));
        Assert.All(ids, id => Assert.NotNull(store.Get(id).DeliveredAt));
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_then_dead_lettered()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        var attempts = new ConcurrentBag<int>();
        using var host = CreateHost(store, (m, _) =>
        {
            attempts.Add(m.Attempt);
            return Task.FromResult(DeliveryResult.Retry("broker busy"));
        }, retry: r => r.MaxAttempts = 4);

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.DeadLettered);
        await host.StopAsync(Ct);

        Assert.Equal([1, 2, 3, 4], attempts.OrderBy(a => a));
        var row = store.Get(id);
        Assert.Equal(4, row.AttemptCount);
        Assert.Contains("Max attempts (4) reached", row.LastError, StringComparison.Ordinal);
        Assert.Contains("broker busy", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exceptions_are_transient_and_succeed_on_a_later_attempt()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        using var host = CreateHost(store, (m, _) => m.Attempt < 3
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(DeliveryResult.Delivered()));

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);

        var row = store.Get(id);
        Assert.Equal(3, row.AttemptCount);
        Assert.Equal("HttpRequestException: connection refused", row.LastError);
    }

    [Fact]
    public async Task Handler_retry_after_overrides_backoff_and_is_capped()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        using var host = CreateHost(
            store,
            (_, _) => Task.FromResult(DeliveryResult.Retry("429", TimeSpan.FromHours(1))),
            retry: r => r.MaxDelay = TimeSpan.FromSeconds(42));

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.LastDelay(id) is not null);
        await host.StopAsync(Ct);

        Assert.Equal(TimeSpan.FromSeconds(42), store.LastDelay(id));
        Assert.Equal(OutboxMessageStatus.Pending, store.Get(id).Status);
    }

    [Fact]
    public async Task Dead_letter_result_and_permanent_exceptions_skip_retries()
    {
        var store = new InMemoryOutboxStore();
        var rejected = store.Add("reject");
        var invalid = store.Add("invalid");
        using var host = CreateHost(
            store,
            (m, _) => m.GetPayloadAsString() == "reject"
                ? Task.FromResult(DeliveryResult.DeadLetter("schema rejected"))
                : throw new FormatException("bad payload"),
            retry: r => r.IsPermanentFailure = ex => ex is FormatException);

        await host.StartAsync(Ct);
        await WaitUntilAsync(() =>
            store.Get(rejected).Status == OutboxMessageStatus.DeadLettered && store.Get(invalid).Status == OutboxMessageStatus.DeadLettered);
        await host.StopAsync(Ct);

        Assert.Equal(1, store.Get(rejected).AttemptCount);
        Assert.Equal("schema rejected", store.Get(rejected).LastError);
        Assert.Equal(1, store.Get(invalid).AttemptCount);
        Assert.Equal("FormatException: bad payload", store.Get(invalid).LastError);
    }

    [Fact]
    public async Task A_throwing_failure_classifier_is_treated_as_transient()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        using var host = CreateHost(
            store,
            (m, _) => m.Attempt == 1 ? throw new InvalidOperationException("x") : Task.FromResult(DeliveryResult.Delivered()),
            retry: r => r.IsPermanentFailure = _ => throw new NotSupportedException());

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Null_results_are_treated_as_failures()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        using var host = CreateHost(store, (_, _) => Task.FromResult<DeliveryResult>(null!), retry: r => r.MaxAttempts = 1);

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.DeadLettered);
        await host.StopAsync(Ct);

        Assert.Contains("returned null", store.Get(id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deliveries_exceeding_the_timeout_are_cancelled_and_retried()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        using var host = CreateHost(
            store,
            async (m, ct) =>
            {
                if (m.Attempt == 1)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.DeliveryTimeout = TimeSpan.FromMilliseconds(200);
                d.LeaseDuration = TimeSpan.FromSeconds(10);
            });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);

        Assert.Equal(2, store.Get(id).AttemptCount);
        Assert.StartsWith("TimeoutException", store.Get(id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handlers_that_ignore_cancellation_are_abandoned_and_do_not_hold_slots()
    {
        // CR-05: a handler that blocks synchronously and ignores its token must not hold a concurrency slot forever.
        var store = new InMemoryOutboxStore();
        var hung = store.Add("hung");
        var other = store.Add("other");
        using var release = new ManualResetEventSlim(false);
        using var host = CreateHost(store, (m, _) =>
        {
            if (m.GetPayloadAsString() == "hung" && m.Attempt == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30)); // ignores the cancellation token on purpose
            }

            return Task.FromResult(DeliveryResult.Delivered());
        }, d =>
        {
            d.MaxConcurrency = 1; // only one slot: if it were held forever, "other" could never be delivered
            d.DeliveryTimeout = TimeSpan.FromMilliseconds(300);
            d.LeaseDuration = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() =>
            store.Get(other).Status == OutboxMessageStatus.Delivered && store.Get(hung).Status == OutboxMessageStatus.Delivered);
        release.Set();
        await host.StopAsync(Ct);

        var row = store.Get(hung);
        Assert.Equal(2, row.AttemptCount); // attempt 1 abandoned, attempt 2 delivered
        Assert.Contains("did not honour cancellation", row.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_rows_are_dead_lettered_without_calling_the_handler_and_do_not_block_the_batch()
    {
        // FU-01 / CR-01 defence in depth: a provider returns a placeholder for a row it cannot map.
        var store = new InMemoryOutboxStore();
        var bad = store.Add("bad");
        var good = store.Add("good");
        store.MarkUnreadable(bad);
        var delivered = new ConcurrentBag<Guid>();
        using var host = CreateHost(store, (m, _) =>
        {
            delivered.Add(m.Id);
            return Task.FromResult(DeliveryResult.Delivered());
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() =>
            store.Get(bad).Status == OutboxMessageStatus.DeadLettered && store.Get(good).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);

        Assert.Equal([good], delivered);
        Assert.Contains("could not be read", store.Get(bad).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Claiming_pauses_while_abandoned_handlers_fill_every_slot()
    {
        // FU-02: orphans are capped at MaxConcurrency; the dispatcher stops claiming and reports Degraded until they finish.
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 10; i++)
        {
            store.Add($"hang{i}");
        }

        using var release = new ManualResetEventSlim(false);
        using var host = CreateHost(store, (_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(60)); // ignores cancellation
            return Task.FromResult(DeliveryResult.Delivered());
        }, d =>
        {
            d.MaxConcurrency = 2;
            d.DeliveryTimeout = TimeSpan.FromMilliseconds(200);
            d.LeaseDuration = TimeSpan.FromSeconds(5);
        });

        await host.StartAsync(Ct);
        var state = host.Services.GetRequiredService<DbRelay.Dispatching.DispatcherState>();
        const int Limit = 4;          // max(2, 2 x MaxConcurrency)
        const int InFlightAtLimit = 2; // MaxConcurrency: handlers already running when the limit is hit may still be abandoned
        await WaitUntilAsync(() => state.AbandonedHandlers >= Limit);
        await Task.Delay(800, Ct);     // let in-flight handlers reach their own abandonment
        var claimsAtCap = store.ClaimCalls;
        var orphansAtCap = state.AbandonedHandlers;
        await Task.Delay(1000, Ct);

        Assert.InRange(orphansAtCap, Limit, Limit + InFlightAtLimit); // bounded, not growing with messages x attempts
        Assert.Equal(orphansAtCap, state.AbandonedHandlers);          // stable: nothing new was started
        Assert.Equal(claimsAtCap, store.ClaimCalls);                  // no claims while at the limit
        var health = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        Assert.Equal(HealthStatus.Degraded, health.Status);

        release.Set();
        await WaitUntilAsync(() => state.AbandonedHandlers == 0);
        await WaitUntilAsync(() => store.CountByStatus().GetValueOrDefault(OutboxMessageStatus.Delivered) == 10, 20_000);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Ambiguous_acknowledgement_after_a_reclaim_is_still_reported_as_a_lost_lease()
    {
        // FU-T3 / FU-05: the re-read must match this claim (attempt count), not just this worker.
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        store.CommitThenFailOnce = true;
        store.BumpAttemptAfterAmbiguousCommit = true; // simulate a reclaim by the same worker id between the commit and the re-read
        var leaseLost = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DbRelayDiagnostics.MeterName && instrument.Name == "dbrelay.lease.lost")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref leaseLost, value));
        listener.Start();

        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()));
        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.HasOperation(id, "delivered:rejected"));
        await WaitUntilAsync(() => Interlocked.Read(ref leaseLost) >= 1);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Delivered_acknowledgements_are_batched_under_concurrent_load()
    {
        // ADR 0003: acknowledgements that arrive while a round trip is in flight share the next one.
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 300; i++)
        {
            store.Add($"m{i}");
        }

        using var host = CreateHost(store, async (_, ct) =>
        {
            await Task.Delay(5, ct);
            return DeliveryResult.Delivered();
        }, d =>
        {
            d.MaxConcurrency = 32;
            d.BatchSize = 300;
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.CountByStatus().GetValueOrDefault(OutboxMessageStatus.Delivered) == 300);
        await host.StopAsync(Ct);

        // Batches of one take the cheaper single-row statement (fast path), so only multi-item batches appear here.
        var sizes = store.DeliveredBatchSizes.ToArray();
        Assert.NotEmpty(sizes);
        Assert.All(sizes, size => Assert.True(size > 1));
        Assert.InRange(sizes.Sum(), 2, 300);
        Assert.True(sizes.Max() > 1, $"expected some batching; batch sizes were {string.Join(",", sizes)}");
    }

    [Fact]
    public async Task Acknowledgement_batcher_completes_every_waiter_under_heavy_contention_smoke()
    {
        // Smoke test only (BA-11): heavy contention with every waiter required to finish. It does NOT reliably catch the lost-wake-up
        // race (the window is too narrow); A_follower_that_enqueues_just_before_the_leader_releases_is_not_stranded is the regression test.
        var store = new InMemoryOutboxStore();
        var ids = Enumerable.Range(0, 4000).Select(i => store.Add($"m{i}")).ToList();
        var token = Guid.NewGuid();
        var claimed = await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(1000, TimeSpan.FromMinutes(5), token, "w"), Ct);
        claimed = [.. claimed, .. await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(1000, TimeSpan.FromMinutes(5), token, "w"), Ct)];
        claimed = [.. claimed, .. await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(1000, TimeSpan.FromMinutes(5), token, "w"), Ct)];
        claimed = [.. claimed, .. await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(1000, TimeSpan.FromMinutes(5), token, "w"), Ct)];
        Assert.Equal(4000, claimed.Count);

        foreach (var maxConcurrency in new[] { 1, 8, 32 })
        {
            using var batcher = new DbRelay.Dispatching.AcknowledgementBatcher(
                store, store, maxConcurrency, (_, _) => Task.FromResult(false),
                new DbRelayDiagnosticsHolder().Metrics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            var slice = claimed.Skip(maxConcurrency switch { 1 => 0, 8 => 1000, _ => 2000 }).Take(1000).ToList();
            var results = await Task.WhenAll(slice.Select(m => Task.Run(() => batcher.MarkDeliveredAsync(m, token), Ct)))
                .WaitAsync(TimeSpan.FromSeconds(30), Ct); // a stranded waiter would hang here
            Assert.All(results, r => Assert.True(r));
            await batcher.CompleteAsync();
        }
    }

    [Fact]
    public async Task A_follower_that_enqueues_just_before_the_leader_releases_is_not_stranded()
    {
        // Deterministic version of the lost-wake-up race. The leader has drained (its own item is done) and is about to release;
        // a follower enqueues in exactly that window. The leader must re-check the queue and flush the follower.
        var store = new InMemoryOutboxStore();
        store.Add("leader");
        store.Add("follower");
        var token = Guid.NewGuid();
        var claimed = await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(10, TimeSpan.FromMinutes(5), token, "w"), Ct);
        using var batcher = new DbRelay.Dispatching.AcknowledgementBatcher(
            store, store, maxConcurrency: 1, (_, _) => Task.FromResult(false),
            new DbRelayDiagnosticsHolder().Metrics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Task<bool?>? follower = null;
        batcher.BeforeLeaderReleaseForTesting = async () =>
        {
            batcher.BeforeLeaderReleaseForTesting = null; // only the first release
            follower = Task.Run(() => batcher.MarkDeliveredAsync(claimed[1], token), Ct);
            await WaitUntilAsync(() => batcher.QueuedCount == 1); // enqueued, and its Wait(0) failed because we still lead
            await Task.Delay(50, Ct);
        };

        Assert.True(await batcher.MarkDeliveredAsync(claimed[0], token));
        Assert.NotNull(follower);
        Assert.True(await follower.WaitAsync(TimeSpan.FromSeconds(5), Ct)); // stranded forever before the fix
    }

    [Fact]
    public async Task A_throwing_logger_and_a_failing_store_never_strand_waiters()
    {
        // BA-01: every log call throws and every store call fails; all waiters must still complete (with null) and none may hang.
        var store = new InMemoryOutboxStore { UpdateFailure = _ => new TimeoutException("db down") };
        for (var i = 0; i < 12; i++)
        {
            store.Add($"m{i}");
        }

        var token = Guid.NewGuid();
        var claimed = await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(100, TimeSpan.FromMinutes(5), token, "w"), Ct);
        using var batcher = new DbRelay.Dispatching.AcknowledgementBatcher(
            store, store, maxConcurrency: 8, (_, _) => Task.FromResult(false),
            new DbRelayDiagnosticsHolder().Metrics, TimeProvider.System, new ThrowingLogger());

        var results = await Task.WhenAll(claimed.Select(m => Task.Run(() => batcher.MarkDeliveredAsync(m, token), Ct)))
            .WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.All(results, r => Assert.Null(r));
        Assert.Equal(0, batcher.QueuedCount);
    }

    [Fact]
    public async Task A_multi_item_batch_that_committed_but_reported_failure_is_recovered_by_re_reading()
    {
        // BA-12: hold the leader's first (single-row) flush so two followers form a real 2-item batch, then make that batch's first
        // row commit and throw. The retry must not report the committed row as lost.
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 3; i++)
        {
            store.Add($"m{i}");
        }

        var token = Guid.NewGuid();
        var claimed = await store.ClaimAsync(new DbRelay.Storage.ClaimRequest(10, TimeSpan.FromMinutes(5), token, "w"), Ct);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSingleDelivered = async () =>
        {
            store.BeforeSingleDelivered = null; // only the leader's first flush
            entered.TrySetResult();
            await gate.Task;
        };

        using var batcher = new DbRelay.Dispatching.AcknowledgementBatcher(
            store, store, maxConcurrency: 1,
            (d, _) => Task.FromResult(store.Get(d.Id).Status == OutboxMessageStatus.Delivered), // the real check re-reads the row
            new DbRelayDiagnosticsHolder().Metrics, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        var leader = Task.Run(() => batcher.MarkDeliveredAsync(claimed[0], token), Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var followers = new[] { claimed[1], claimed[2] }.Select(m => Task.Run(() => batcher.MarkDeliveredAsync(m, token), Ct)).ToList();
        await WaitUntilAsync(() => batcher.QueuedCount == 2);
        store.CommitThenFailOnce = true; // the 2-item batch's first row commits, then the call "fails"
        gate.SetResult();

        var results = await Task.WhenAll([leader, .. followers]).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.All(results, r => Assert.True(r));
        Assert.Contains(2, store.DeliveredBatchSizes); // it really was a multi-item batch
        Assert.All(claimed, m => Assert.Equal(OutboxMessageStatus.Delivered, store.Get(m.Id).Status));
    }

    [Fact]
    public async Task Shutdown_falls_back_to_per_message_release_when_the_batched_release_fails()
    {
        // BA-12: if ReleaseBatchAsync throws, unstarted claims must still be released one by one.
        var store = new InMemoryOutboxStore { FailBatchRelease = true };
        for (var i = 0; i < 5; i++)
        {
            store.Add($"m{i}");
        }

        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        using var host = CreateHost(store, async (_, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            return DeliveryResult.Delivered();
        }, d => d.MaxConcurrency = 1);

        await host.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var stopping = host.StopAsync(Ct);
        await Task.Delay(200, Ct);
        finish.SetResult();
        await stopping;

        Assert.True(store.BatchReleaseCalls >= 1);
        var statuses = store.CountByStatus();
        Assert.Equal(1, statuses.GetValueOrDefault(OutboxMessageStatus.Delivered));
        Assert.Equal(4, statuses.GetValueOrDefault(OutboxMessageStatus.Pending));
        Assert.Equal(0, statuses.GetValueOrDefault(OutboxMessageStatus.Processing));
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logging provider failure");
    }

    private sealed class DbRelayDiagnosticsHolder
    {
        public DbRelayMetrics Metrics { get; } = new(new DbRelay.Dispatching.DispatcherState());
    }

    [Fact]
    public async Task Acknowledgements_are_written_one_by_one_when_batching_is_disabled()
    {
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 20; i++)
        {
            store.Add($"m{i}");
        }

        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()), d => d.BatchAcknowledgements = false);
        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.CountByStatus().GetValueOrDefault(OutboxMessageStatus.Delivered) == 20);
        await host.StopAsync(Ct);

        Assert.Empty(store.DeliveredBatchSizes);
    }

    [Fact]
    public async Task Crash_looping_messages_are_dead_lettered_without_delivery()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add(attemptCount: 5); // five earlier claims never recorded an outcome
        var invoked = false;
        using var host = CreateHost(store, (_, _) =>
        {
            invoked = true;
            return Task.FromResult(DeliveryResult.Delivered());
        }, retry: r => r.MaxAttempts = 5);

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.DeadLettered);
        await host.StopAsync(Ct);

        Assert.False(invoked);
        Assert.Contains("crash loop", store.Get(id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrency_never_exceeds_the_configured_maximum()
    {
        var store = new InMemoryOutboxStore();
        for (var i = 0; i < 40; i++)
        {
            store.Add();
        }

        var current = 0;
        var peak = 0;
        using var host = CreateHost(store, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(20, ct);
            Interlocked.Decrement(ref current);
            return DeliveryResult.Delivered();
        }, d =>
        {
            d.MaxConcurrency = 3;
            d.BatchSize = 7;
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.CountByStatus().GetValueOrDefault(OutboxMessageStatus.Delivered) == 40);
        await host.StopAsync(Ct);

        Assert.InRange(peak, 2, 3);
    }

    [Fact]
    public async Task Each_delivery_gets_its_own_scope()
    {
        var store = new InMemoryOutboxStore();
        store.Add();
        store.Add();
        var builder = Host.CreateApplicationBuilder();
        var scopes = new ConcurrentBag<ScopedDependency>();
        builder.Services.AddScoped<ScopedDependency>();
        builder.Services.AddSingleton(scopes);
        builder.Services.AddDbRelay(relay => relay
            .UseStorage(_ => store)
            .UseDeliveryHandler<ScopeRecordingHandler>()
            .ConfigureDispatcher(d => d.PollingInterval = TimeSpan.FromMilliseconds(20))
            .ConfigureMaintenance(m => m.StatisticsInterval = null));
        using var host = builder.Build();

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => scopes.Count == 2);
        await host.StopAsync(Ct);

        Assert.Equal(2, scopes.Distinct().Count());
        Assert.All(scopes, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task Database_outages_do_not_stop_the_dispatcher()
    {
        var store = new InMemoryOutboxStore { ClaimFailure = call => call <= 3 ? new TimeoutException("db down") : null };
        var id = store.Add();
        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()));

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.ClaimCalls >= 2);
        var degraded = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);

        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered);
        var healthy = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Ct);
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        await host.StopAsync(Ct);
    }

    [Fact]
    public async Task Lost_leases_are_detected_and_never_overwrite_the_new_owner()
    {
        var store = new InMemoryOutboxStore { StealLeaseBeforeUpdate = true };
        var id = store.Add();
        var leaseLost = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DbRelayDiagnostics.MeterName && instrument.Name == "dbrelay.lease.lost")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref leaseLost, value));
        listener.Start();

        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()));
        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.HasOperation(null, "delivered:rejected"));
        await host.StopAsync(Ct);

        Assert.Equal(OutboxMessageStatus.Processing, store.Get(id).Status); // still owned by the "other" worker
        Assert.True(Interlocked.Read(ref leaseLost) >= 1);
    }

    [Fact]
    public async Task Outcome_updates_are_retried_when_the_database_blips()
    {
        var failures = 0;
        var store = new InMemoryOutboxStore();
        store.UpdateFailure = _ => Interlocked.Increment(ref failures) <= 2 ? new TimeoutException("blip") : null;
        var id = store.Add();
        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()));

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);

        Assert.Equal(1, store.Get(id).AttemptCount);
    }

    [Fact]
    public async Task An_outcome_whose_reason_cannot_be_stored_is_recorded_with_a_fallback_reason()
    {
        var rejections = 0;
        var store = new InMemoryOutboxStore();
        store.UpdateFailure = _ => Interlocked.Increment(ref rejections) <= 3 ? new InvalidOperationException("text rejected") : null;
        var id = store.Add();
        var calls = 0;
        using var host = CreateHost(store, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(DeliveryResult.DeadLetter("unstorable reason"));
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.DeadLettered);
        await host.StopAsync(Ct);

        Assert.Equal(1, calls); // not redelivered every lease
        Assert.Contains("could not store the original dead-lettered reason", store.Get(id).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Graceful_shutdown_lets_in_flight_deliveries_finish_and_releases_unstarted_messages()
    {
        var store = new InMemoryOutboxStore();
        var ids = Enumerable.Range(0, 5).Select(i => store.Add($"m{i}")).ToList();
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        using var host = CreateHost(store, async (_, _) =>
        {
            started.TrySetResult();
            await finish.Task;
            return DeliveryResult.Delivered();
        }, d => d.MaxConcurrency = 1);

        await host.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var stopping = host.StopAsync(Ct);
        await Task.Delay(100, Ct);
        finish.SetResult();
        await stopping;

        var statuses = store.CountByStatus();
        Assert.Equal(1, statuses.GetValueOrDefault(OutboxMessageStatus.Delivered));
        Assert.Equal(4, statuses.GetValueOrDefault(OutboxMessageStatus.Pending));
        Assert.All(ids.Select(store.Get).Where(m => m.Status == OutboxMessageStatus.Pending), m => Assert.Equal(0, m.AttemptCount));
    }

    [Fact]
    public async Task Shutdown_timeout_cancels_in_flight_deliveries_and_returns_them_with_the_attempt_counted()
    {
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        var started = new TaskCompletionSource();
        using var host = CreateHost(store, async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return DeliveryResult.Delivered();
        }, shutdownTimeout: TimeSpan.FromMilliseconds(300));

        await host.StartAsync(Ct);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await host.StopAsync(Ct);

        var row = store.Get(id);
        Assert.Equal(OutboxMessageStatus.Pending, row.Status);
        Assert.Equal(1, row.AttemptCount); // the handler ran, so the attempt counts (CR-06)
        Assert.Equal("Delivery was interrupted by host shutdown.", row.LastError);
    }

    [Fact]
    public async Task Handlers_that_always_outlast_shutdown_still_reach_dead_letter()
    {
        // CR-06 / invariant 10: repeated forced shutdowns must not let a message escape MaxAttempts.
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        for (var restart = 0; restart < 3; restart++)
        {
            var started = new TaskCompletionSource();
            using var host = CreateHost(store, async (_, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return DeliveryResult.Delivered();
            }, retry: r => r.MaxAttempts = 2, shutdownTimeout: TimeSpan.FromMilliseconds(300));
            await host.StartAsync(Ct);
            if (restart < 2)
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            }
            else
            {
                await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.DeadLettered);
            }

            await host.StopAsync(Ct);
        }

        Assert.Equal(OutboxMessageStatus.DeadLettered, store.Get(id).Status);
        Assert.Contains("last attempt was interrupted by host shutdown", store.Get(id).LastError, StringComparison.Ordinal); // FU-10
    }

    [Fact]
    public async Task An_acknowledgement_that_committed_but_timed_out_is_not_reported_as_a_lost_lease()
    {
        // CR-10: the first MarkDelivered "fails" after committing; the retry matches no rows; the dispatcher must re-read, not log lease lost.
        var store = new InMemoryOutboxStore();
        var id = store.Add();
        store.CommitThenFailOnce = true;
        var leaseLost = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == DbRelayDiagnostics.MeterName && instrument.Name == "dbrelay.lease.lost")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => Interlocked.Add(ref leaseLost, value));
        listener.Start();

        var workerId = $"worker-{Guid.NewGuid():N}";
        using var host = CreateHost(store, (_, _) => Task.FromResult(DeliveryResult.Delivered()), d => d.WorkerId = workerId);
        await host.StartAsync(Ct);
        await WaitUntilAsync(() => store.Get(id).Status == OutboxMessageStatus.Delivered && store.HasOperation(id, "delivered:rejected"));
        await host.StopAsync(Ct);

        Assert.True(store.HasOperation(id, "delivered:rejected")); // the retry really did match no rows
        Assert.Equal(workerId, store.Get(id).LockedBy);
        Assert.Equal(0, Interlocked.Read(ref leaseLost));
    }

    [Fact]
    public async Task Messages_with_too_little_lease_left_are_released_instead_of_delivered()
    {
        var store = new InMemoryOutboxStore();
        var first = store.Add("first");
        var second = store.Add("second");
        var delivered = new ConcurrentBag<string>();
        var gate = new TaskCompletionSource();
        using var host = CreateHost(store, async (m, _) =>
        {
            delivered.Add(m.GetPayloadAsString());
            if (m.GetPayloadAsString() == "first")
            {
                await gate.Task; // hold the only slot while the lease of "second" runs out
            }

            return DeliveryResult.Delivered();
        }, d =>
        {
            d.MaxConcurrency = 1;
            d.LeaseDuration = TimeSpan.FromSeconds(2);
            d.DeliveryTimeout = TimeSpan.FromSeconds(1);
        });

        await host.StartAsync(Ct);
        await WaitUntilAsync(() => delivered.Contains("first"));
        await Task.Delay(1500, Ct);
        gate.SetResult();
        await WaitUntilAsync(() => store.HasOperation(second, "released"));
        await WaitUntilAsync(() => store.Get(second).Status == OutboxMessageStatus.Delivered);
        await host.StopAsync(Ct);

        Assert.Equal(OutboxMessageStatus.Delivered, store.Get(first).Status);
        Assert.Equal(1, store.Get(second).AttemptCount); // the released claim did not count
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int snapshot;
        while ((snapshot = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, snapshot) != snapshot)
        {
        }
    }

    public sealed class ScopedDependency : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public sealed class ScopeRecordingHandler(ScopedDependency dependency, ConcurrentBag<ScopedDependency> scopes) : IMessageDeliveryHandler
    {
        public Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            scopes.Add(dependency);
            return Task.FromResult(DeliveryResult.Delivered());
        }
    }
}
