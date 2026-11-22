using System.Diagnostics;
using System.Globalization;
using System.Text;
using DbRelay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbRelay.Benchmarks;

/// <summary>
/// End-to-end delivery throughput: pre-load N messages, start W dispatcher hosts with a no-op handler, and time until every
/// message is Delivered. Measures DbRelay's claim/ack pipeline and database cost, not a broker.
/// </summary>
public static class ThroughputHarness
{
    public static async Task<string> RunAsync(Provider provider, int messages, int[] batchSizes, int[] concurrencies, int[] workers)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"| Provider | Messages | Batch | Concurrency | Workers | Ack batching | Seconds | Msg/s | Claims | Duplicates |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        foreach (var workerCount in workers)
        {
            foreach (var batch in batchSizes)
            {
                foreach (var concurrency in concurrencies)
                {
                    // A/B in the same session, back to back: batching off, then on (ADR 0003). The machine's run-to-run noise
                    // otherwise swamps the difference.
                    foreach (var batching in new[] { false, true })
                    {
                        var line = await RunOnceAsync(provider, messages, batch, concurrency, workerCount, batching);
                        Console.WriteLine(line);
                        report.AppendLine(line);
                    }
                }
            }
        }

        return report.ToString();
    }

    public static async Task<string> RepeatAsync(Provider provider, int messages, int batch, int concurrency, int workers, int reps)
    {
        var off = new List<double>();
        var on = new List<double>();
        for (var r = 0; r < reps; r++)
        {
            foreach (var batching in new[] { false, true })
            {
                var line = await RunOnceAsync(provider, messages, batch, concurrency, workers, batching);
                var cells = line.Split('|', StringSplitOptions.TrimEntries);
                var rate = double.Parse(cells[8], CultureInfo.InvariantCulture);
                (batching ? on : off).Add(rate);
                Console.WriteLine(line);
            }
        }

        static double Median(List<double> v)
        {
            var s = v.OrderBy(x => x).ToList();
            return s.Count % 2 == 1 ? s[s.Count / 2] : (s[(s.Count / 2) - 1] + s[s.Count / 2]) / 2;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {provider} | b{batch} c{concurrency} w{workers} | off median {Median(off):F0} (min {off.Min():F0}, max {off.Max():F0}) | on median {Median(on):F0} (min {on.Min():F0}, max {on.Max():F0}) | {(Median(on) - Median(off)) / Median(off):P0} |{Environment.NewLine}");
    }

    private static async Task<string> RunOnceAsync(Provider provider, int messages, int batch, int concurrency, int workerCount, bool batching)
    {
        var schema = Databases.NewSchema("tput");
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<Guid, int>();
        await using (var setup = await Databases.CreateSchemaAsync(provider, schema))
        {
            var outbox = setup.GetRequiredService<IOutbox>();
            for (var offset = 0; offset < messages; offset += 1000)
            {
                await using var connection = await Databases.OpenAsync(provider);
                await using var transaction = await connection.BeginTransactionAsync();
                var count = Math.Min(1000, messages - offset);
                await outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, count).Select(_ => new BenchMessage(Guid.NewGuid(), "c", 1m)));
                await transaction.CommitAsync();
            }
        }

        var hosts = Enumerable.Range(0, workerCount).Select(_ =>
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            builder.Services.AddDbRelay(relay =>
            {
                relay.UseProvider(provider, schema);
                relay.UseDeliveryHandler((m, _) =>
                {
                    seen.AddOrUpdate(m.Id, 1, (_, n) => n + 1);
                    return Task.FromResult(DeliveryResult.Delivered());
                });
                relay.ConfigureDispatcher(d =>
                {
                    d.BatchSize = batch;
                    d.MaxConcurrency = concurrency;
                    d.PollingInterval = TimeSpan.FromMilliseconds(100);
                    d.BatchAcknowledgements = batching;
                });
                relay.ConfigureMaintenance(m =>
                {
                    m.CleanupEnabled = false;
                    m.StatisticsInterval = null;
                });
            });
            return builder.Build();
        }).ToList();

        var claimsBefore = ClaimCounter.Value;
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(hosts.Select(h => h.StartAsync()));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10)))
        {
            while (seen.Count < messages)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        stopwatch.Stop();
        await Task.WhenAll(hosts.Select(h => h.StopAsync()));
        foreach (var host in hosts)
        {
            host.Dispose();
        }

        await Databases.DropSchemaAsync(provider, schema);
        var duplicates = seen.Values.Sum(v => v - 1);
        var seconds = stopwatch.Elapsed.TotalSeconds;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"| {provider} | {messages} | {batch} | {concurrency} | {workerCount} | {(batching ? "on" : "off")} | {seconds:F2} | {messages / seconds:F0} | {ClaimCounter.Value - claimsBefore} | {duplicates} |");
    }
}

/// <summary>Counts claim round trips via the dbrelay.claim.batch_size histogram.</summary>
internal static class ClaimCounter
{
    private static long s_value;
    private static readonly System.Diagnostics.Metrics.MeterListener Listener = Create();

    public static long Value => Interlocked.Read(ref s_value);

    private static System.Diagnostics.Metrics.MeterListener Create()
    {
        var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "DbRelay" && instrument.Name == "dbrelay.claim.batch_size")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<int>((_, _, _, _) => Interlocked.Increment(ref s_value));
        listener.Start();
        return listener;
    }

    public static void EnsureStarted() => _ = Listener;
}
