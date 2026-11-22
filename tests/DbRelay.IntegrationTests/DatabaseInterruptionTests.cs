using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 5: the dispatcher's database connections are killed repeatedly while it delivers a steady stream.</summary>
public sealed class DatabaseInterruptionTests(PostgreSqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Dispatcher_survives_terminated_connections_and_delivers_everything()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        var appName = "dbrelay-it-" + Guid.NewGuid().ToString("N");
        var logs = new CapturingLoggerProvider();
        var deliveries = new ConcurrentQueue<Guid>();
        using var host = RelayHost.Build(
            schema,
            appName,
            async (message, ct) =>
            {
                deliveries.Enqueue(message.Id);
                await Task.Delay(2, ct);
                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.LeaseDuration = TimeSpan.FromSeconds(4);
                d.DeliveryTimeout = TimeSpan.FromSeconds(1);
                d.PollingInterval = TimeSpan.FromMilliseconds(50);
                d.MaxErrorBackoff = TimeSpan.FromSeconds(2);
                d.BatchSize = 20;
                d.MaxConcurrency = 4;
            },
            logs: logs,
            services: s => s.AddHealthChecks().AddDbRelay());
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        var health = host.Services.GetRequiredService<HealthCheckService>();
        await host.StartAsync(Ct);

        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();
        var enqueued = new ConcurrentBag<Guid>();
        using var stopProducing = new CancellationTokenSource();
        var producing = Task.Run(
            async () =>
            {
                var sequence = 0;
                while (!stopProducing.IsCancellationRequested)
                {
                    enqueued.Add(await schema.EnqueueOrderAsync(outbox, sequence++, "stream", Ct));
                    await Task.Delay(10, Ct);
                }
            },
            Ct);

        var statuses = new ConcurrentQueue<(TimeSpan At, HealthStatus Status)>();
        var clock = Stopwatch.StartNew();
        using var stopSampling = new CancellationTokenSource();
        var sampling = Task.Run(
            async () =>
            {
                while (!stopSampling.IsCancellationRequested)
                {
                    var report = await health.CheckHealthAsync(Ct);
                    statuses.Enqueue((clock.Elapsed, report.Status));
                    await Task.Delay(50, Ct);
                }
            },
            Ct);

        // Warm-up: deliveries are flowing before the first interruption.
        await Wait.UntilAsync(() => deliveries.Count >= 20, TimeSpan.FromSeconds(30), "deliveries to start");

        var terminated = 0L;
        var interruptionWindows = new List<(TimeSpan From, TimeSpan To)>();
        for (var round = 0; round < 3; round++)
        {
            var from = clock.Elapsed;
            var burst = Stopwatch.StartNew();
            while (burst.Elapsed < TimeSpan.FromSeconds(2))
            {
                terminated += await TerminateAsync(appName);
                await Task.Delay(25, Ct);
            }

            interruptionWindows.Add((from, clock.Elapsed));
            Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "The host began stopping during the interruption.");
            await Task.Delay(1500, Ct);
        }

        var deliveredBeforeFinalCalm = deliveries.Count;
        await Task.Delay(1000, Ct);
        await stopProducing.CancelAsync();
        await producing;

        var ids = enqueued.ToHashSet();
        await Wait.UntilAsync(
            async () => (await schema.CountByStatusAsync()).GetValueOrDefault(OutboxMessageStatus.Delivered) == ids.Count,
            TimeSpan.FromSeconds(60),
            $"all {ids.Count} messages to be Delivered after the interruptions",
            pollMs: 100);

        await Wait.UntilAsync(
            () => statuses.LastOrDefault().Status == HealthStatus.Healthy,
            TimeSpan.FromSeconds(30),
            "the health check to report Healthy again");
        await stopSampling.CancelAsync();
        try
        {
            await sampling;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested, "The host stopped by itself.");
        await host.StopAsync(Ct);

        var samples = statuses.ToArray();
        var duringOutage = samples.Where(s => interruptionWindows.Any(w => s.At >= w.From && s.At <= w.To + TimeSpan.FromSeconds(1))).ToArray();
        var perMessage = deliveries.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        Metrics.Report(
            nameof(Dispatcher_survives_terminated_connections_and_delivers_everything),
            $"messages={ids.Count} deliveries={deliveries.Count} duplicates={deliveries.Count - perMessage.Count} terminatedBackends={terminated} " +
            $"claimFailures={logs.Count(1002)} persistRetries={logs.Count(1008)} persistFailed={logs.Count(1009)} leaseLost={logs.Count(1007)} " +
            $"ackBatchRetries={logs.Count(1015)} ackBatchFailed={logs.Count(1016)} " +
            $"healthSamples={samples.Length} healthy={samples.Count(s => s.Status == HealthStatus.Healthy)} " +
            $"degraded={samples.Count(s => s.Status == HealthStatus.Degraded)} unhealthy={samples.Count(s => s.Status == HealthStatus.Unhealthy)}");

        Assert.True(terminated > 0, "No dispatcher connection was terminated; the test proved nothing.");
        // Delivered acknowledgements go through the batcher (ADR 0003), which logs its own retries (1015) and failures (1016).
        // Without them, a run whose interruptions all hit acknowledgements rather than claims looked like "no failure observed".
        var observedFailures = logs.Count(1002) + logs.Count(1008) + logs.Count(1009) + logs.Count(1012) + logs.Count(1015) + logs.Count(1016);
        Assert.True(observedFailures > 0, "The dispatcher never observed a database failure.");
        Assert.Contains(duringOutage, s => s.Status != HealthStatus.Healthy);
        Assert.Equal(HealthStatus.Healthy, samples[^1].Status);

        // Every committed message was delivered at least once; nothing unknown was delivered; nothing is stuck.
        Assert.DoesNotContain(ids, id => !perMessage.ContainsKey(id));
        Assert.DoesNotContain(perMessage.Keys, id => !ids.Contains(id));
        var rows = await schema.GetRowsAsync();
        Assert.Equal(ids.Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(OutboxMessageStatus.Delivered, r.State));
        Assert.True(deliveries.Count > deliveredBeforeFinalCalm, "No deliveries happened after the last interruption.");
    }

    private async Task<long> TerminateAsync(string applicationName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FILTER (WHERE pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = @applicationName AND pid <> pg_backend_pid()",
            new { applicationName });
    }
}
