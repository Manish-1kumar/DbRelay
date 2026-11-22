using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Dapper;
using DbRelay;
using Microsoft.Extensions.DependencyInjection;

namespace DbRelay.Soak;

/// <summary>
/// Runs producers and chaos against a set of worker processes for a fixed duration, then drains and verifies invariants.
/// </summary>
public static class Coordinator
{
    private const int MaxAttempts = 8; // must match Worker

    public static async Task<int> RunAsync(Provider provider, TimeSpan duration, int workerCount, string reportPath, double messagesPerSecond, bool keep)
    {
        var schema = "soak_" + Guid.NewGuid().ToString("N")[..12];
        var db = new SoakDatabase(provider, schema);
        var workDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "dbrelay-" + schema)).FullName;
        var stats = new RunStats();
        var started = DateTimeOffset.UtcNow;
        Log($"Soak {schema}: provider {provider}, {duration}, {workerCount} workers, ~{messagesPerSecond} msg/s, work dir {workDir}");

        await using var producerServices = CreateProducerServices(db);
        await producerServices.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
        await using (var connection = await db.OpenAsync("soak-setup"))
        {
            await connection.ExecuteAsync(db.CreateTablesSql);
        }

        var workers = new List<WorkerProcess>();
        try
        {
            return await RunWithWorkersAsync(db, provider, schema, workDir, stats, started, duration, workerCount, reportPath, messagesPerSecond, keep, producerServices, workers);
        }
        finally
        {
            // Whatever happens to the coordinator, never leave worker processes running against the database.
            foreach (var worker in workers.Where(w => !w.HasExited))
            {
                worker.Kill();
            }
        }
    }

    private static async Task<int> RunWithWorkersAsync(
        SoakDatabase db, Provider provider, string schema, string workDir, RunStats stats, DateTimeOffset started, TimeSpan duration,
        int workerCount, string reportPath, double messagesPerSecond, bool keep, ServiceProvider producerServices, List<WorkerProcess> workers)
    {
        for (var i = 0; i < workerCount; i++)
        {
            workers.Add(WorkerProcess.Start(provider, schema, $"w{i}", workDir, immune: i == 0));
        }

        using var stopProducing = new CancellationTokenSource(duration);
        var rolledBack = new ConcurrentBag<Guid>();
        var outbox = producerServices.GetRequiredService<IOutbox>();
        var producers = Enumerable.Range(0, 4).Select(p => ProduceAsync(db, outbox, messagesPerSecond / 4, rolledBack, stats, stopProducing.Token)).ToList();
        var chaos = ChaosAsync(db, workers, provider, schema, workDir, stats, stopProducing.Token);
        var samples = new List<Sample>();
        var sampler = SampleAsync(db, workers, samples, stopProducing.Token);

        await Task.WhenAll(producers);
        await chaos;
        await sampler;
        Log($"Producing stopped: {stats.CommittedMessages} committed, {stats.RolledBackMessages} rolled back. Draining...");

        // Drain: make sure every worker slot is alive, then wait until nothing is pending or processing.
        for (var i = 0; i < workers.Count; i++)
        {
            if (workers[i].HasExited)
            {
                workers[i] = WorkerProcess.Start(provider, schema, $"w{i}-drain", workDir, immune: false);
            }
        }

        var admin = producerServices.GetRequiredService<IOutboxAdmin>();
        var drainStart = Stopwatch.StartNew();
        OutboxStatistics last;
        while (true)
        {
            last = await admin.GetStatisticsAsync();
            if (last.Pending == 0 && last.Processing == 0)
            {
                break;
            }

            if (drainStart.Elapsed > TimeSpan.FromMinutes(10))
            {
                Log($"Drain timed out: pending {last.Pending}, processing {last.Processing}");
                break;
            }

            await Task.Delay(1000);
        }

        stats.DrainSeconds = drainStart.Elapsed.TotalSeconds;
        foreach (var worker in workers)
        {
            await worker.StopGracefullyAsync(TimeSpan.FromSeconds(30));
        }

        var report = await VerifyAsync(db, rolledBack.ToHashSet(), stats, samples, workers, workDir, started, duration, workerCount, messagesPerSecond);
        await File.WriteAllTextAsync(reportPath, report.Text);
        Log($"Report written to {reportPath}. Result: {(report.Passed ? "PASS" : "FAIL")}");
        if (!keep)
        {
            await db.DropAsync();
        }

        return report.Passed ? 0 : 1;
    }

    private static ServiceProvider CreateProducerServices(SoakDatabase db)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbRelay(relay =>
        {
            db.UseStorage(relay, "soak-producer");
            relay.DisableDispatcher();
        });
        return services.BuildServiceProvider();
    }

    private static async Task ProduceAsync(
        SoakDatabase db, IOutbox outbox, double rate, ConcurrentBag<Guid> rolledBack, RunStats stats, CancellationToken stop)
    {
        var sequence = 0;
        var filler = new string('x', 200);
        var clock = Stopwatch.StartNew();
        var nextDue = TimeSpan.Zero;
        while (!stop.IsCancellationRequested)
        {
            // Every 5 minutes, a 20-second burst at 10x the rate to build and then drain a backlog.
            var burst = clock.Elapsed.TotalSeconds % 300 < 20;
            var effectiveRate = burst ? rate * 10 : rate;
            var count = Random.Shared.Next(1, 6);
            var commit = Random.Shared.NextDouble() >= 0.2;
            try
            {
                await using var connection = await db.OpenAsync("soak-producer", stop);
                await using var transaction = await connection.BeginTransactionAsync(stop);
                var messages = Enumerable.Range(0, count)
                    .Select(_ => new SoakMessage(Interlocked.Increment(ref sequence), Random.Shared.NextDouble() < 0.02, filler))
                    .ToList();
                IReadOnlyList<Guid> ids = count == 1
                    ? [await outbox.EnqueueAsync(transaction, messages[0], stop)]
                    : await outbox.EnqueueManyAsync(transaction, messages, stop);
                for (var i = 0; i < ids.Count; i++)
                {
                    await connection.ExecuteAsync(db.InsertOrderSql, new { Id = ids[i], messages[i].Poison }, transaction);
                }

                if (commit)
                {
                    await transaction.CommitAsync(stop);
                    Interlocked.Increment(ref stats.CommittedTransactions);
                    Interlocked.Add(ref stats.CommittedMessages, count);
                    Interlocked.Add(ref stats.PoisonMessages, messages.Count(m => m.Poison));
                }
                else
                {
                    await transaction.RollbackAsync(stop);
                    foreach (var id in ids)
                    {
                        rolledBack.Add(id);
                    }

                    Interlocked.Increment(ref stats.RolledBackTransactions);
                    Interlocked.Add(ref stats.RolledBackMessages, count);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The transaction rolled back (disposed without commit). Its IDs were never committed, so they need no tracking:
                // verification checks every outbox row against the committed orders table.
                Interlocked.Increment(ref stats.ProducerErrors);
                stats.LastProducerError = exception.GetType().Name + ": " + exception.Message;
            }

            // Pace each transaction at the current rate. Never accumulate "debt": falling behind (for example during a database
            // outage) must not cause a catch-up flood afterwards, and a burst must not borrow time from the steady phase.
            var now = clock.Elapsed;
            nextDue = (nextDue > now ? nextDue : now) + TimeSpan.FromSeconds(count / effectiveRate);
            if (nextDue > now)
            {
                try
                {
                    await Task.Delay(nextDue - now, stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static async Task ChaosAsync(
        SoakDatabase db, List<WorkerProcess> workers, Provider provider, string schema, string workDir, RunStats stats, CancellationToken stop)
    {
        var nextWorkerChaos = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(20, 60));
        var nextConnectionChaos = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(45, 90));
        var generation = 0;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextWorkerChaos)
            {
                var candidates = workers.Select((w, i) => (w, i)).Where(x => !x.w.Immune).ToList();
                if (candidates.Count > 0)
                {
                    var (victim, index) = candidates[Random.Shared.Next(candidates.Count)];
                    if (Random.Shared.NextDouble() < 0.7)
                    {
                        victim.Kill();
                        Interlocked.Increment(ref stats.WorkerKills);
                        Log($"chaos: killed {victim.Name}");
                    }
                    else
                    {
                        await victim.StopGracefullyAsync(TimeSpan.FromSeconds(30));
                        Interlocked.Increment(ref stats.GracefulStops);
                        Log($"chaos: gracefully stopped {victim.Name}");
                    }

                    await Task.Delay(Random.Shared.Next(1000, 5000), CancellationToken.None);
                    workers[index] = WorkerProcess.Start(provider, schema, $"w{index}-g{++generation}", workDir, immune: false);
                }

                nextWorkerChaos = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(20, 60));
            }

            if (now >= nextConnectionChaos)
            {
                try
                {
                    var killed = await db.KillWorkerSessionsAsync();
                    Interlocked.Increment(ref stats.ConnectionKillRounds);
                    Interlocked.Add(ref stats.SessionsKilled, killed);
                    Log($"chaos: terminated {killed} worker database sessions");
                }
                catch (Exception exception)
                {
                    Log($"chaos: connection kill failed: {exception.Message}");
                }

                nextConnectionChaos = DateTimeOffset.UtcNow.AddSeconds(Random.Shared.Next(45, 90));
            }
        }
    }

    private static async Task SampleAsync(SoakDatabase db, List<WorkerProcess> workers, List<Sample> samples, CancellationToken stop)
    {
        var services = CreateProducerServices(db);
        await using (services)
        {
            var admin = services.GetRequiredService<IOutboxAdmin>();
            var clock = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    var s = await admin.GetStatisticsAsync();
                    var immune = workers[0];
                    var (rss, threads, handles) = immune.Measure();
                    samples.Add(new Sample(clock.Elapsed, s.Pending, s.Processing, s.Delivered, s.DeadLettered, s.OldestDueAge, rss, threads, handles));
                    Log($"sample {clock.Elapsed:hh\\:mm\\:ss}: pending {s.Pending}, processing {s.Processing}, delivered {s.Delivered}, dead {s.DeadLettered}, " +
                        $"oldest due {(s.OldestDueAge is { } age ? age.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture) + "s" : "-")}, w0 RSS {rss / 1048576.0:F1} MB, threads {threads}, handles {handles}");
                }
                catch (Exception exception)
                {
                    Log($"sample failed: {exception.Message}");
                }
            }
        }
    }

    private static async Task<(string Text, bool Passed)> VerifyAsync(
        SoakDatabase db, HashSet<Guid> rolledBack, RunStats stats, List<Sample> samples, List<WorkerProcess> workers, string workDir,
        DateTimeOffset started, TimeSpan duration, int workerCount, double rate)
    {
        await using var c = await db.OpenAsync("soak-verify");
        var outbox = db.T("outbox_messages");
        var orders = db.T("orders");
        var sink = db.T("sink");
        var no = false;
        var yes = true;

        async Task<long> Count(string sql, object? args = null) => await c.ExecuteScalarAsync<long>(sql, args, commandTimeout: 600);

        var committed = await Count($"SELECT COUNT(*) FROM {orders}");
        var outboxRows = await Count($"SELECT COUNT(*) FROM {outbox}");
        var normalDelivered = await Count($"SELECT COUNT(*) FROM {orders} o JOIN {outbox} m ON m.id = o.message_id WHERE o.poison = @no AND m.status = 2", new { no });
        var normalTotal = await Count($"SELECT COUNT(*) FROM {orders} WHERE poison = @no", new { no });
        var normalDeadLettered = await Count($"SELECT COUNT(*) FROM {orders} o JOIN {outbox} m ON m.id = o.message_id WHERE o.poison = @no AND m.status = 3", new { no });
        var poisonTotal = await Count($"SELECT COUNT(*) FROM {orders} WHERE poison = @yes", new { yes });
        var poisonDead = await Count($"SELECT COUNT(*) FROM {orders} o JOIN {outbox} m ON m.id = o.message_id WHERE o.poison = @yes AND m.status = 3", new { yes });
        var missingOutbox = await Count($"SELECT COUNT(*) FROM {orders} o WHERE NOT EXISTS (SELECT 1 FROM {outbox} m WHERE m.id = o.message_id)");
        var orphanOutbox = await Count($"SELECT COUNT(*) FROM {outbox} m WHERE NOT EXISTS (SELECT 1 FROM {orders} o WHERE o.message_id = m.id)");
        var stuck = await Count($"SELECT COUNT(*) FROM {outbox} WHERE status IN (0, 1)");
        var maxAttempt = await Count($"SELECT COALESCE(MAX(attempt_count), 0) FROM {outbox}");
        var overAttempts = await Count($"SELECT COUNT(*) FROM {outbox} WHERE attempt_count > {MaxAttempts + 1}");
        var deliveredWithoutSink = await Count($"SELECT COUNT(*) FROM {outbox} m WHERE m.status = 2 AND NOT EXISTS (SELECT 1 FROM {sink} s WHERE s.message_id = m.id)");
        var sinkRows = await Count($"SELECT COUNT(*) FROM {sink}");
        var sinkDistinct = await Count($"SELECT COUNT(DISTINCT message_id) FROM {sink}");
        var sunkButNotDelivered = await Count($"SELECT COUNT(DISTINCT s.message_id) FROM {sink} s JOIN {outbox} m ON m.id = s.message_id WHERE m.status <> 2");
        var poisonSunk = await Count($"SELECT COUNT(DISTINCT s.message_id) FROM {sink} s JOIN {orders} o ON o.message_id = s.message_id WHERE o.poison = @yes", new { yes });

        var outboxIds = (await c.QueryAsync<Guid>($"SELECT id FROM {outbox}", commandTimeout: 600)).ToHashSet();
        var sinkIds = (await c.QueryAsync<Guid>($"SELECT DISTINCT message_id FROM {sink}", commandTimeout: 600)).ToHashSet();
        var rolledBackInOutbox = rolledBack.Count(outboxIds.Contains);
        var rolledBackDelivered = rolledBack.Count(sinkIds.Contains);

        var latencySql = db.Provider == Provider.PostgreSql
            ? $"SELECT EXTRACT(EPOCH FROM delivered_at - created_at) * 1000.0 FROM {outbox} WHERE status = 2"
            : $"SELECT CAST(DATEDIFF_BIG(millisecond, created_at, delivered_at) AS float) FROM {outbox} WHERE status = 2";
        var latencies = (await c.QueryAsync<double>(latencySql, commandTimeout: 600)).OrderBy(x => x).ToArray();
        double P(double q) => latencies.Length == 0 ? 0 : latencies[Math.Min(latencies.Length - 1, (int)(q * latencies.Length))];

        var workerErrors = Directory.GetFiles(workDir, "*.log").Sum(f => ReadLinesShared(f).Count(l => l.Contains("fail:", StringComparison.Ordinal)));
        var unexpected = Directory.GetFiles(workDir, "*.log").SelectMany(ReadLinesShared)
            .Where(l => l.Contains("Unhandled exception", StringComparison.Ordinal) || l.Contains("[1011]", StringComparison.Ordinal))
            .Take(5).ToList();

        var checks = new List<(string Invariant, bool Passed, string Detail)>
        {
            ("Atomicity: every committed order has its outbox row", missingOutbox == 0, $"{missingOutbox} missing of {committed}"),
            ("Atomicity: no outbox row without a committed order", orphanOutbox == 0, $"{orphanOutbox} orphans of {outboxRows}"),
            ("Atomicity: nothing from a rolled-back transaction was stored", rolledBackInOutbox == 0, $"{rolledBackInOutbox} of {rolledBack.Count} rolled-back IDs found"),
            ("Atomicity: nothing from a rolled-back transaction was delivered", rolledBackDelivered == 0, $"{rolledBackDelivered} delivered"),
            ("Completeness: every committed non-poison message was delivered", normalDelivered == normalTotal, $"{normalDelivered} of {normalTotal} (dead-lettered: {normalDeadLettered})"),
            ("Poison messages were dead-lettered, never delivered", poisonDead == poisonTotal && poisonSunk == 0, $"{poisonDead} of {poisonTotal} dead-lettered; {poisonSunk} reached the sink"),
            ("Nothing left Pending or Processing after the drain", stuck == 0, $"{stuck} rows"),
            ($"Attempts bounded (<= MaxAttempts + 1 = {MaxAttempts + 1})", overAttempts == 0, $"max attempt_count {maxAttempt}"),
            ("Every Delivered row reached the external system (sink)", deliveredWithoutSink == 0, $"{deliveredWithoutSink} rows"),
        };
        var passed = checks.All(x => x.Passed);
        var duplicates = sinkRows - sinkDistinct;

        var sb = new StringBuilder();
        void L(string line = "") => sb.AppendLine(line);
        L($"# DbRelay soak test: {db.Provider}");
        L();
        L($"**Result: {(passed ? "PASS" : "FAIL")}** ({checks.Count(x => x.Passed)}/{checks.Count} invariants)");
        L();
        L("| Setting | Value |");
        L("|---|---|");
        L($"| Started (UTC) | {started:yyyy-MM-dd HH:mm} |");
        L($"| Produce duration | {duration} |");
        L($"| Worker processes | {workerCount} (w0 is never killed and is the memory-trend probe) |");
        L($"| Target rate | ~{rate} msg/s, with a 10x burst for 20 s every 5 min |");
        L("| Dispatcher | BatchSize 50, MaxConcurrency 8, lease 10 s, timeout 3 s, MaxAttempts 8, batched acknowledgements on |");
        L("| Handler | 1-30 ms latency; 5% Retry, 2% throws; 2% of messages are poison and dead-lettered; success writes to a sink table before acknowledging |");
        L("| Producer | 1-5 messages per transaction; 20% of transactions rolled back |");
        L();
        L("## Load and chaos");
        L();
        L("| Measure | Value |");
        L("|---|---|");
        L($"| Committed transactions / messages | {stats.CommittedTransactions:N0} / {stats.CommittedMessages:N0} |");
        L($"| Rolled-back transactions / messages | {stats.RolledBackTransactions:N0} / {stats.RolledBackMessages:N0} |");
        L($"| Poison messages committed | {stats.PoisonMessages:N0} |");
        L($"| Producer errors (transaction failed and rolled back) | {stats.ProducerErrors:N0}{(stats.LastProducerError is null ? "" : $" (last: {stats.LastProducerError})")} |");
        L($"| Worker processes hard-killed | {stats.WorkerKills} |");
        L($"| Workers stopped gracefully | {stats.GracefulStops} |");
        L($"| Connection-kill rounds / sessions terminated | {stats.ConnectionKillRounds} / {stats.SessionsKilled} |");
        L($"| Drain time after producing stopped | {stats.DrainSeconds:F1} s |");
        L();
        L("## Invariants");
        L();
        L("| Invariant | Result | Detail |");
        L("|---|---|---|");
        foreach (var (invariant, ok, detail) in checks)
        {
            L($"| {invariant} | {(ok ? "PASS" : "**FAIL**")} | {detail} |");
        }

        L();
        L("## At-least-once observations (allowed, not failures)");
        L();
        L($"- Duplicate deliveries to the sink: **{duplicates:N0}** ({(sinkDistinct == 0 ? 0 : 100.0 * duplicates / sinkDistinct):F3}% of delivered messages). They are expected after hard kills (a message reached the sink, but the worker died before the acknowledgement), and after connection kills that interrupt an acknowledgement.");
        L($"- Messages that reached the sink but are not Delivered: {sunkButNotDelivered:N0}. For example, a message delivered, its acknowledgement lost, then dead-lettered after its remaining attempts failed.");
        L($"- Non-poison messages dead-lettered: {normalDeadLettered:N0}.");
        L();
        L("## Delivery latency (created_at to delivered_at, database clock)");
        L();
        L($"| p50 | p95 | p99 | max | n |");
        L("|---|---|---|---|---|");
        L(string.Create(CultureInfo.InvariantCulture, $"| {P(0.50):F0} ms | {P(0.95):F0} ms | {P(0.99):F0} ms | {(latencies.Length == 0 ? 0 : latencies[^1]):F0} ms | {latencies.Length:N0} |"));
        L();
        L("## Long-lived worker (w0) resource trend");
        L();
        if (samples.Count > 0)
        {
            var warm = samples.SkipWhile(s => s.Elapsed < TimeSpan.FromMinutes(5)).ToList();
            if (warm.Count == 0)
            {
                warm = samples;
            }

            L("| | After warm-up | Max | Last |");
            L("|---|---|---|---|");
            L(string.Create(CultureInfo.InvariantCulture, $"| Working set | {warm[0].Rss / 1048576.0:F1} MB | {samples.Max(s => s.Rss) / 1048576.0:F1} MB | {samples[^1].Rss / 1048576.0:F1} MB |"));
            L($"| Threads | {warm[0].Threads} | {samples.Max(s => s.Threads)} | {samples[^1].Threads} |");
            L($"| Handles | {warm[0].Handles} | {samples.Max(s => s.Handles)} | {samples[^1].Handles} |");
            L();
            L(string.Create(CultureInfo.InvariantCulture, $"Max backlog seen: pending {samples.Max(s => s.Pending):N0}; max oldest-due age {samples.Max(s => s.OldestDueAge?.TotalSeconds ?? 0):F1} s."));
        }

        L();
        L("## Worker logs");
        L();
        L($"- `fail:` lines (dead-letter and persist-failure logs, expected under chaos): {workerErrors:N0}");
        L($"- Unexpected errors (unhandled exceptions, event 1011 \"unexpected error while processing\"): {(unexpected.Count == 0 ? "none" : string.Join("; ", unexpected))}");
        return (sb.ToString(), passed && unexpected.Count == 0);
    }

    /// <summary>Reads a log that a worker or the coordinator may still have open for writing.</summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private static void Log(string message) => Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss} {message}");

    private sealed class RunStats
    {
        public long CommittedTransactions;
        public long CommittedMessages;
        public long RolledBackTransactions;
        public long RolledBackMessages;
        public long PoisonMessages;
        public long ProducerErrors;
        public string? LastProducerError;
        public int WorkerKills;
        public int GracefulStops;
        public int ConnectionKillRounds;
        public int SessionsKilled;
        public double DrainSeconds;
    }

    private sealed record Sample(TimeSpan Elapsed, long Pending, long Processing, long Delivered, long DeadLettered, TimeSpan? OldestDueAge, long Rss, int Threads, int Handles);
}
