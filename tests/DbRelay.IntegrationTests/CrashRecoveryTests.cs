using System.Collections.Concurrent;
using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>
/// Scenarios 2 and 3: a real child worker process is killed mid-delivery. Proves at-least-once redelivery after lease expiry and
/// that a message which kills its worker every time still terminates in the dead-letter state.
/// </summary>
public sealed class CrashRecoveryTests(PostgreSqlFixture fixture) : IDisposable
{
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(2);

    private readonly string _markerDirectory = Path.Combine(AppContext.BaseDirectory, "crash-markers", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_markerDirectory))
            {
                Directory.Delete(_markerDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact]
    public async Task Killed_worker_process_message_is_redelivered_after_lease_expiry()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        Guid id;
        await using (var producer = schema.CreateProducer())
        {
            id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);
        }

        Directory.CreateDirectory(_markerDirectory);
        var marker = Path.Combine(_markerDirectory, "worker.log");
        var appName = "dbrelay-it-crash-" + Guid.NewGuid().ToString("N")[..12];
        var stopwatch = Stopwatch.StartNew();

        int pid;
        using (var worker = ChildWorker.Start(fixture.ConnectionStringFor(appName, 5), schema.Name, marker, "hang", 5, Lease, DeliveryTimeout))
        {
            pid = worker.Pid;
            await Wait.UntilAsync(
                () =>
                {
                    Assert.False(worker.HasExited, $"The worker exited early (code {(worker.HasExited ? worker.ExitCode : 0)}):\n{worker.Output}");
                    return ChildWorker.ReadDeliveries(marker).Count > 0;
                },
                TimeSpan.FromSeconds(60),
                "the child worker to receive the message");
            var receivedAfter = stopwatch.Elapsed;

            // The external system "received" the message; now the worker dies before it can record the outcome.
            worker.KillTree();
            Assert.True(worker.HasExited);
            Assert.False(ChildWorker.IsRunning(pid), $"Process {pid} is still running after being killed.");
            Metrics.Report(nameof(Killed_worker_process_message_is_redelivered_after_lease_expiry), $"child received message after {receivedAfter.TotalMilliseconds:0}ms (includes process start-up)");
        }

        Assert.Equal(new[] { $"DELIVERED {id} 1" }, ChildWorker.ReadDeliveries(marker));

        // The database also sees the worker's sessions disappear.
        await Wait.UntilAsync(async () => await CountSessionsAsync(appName) == 0, TimeSpan.FromSeconds(15), "the killed worker's sessions to end");

        // The claim survives the crash: still Processing, attempt 1, locked by the dead process, lease not yet over.
        var orphaned = await schema.GetRequiredRowAsync(id);
        Assert.Equal(OutboxMessageStatus.Processing, orphaned.State);
        Assert.Equal(1, orphaned.AttemptCount);
        Assert.NotNull(orphaned.LockToken);
        Assert.Contains($":{pid}:", orphaned.LockedBy, StringComparison.Ordinal);
        var leaseExpiry = orphaned.AvailableAt;

        var deliveries = new ConcurrentQueue<(Guid Id, int Attempt, string WorkerId)>();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-recover",
            (message, _) =>
            {
                deliveries.Enqueue((message.Id, message.Attempt, "in-process"));
                return Task.FromResult(DeliveryResult.Delivered());
            },
            d =>
            {
                d.LeaseDuration = Lease;
                d.DeliveryTimeout = DeliveryTimeout;
                d.PollingInterval = TimeSpan.FromMilliseconds(100);
            });
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.Delivered,
            TimeSpan.FromSeconds(30),
            "the orphaned message to be redelivered");
        await host.StopAsync(Ct);

        var delivered = await schema.GetRequiredRowAsync(id);
        Assert.Equal(OutboxMessageStatus.Delivered, delivered.State);
        Assert.Equal(2, delivered.AttemptCount);
        Assert.Null(delivered.LockToken);
        Assert.Equal(new[] { (id, 2, "in-process") }, deliveries.ToArray());

        // Redelivery waited for the lease (database clock): the second claim happened at or after the first lease's expiry.
        Assert.NotNull(delivered.LastAttemptAt);
        Assert.True(delivered.LastAttemptAt >= leaseExpiry, $"Reclaimed at {delivered.LastAttemptAt:O}, before lease expiry {leaseExpiry:O}.");
        Metrics.Report(
            nameof(Killed_worker_process_message_is_redelivered_after_lease_expiry),
            $"redelivered {(delivered.LastAttemptAt!.Value - leaseExpiry).TotalMilliseconds:0}ms after lease expiry; total {stopwatch.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public async Task Message_that_kills_its_worker_every_time_ends_dead_lettered_after_MaxAttempts()
    {
        const int MaxAttempts = 2;
        await using var schema = await TestSchema.CreateAsync(fixture);
        Guid id;
        await using (var producer = schema.CreateProducer())
        {
            id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, "poison", Ct);
        }

        Directory.CreateDirectory(_markerDirectory);
        var marker = Path.Combine(_markerDirectory, "crashloop.log");
        var stopwatch = Stopwatch.StartNew();
        var spawns = 0;
        var crashes = 0;
        var deadLettered = false;
        var exitCodes = new List<int>();

        while (!deadLettered && spawns < 6)
        {
            spawns++;
            var appName = $"dbrelay-it-loop-{spawns}-" + Guid.NewGuid().ToString("N")[..8];
            using var worker = ChildWorker.Start(fixture.ConnectionStringFor(appName, 5), schema.Name, marker, "kill", MaxAttempts, Lease, DeliveryTimeout);
            var spawnTimer = Stopwatch.StartNew();
            while (true)
            {
                if (worker.HasExited)
                {
                    crashes++;
                    exitCodes.Add(worker.ExitCode);
                    Assert.False(ChildWorker.IsRunning(worker.Pid));
                    break;
                }

                if ((await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.DeadLettered)
                {
                    // The final claim must not have run the handler, so this worker is still alive.
                    deadLettered = true;
                    await Task.Delay(300, Ct);
                    Assert.False(worker.HasExited, $"The worker that dead-lettered the message crashed:\n{worker.Output}");
                    worker.KillTree();
                    break;
                }

                Assert.True(spawnTimer.Elapsed < TimeSpan.FromSeconds(60), $"Spawn {spawns} neither crashed nor dead-lettered within 60s:\n{worker.Output}");
                await Task.Delay(100, Ct);
            }
        }

        var row = await schema.GetRequiredRowAsync(id);
        Metrics.Report(
            nameof(Message_that_kills_its_worker_every_time_ends_dead_lettered_after_MaxAttempts),
            $"spawns={spawns} crashes={crashes} exitCodes=[{string.Join(",", exitCodes)}] attempts={row.AttemptCount} total={stopwatch.Elapsed.TotalSeconds:0.0}s");

        Assert.True(deadLettered, "The crash loop never terminated.");
        Assert.Equal(OutboxMessageStatus.DeadLettered, row.State);
        Assert.Equal(
            $"Exceeded the maximum of {MaxAttempts} attempts without a recorded outcome (possible worker crash loop).",
            row.LastError);
        Assert.Equal(MaxAttempts + 1, row.AttemptCount); // the final, handler-less claim is counted too
        Assert.NotNull(row.DeadLetteredAt);
        Assert.Null(row.LockToken);

        // The handler ran exactly MaxAttempts times (attempts 1..MaxAttempts); the last claim did not invoke it.
        Assert.Equal(
            Enumerable.Range(1, MaxAttempts).Select(a => $"DELIVERED {id} {a}"),
            ChildWorker.ReadDeliveries(marker));
        Assert.Equal(MaxAttempts, crashes);
        Assert.Equal(MaxAttempts + 1, spawns);
    }

    private async Task<long> CountSessionsAsync(string applicationName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM pg_stat_activity WHERE application_name = @applicationName",
            new { applicationName });
    }
}
