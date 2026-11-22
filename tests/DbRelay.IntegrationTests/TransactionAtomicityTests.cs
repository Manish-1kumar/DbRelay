using System.Collections.Concurrent;
using System.Data;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 6: the delivered set equals exactly the committed set, with a dispatcher polling during every transaction.</summary>
public sealed class TransactionAtomicityTests(PostgreSqlFixture fixture)
{
    private enum Ending
    {
        Commit,
        Rollback,
        DisposeWithoutCommit,
        BusinessFailureAfterEnqueue,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_committed_business_transactions_are_ever_delivered()
    {
        const int Transactions = 200;
        const int Seed = 20261119;

        await using var schema = await TestSchema.CreateAsync(fixture);
        var delivered = new ConcurrentQueue<Guid>();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-atomic",
            (message, _) =>
            {
                delivered.Enqueue(message.Id);
                return Task.FromResult(DeliveryResult.Delivered());
            },
            d => d.PollingInterval = TimeSpan.FromMilliseconds(20));
        await host.StartAsync(Ct);

        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();

        // A pre-existing order whose key the "business failure" transactions collide with after they enqueued.
        var existingOrder = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync($"INSERT INTO {schema.Orders} (id, sequence, note) VALUES (@existingOrder, -1, 'seed')", new { existingOrder });
        }

        var random = new Random(Seed);
        var plan = Enumerable.Range(0, Transactions)
            .Select(i => (Index: i, Ending: Pick(random.NextDouble()), HoldMs: random.Next(0, 25)))
            .ToArray();
        var committedMessages = new ConcurrentBag<Guid>();
        var committedOrders = new ConcurrentBag<Guid>();
        var abortedMessages = new ConcurrentBag<Guid>();

        await Parallel.ForEachAsync(plan, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = Ct }, async (step, ct) =>
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync(ct);
            var transaction = await connection.BeginTransactionAsync(ct);
            var orderId = Guid.NewGuid();
            Guid messageId;
            try
            {
                await connection.ExecuteAsync(
                    $"INSERT INTO {schema.Orders} (id, sequence, note) VALUES (@orderId, @sequence, @note)",
                    new { orderId, sequence = step.Index, note = step.Ending.ToString() },
                    transaction);
                messageId = await outbox.EnqueueAsync(transaction, new OrderPlaced(orderId, step.Index, step.Ending.ToString()), ct);

                // Keep the transaction open (uncommitted outbox row present) while the dispatcher polls.
                await Task.Delay(step.HoldMs, ct);

                switch (step.Ending)
                {
                    case Ending.Commit:
                        await transaction.CommitAsync(ct);
                        committedMessages.Add(messageId);
                        committedOrders.Add(orderId);
                        break;
                    case Ending.Rollback:
                        await transaction.RollbackAsync(ct);
                        abortedMessages.Add(messageId);
                        break;
                    case Ending.DisposeWithoutCommit:
                        abortedMessages.Add(messageId);
                        break; // disposed below: implicit rollback
                    case Ending.BusinessFailureAfterEnqueue:
                        abortedMessages.Add(messageId);
                        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(
                            $"INSERT INTO {schema.Orders} (id, sequence, note) VALUES (@existingOrder, @sequence, 'dup')",
                            new { existingOrder, sequence = step.Index },
                            transaction));
                        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
                        await transaction.RollbackAsync(ct);
                        break;
                }
            }
            finally
            {
                await transaction.DisposeAsync();
            }

            // DbRelay never closed the caller's connection: it is still usable after the transaction ended.
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT 1"));
        });

        var committed = committedMessages.ToHashSet();
        var aborted = abortedMessages.ToHashSet();
        await Wait.UntilAsync(() => committed.All(delivered.Contains), TimeSpan.FromSeconds(60), "all committed messages to be delivered");

        // Give the dispatcher many more polls to (wrongly) find anything else.
        await Task.Delay(1000, Ct);
        await host.StopAsync(Ct);

        var deliveredSet = delivered.ToHashSet();
        var counts = plan.GroupBy(p => p.Ending).ToDictionary(g => g.Key, g => g.Count());
        Metrics.Report(
            nameof(Only_committed_business_transactions_are_ever_delivered),
            $"seed={Seed} commit={counts.GetValueOrDefault(Ending.Commit)} rollback={counts.GetValueOrDefault(Ending.Rollback)} " +
            $"dispose={counts.GetValueOrDefault(Ending.DisposeWithoutCommit)} businessFailure={counts.GetValueOrDefault(Ending.BusinessFailureAfterEnqueue)} " +
            $"delivered={delivered.Count}");

        Assert.Equal(Transactions, committed.Count + aborted.Count);
        Assert.True(committed.Count > 0 && aborted.Count > 0);
        Assert.Equal(committed.OrderBy(x => x), deliveredSet.OrderBy(x => x));
        Assert.Empty(deliveredSet.Intersect(aborted));
        Assert.Equal(delivered.Count, deliveredSet.Count); // no duplicates

        // The tables agree: outbox rows exist exactly for committed transactions, and so do orders.
        var rows = await schema.GetRowsAsync();
        Assert.Equal(committed.OrderBy(x => x), rows.Select(r => r.Id).OrderBy(x => x));
        Assert.All(rows, r => Assert.Equal(OutboxMessageStatus.Delivered, r.State));
        await using var check = await fixture.DataSource.OpenConnectionAsync(Ct);
        var orders = (await check.QueryAsync<Guid>($"SELECT id FROM {schema.Orders} WHERE sequence >= 0")).ToHashSet();
        Assert.Equal(committedOrders.OrderBy(x => x), orders.OrderBy(x => x));
    }

    private static Ending Pick(double roll) => roll switch
    {
        < 0.5 => Ending.Commit,
        < 0.7 => Ending.Rollback,
        < 0.85 => Ending.DisposeWithoutCommit,
        _ => Ending.BusinessFailureAfterEnqueue,
    };
}
