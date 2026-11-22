using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 8: the retry schedule as applied by the real database clock, plus exhaustion and manual requeue.</summary>
public sealed class RetryScheduleTests(PostgreSqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds()
    {
        var initial = TimeSpan.FromMilliseconds(200);
        var max = TimeSpan.FromSeconds(1);
        var polling = TimeSpan.FromMilliseconds(50);

        await using var schema = await TestSchema.CreateAsync(fixture);
        await schema.InstallHistoryTriggerAsync();
        await using var producer = schema.CreateProducer();
        var admin = producer.GetRequiredService<IOutboxAdmin>();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);

        var attempts = new ConcurrentQueue<(int Attempt, long Timestamp, string? LastErrorSeen)>();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-retry",
            async (message, ct) =>
            {
                var timestamp = Stopwatch.GetTimestamp();
                var seen = (await admin.GetAsync(message.Id, ct))?.LastError;
                attempts.Enqueue((message.Attempt, timestamp, seen));
                if (message.Attempt <= 2)
                {
                    throw new InvalidOperationException($"transient failure #{message.Attempt}");
                }

                return DeliveryResult.Delivered();
            },
            d => d.PollingInterval = polling,
            r =>
            {
                r.MaxAttempts = 5;
                r.InitialDelay = initial;
                r.MaxDelay = max;
                r.BackoffMultiplier = 2;
            });
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.Delivered,
            TimeSpan.FromSeconds(30),
            "the third attempt to succeed");
        await host.StopAsync(Ct);

        var row = await schema.GetRequiredRowAsync(id);
        var seenAttempts = attempts.ToArray();
        Assert.Equal(new[] { 1, 2, 3 }, seenAttempts.Select(a => a.Attempt));
        Assert.Equal(3, row.AttemptCount);
        Assert.Equal(OutboxMessageStatus.Delivered, row.State);

        // last_error is recorded on each retry and visible to the next attempt; it is not cleared by success.
        Assert.Null(seenAttempts[0].LastErrorSeen);
        Assert.Equal("InvalidOperationException: transient failure #1", seenAttempts[1].LastErrorSeen);
        Assert.Equal("InvalidOperationException: transient failure #2", seenAttempts[2].LastErrorSeen);
        Assert.Equal("InvalidOperationException: transient failure #2", row.LastError);

        // Equal jitter: the delay after failed attempt n is in [d/2, d] with d = min(MaxDelay, Initial * 2^(n-1)).
        // 1) Exact check on the database clock, independent of machine load: a test-only trigger recorded every update with the
        //    statement's now(), so for each ScheduleRetry the applied delay is available_at - now(), and the next claim must not
        //    happen before that available_at.
        var history = await schema.GetHistoryAsync(id);
        var retries = history.Where(h => h.Status == (short)OutboxMessageStatus.Pending).ToList();
        var claims = history.Where(h => h.Status == (short)OutboxMessageStatus.Processing).ToList();
        Assert.Equal(2, retries.Count);
        Assert.Equal(new[] { 1, 2, 3 }, claims.Select(c => c.AttemptCount));
        var applied = new List<TimeSpan>();
        var waited = new List<TimeSpan>();
        for (var n = 1; n <= 2; n++)
        {
            var d = TimeSpan.FromMilliseconds(Math.Min(max.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, n - 1)));
            var retry = retries[n - 1];
            var delay = retry.AvailableAt - retry.At;
            applied.Add(delay);
            Assert.InRange(delay, (d / 2) - TimeSpan.FromMilliseconds(1), d + TimeSpan.FromMilliseconds(1));
            var nextClaim = claims[n];
            Assert.True(nextClaim.At >= retry.AvailableAt, $"Attempt {n + 1} was claimed at {nextClaim.At:O}, before its retry time {retry.AvailableAt:O}.");
            waited.Add(nextClaim.At - retry.At);
        }

        // 2) End-to-end, as the handler sees it (application clock). The lower bound is strict. The upper bound is only a gross
        //    sanity check (wrong units, no cap): it includes polling, round trips and scheduling, which on a loaded 2-core machine
        //    running four test processes was observed at 1.07 s for a 200 ms delay.
        var gaps = new List<TimeSpan>();
        for (var n = 1; n <= 2; n++)
        {
            var d = TimeSpan.FromMilliseconds(Math.Min(max.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, n - 1)));
            var gap = Stopwatch.GetElapsedTime(seenAttempts[n - 1].Timestamp, seenAttempts[n].Timestamp);
            gaps.Add(gap);
            Assert.InRange(gap, (d / 2) - TimeSpan.FromMilliseconds(5), d + TimeSpan.FromSeconds(5));
        }

        Metrics.Report(
            nameof(Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds),
            $"appliedDelay1={applied[0].TotalMilliseconds:0.0}ms (bounds 100-200) appliedDelay2={applied[1].TotalMilliseconds:0.0}ms (bounds 200-400) " +
            $"retryToClaim1={waited[0].TotalMilliseconds:0}ms retryToClaim2={waited[1].TotalMilliseconds:0}ms " +
            $"handlerGap1={gaps[0].TotalMilliseconds:0}ms handlerGap2={gaps[1].TotalMilliseconds:0}ms");
    }

    [Fact]
    public async Task Exhausted_message_is_dead_lettered_and_manual_requeue_redelivers_it_from_attempt_one()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var admin = producer.GetRequiredService<IOutboxAdmin>();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);

        var fail = 1;
        var attempts = new ConcurrentQueue<int>();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-requeue",
            (message, _) =>
            {
                attempts.Enqueue(message.Attempt);
                return Task.FromResult(Volatile.Read(ref fail) == 1
                    ? DeliveryResult.Retry("broker unavailable")
                    : DeliveryResult.Delivered());
            },
            retry: r =>
            {
                r.MaxAttempts = 3;
                r.InitialDelay = TimeSpan.FromMilliseconds(20);
                r.MaxDelay = TimeSpan.FromMilliseconds(50);
            });
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.DeadLettered,
            TimeSpan.FromSeconds(30),
            "the message to be dead-lettered");

        var dead = await schema.GetRequiredRowAsync(id);
        Assert.Equal(3, dead.AttemptCount);
        Assert.Equal("Max attempts (3) reached. Last error: broker unavailable", dead.LastError);
        Assert.NotNull(dead.DeadLetteredAt);

        // Dead letters stay put until an operator acts.
        await Task.Delay(500, Ct);
        Assert.Equal(new[] { 1, 2, 3 }, attempts.ToArray());

        Volatile.Write(ref fail, 0);
        Assert.True(await admin.RequeueAsync(id, Ct));
        Assert.False(await admin.RequeueAsync(id, Ct)); // only dead-lettered rows can be requeued
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.Delivered,
            TimeSpan.FromSeconds(30),
            "the requeued message to be delivered");
        await host.StopAsync(Ct);

        var delivered = await schema.GetRequiredRowAsync(id);
        Assert.Equal(new[] { 1, 2, 3, 1 }, attempts.ToArray());
        Assert.Equal(1, delivered.AttemptCount);
        Assert.Null(delivered.DeadLetteredAt);
    }
}
