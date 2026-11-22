using Dapper;
using DbRelay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbRelay.Soak;

/// <summary>
/// A soak worker process: a real DbRelay dispatcher host with a deliberately unreliable handler. A successful delivery is
/// written to the <c>sink</c> table (the "external system") before the handler returns Delivered, so a kill between the two
/// produces a visible duplicate, which at-least-once allows.
/// </summary>
public static class Worker
{
    public static async Task<int> RunAsync(Provider provider, string schema, string workerName, string stopFile)
    {
        var db = new SoakDatabase(provider, schema);
        var appName = $"soak-worker-{workerName}";
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, Args = [] });
        builder.Logging.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(10));
        builder.Services.AddDbRelay(relay =>
        {
            db.UseStorage(relay, appName);
            relay.UseDeliveryHandler(async (message, ct) =>
            {
                // Real latency, honouring cancellation.
                await Task.Delay(Random.Shared.Next(1, 30), ct);

                var roll = Random.Shared.NextDouble();
                var poison = message.GetPayloadAsString().Contains("\"poison\":true", StringComparison.Ordinal);
                if (poison)
                {
                    return DeliveryResult.DeadLetter("soak: poison message rejected");
                }

                if (roll < 0.05)
                {
                    return DeliveryResult.Retry("soak: transient failure");
                }

                if (roll < 0.07)
                {
                    throw new InvalidOperationException("soak: handler exception");
                }

                await using (var connection = await db.OpenAsync(appName, ct))
                {
                    await connection.ExecuteAsync(db.InsertSinkSql, new { Id = message.Id, message.Attempt, Worker = workerName });
                }

                return DeliveryResult.Delivered();
            });
            relay.ConfigureDispatcher(d =>
            {
                d.BatchSize = 50;
                d.MaxConcurrency = 8;
                d.PollingInterval = TimeSpan.FromMilliseconds(200);
                d.LeaseDuration = TimeSpan.FromSeconds(10);
                d.DeliveryTimeout = TimeSpan.FromSeconds(3);
                d.MaxErrorBackoff = TimeSpan.FromSeconds(5);
                d.WorkerId = $"{appName}:{Environment.ProcessId}";
            });
            relay.ConfigureRetry(r =>
            {
                r.MaxAttempts = 8;
                r.InitialDelay = TimeSpan.FromMilliseconds(100);
                r.MaxDelay = TimeSpan.FromSeconds(2);
            });
            relay.ConfigureMaintenance(m =>
            {
                m.CleanupEnabled = false; // keep every row so the coordinator can verify the whole run
                m.StatisticsInterval = TimeSpan.FromSeconds(15);
            });
        });

        using var host = builder.Build();
        await host.StartAsync();

        // Graceful shutdown on request (exercises the drain/release path); hard kills come from the coordinator.
        while (!File.Exists(stopFile))
        {
            await Task.Delay(250);
        }

        await host.StopAsync();
        return 0;
    }
}
