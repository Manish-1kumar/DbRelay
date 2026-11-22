using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 1: several hosts, each with its own dispatcher, share one outbox table.</summary>
public sealed class MultiWorkerTests(PostgreSqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run()
    {
        const int HostCount = 4;
        const int MessageCount = 2000;
        const int OrdersPerTransaction = 10;
        const int Producers = 4;

        await using var schema = await TestSchema.CreateAsync(fixture);
        var deliveries = new ConcurrentQueue<(Guid Id, int Host, int Attempt)>();
        var logs = Enumerable.Range(0, HostCount).Select(_ => new CapturingLoggerProvider()).ToArray();

        var hosts = Enumerable.Range(0, HostCount).Select(h => RelayHost.Build(
            schema,
            $"dbrelay-it-mw-{h}",
            async (message, ct) =>
            {
                deliveries.Enqueue((message.Id, h, message.Attempt));
                await Task.Delay(Random.Shared.Next(1, 6), ct); // artificial broker latency
                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.BatchSize = 25;
                d.MaxConcurrency = 8;
                d.WorkerId = $"it-host-{h}";
            },
            logs: logs[h],
            maxPoolSize: 12)).ToArray();

        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();
        try
        {
            foreach (var host in hosts)
            {
                await host.StartAsync(Ct);
            }

            var total = Stopwatch.StartNew();
            var enqueued = new ConcurrentBag<Guid>();
            var transactionsPerProducer = MessageCount / OrdersPerTransaction / Producers;
            await Task.WhenAll(Enumerable.Range(0, Producers).Select(p => Task.Run(
                async () =>
                {
                    for (var t = 0; t < transactionsPerProducer; t++)
                    {
                        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
                        await using var transaction = await connection.BeginTransactionAsync(Ct);
                        for (var o = 0; o < OrdersPerTransaction; o++)
                        {
                            var sequence = (((p * transactionsPerProducer) + t) * OrdersPerTransaction) + o;
                            var orderId = Guid.NewGuid();
                            await Dapper.SqlMapper.ExecuteAsync(
                                connection,
                                $"INSERT INTO {schema.Orders} (id, sequence, note) VALUES (@orderId, @sequence, 'mw')",
                                new { orderId, sequence },
                                transaction);
                            enqueued.Add(await outbox.EnqueueAsync(transaction, new OrderPlaced(orderId, sequence, "mw"), Ct));
                        }

                        await transaction.CommitAsync(Ct);
                    }
                },
                Ct)));
            var enqueueTime = total.Elapsed;

            await Wait.UntilAsync(
                async () => (await schema.CountByStatusAsync()).GetValueOrDefault(OutboxMessageStatus.Delivered) == MessageCount,
                TimeSpan.FromSeconds(120),
                $"all {MessageCount} messages Delivered",
                pollMs: 100);
            var wall = total.Elapsed;

            foreach (var host in hosts)
            {
                await host.StopAsync(Ct);
            }

            var all = deliveries.ToArray();
            var ids = enqueued.ToHashSet();
            var perMessage = all.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.Count());
            var duplicates = all.Length - perMessage.Count;
            var perHost = Enumerable.Range(0, HostCount).Select(h => all.Count(d => d.Host == h)).ToArray();
            Metrics.Report(
                nameof(Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run),
                $"messages={MessageCount} hosts={HostCount} enqueue={enqueueTime.TotalMilliseconds:0}ms wall(enqueue+deliver)={wall.TotalMilliseconds:0}ms " +
                $"throughput={MessageCount / wall.TotalSeconds:0}/s deliveries={all.Length} duplicates={duplicates} perHost=[{string.Join(",", perHost)}] " +
                $"leaseLost={logs.Sum(l => l.Count(1007))}");

            Assert.Equal(MessageCount, ids.Count);
            Assert.Equal(MessageCount, await CountOrdersAsync(schema));

            // Every message delivered at least once ...
            Assert.DoesNotContain(ids, id => !perMessage.ContainsKey(id));

            // ... nothing unknown was delivered, and with no crashes there are no duplicates at all.
            Assert.DoesNotContain(perMessage.Keys, id => !ids.Contains(id));
            Assert.Equal(0, duplicates);
            Assert.All(all, d => Assert.Equal(1, d.Attempt));

            // The database agrees: every row Delivered on its first attempt, lock cleared.
            var rows = await schema.GetRowsAsync();
            Assert.Equal(MessageCount, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal(OutboxMessageStatus.Delivered, r.State);
                Assert.Equal(1, r.AttemptCount);
                Assert.Null(r.LockToken);
                Assert.NotNull(r.DeliveredAt);
            });

            // The work really was shared: every host's dispatcher delivered something.
            Assert.All(perHost, count => Assert.True(count > 0, $"A host delivered nothing: [{string.Join(",", perHost)}]"));
            Assert.All(logs, l => Assert.Equal(0, l.Count(1007))); // no lease lost
        }
        finally
        {
            foreach (var host in hosts)
            {
                host.Dispose();
            }
        }
    }

    private static async Task<long> CountOrdersAsync(TestSchema schema)
    {
        await using var connection = await schema.Fixture.DataSource.OpenConnectionAsync();
        return await Dapper.SqlMapper.ExecuteScalarAsync<long>(connection, $"SELECT count(*) FROM {schema.Orders}");
    }
}
