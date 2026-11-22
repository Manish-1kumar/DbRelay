using System.Collections.Concurrent;
using Dapper;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 4: a worker whose lease expired can never change the state recorded by the new owner.</summary>
public sealed class StaleWorkerTests(PostgreSqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stale_lock_token_cannot_overwrite_while_the_new_owner_delivers_or_after_it_delivered()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var store = producer.GetRequiredService<IOutboxStore>();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);

        // The stale worker claims with a short lease ...
        var staleToken = Guid.NewGuid();
        var claimed = await store.ClaimAsync(new ClaimRequest(10, TimeSpan.FromSeconds(1), staleToken, "stale-worker"), Ct);
        Assert.Equal(id, Assert.Single(claimed).Id);

        // ... and stalls past the lease (database clock).
        var staleRow = await schema.GetRequiredRowAsync(id);
        await Wait.UntilAsync(async () => await schema.DbNowAsync() > staleRow.AvailableAt, TimeSpan.FromSeconds(10), "the stale lease to expire");

        // A real dispatcher takes over. Its handler is held open so we can probe while it owns the lease.
        var inHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveries = new ConcurrentQueue<int>();
        using var host = RelayHost.Build(schema, "dbrelay-it-owner", async (message, _) =>
        {
            deliveries.Enqueue(message.Attempt);
            inHandler.TrySetResult();
            await release.Task;
            return DeliveryResult.Delivered();
        });
        await host.StartAsync(Ct);
        await inHandler.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var owned = await schema.GetRequiredRowAsync(id);
        Assert.Equal(OutboxMessageStatus.Processing, owned.State);
        Assert.Equal(2, owned.AttemptCount);
        Assert.NotEqual(staleToken, owned.LockToken);

        await AssertStaleTokenIsPowerlessAsync(store, id, staleToken);
        var stillOwned = await schema.GetRequiredRowAsync(id);
        Assert.Equal(OutboxMessageStatus.Processing, stillOwned.State);
        Assert.Equal(owned.LockToken, stillOwned.LockToken);
        Assert.Equal(owned.AvailableAt, stillOwned.AvailableAt);
        Assert.Equal(2, stillOwned.AttemptCount);
        Assert.Null(stillOwned.LastError);

        release.SetResult();
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.Delivered,
            TimeSpan.FromSeconds(30),
            "the new owner to record delivery");
        await host.StopAsync(Ct);

        var delivered = await schema.GetRequiredRowAsync(id);
        await AssertStaleTokenIsPowerlessAsync(store, id, staleToken);
        var after = await schema.GetRequiredRowAsync(id);

        Assert.Equal(new[] { 2 }, deliveries.ToArray());
        Assert.Equal(OutboxMessageStatus.Delivered, after.State);
        Assert.Equal(delivered.DeliveredAt, after.DeliveredAt);
        Assert.Equal(2, after.AttemptCount);
        Assert.Null(after.LastError);
        Assert.Null(after.DeadLetteredAt);
        Assert.Null(after.LockToken);
        Assert.Equal(delivered.AvailableAt, after.AvailableAt);
    }

    [Fact]
    public async Task Handler_that_ignores_its_timeout_is_abandoned_and_its_late_result_never_overwrites_the_redelivery()
    {
        // Revised after review finding CR-05. The dispatcher abandons a handler that ignores cancellation once DeliveryTimeout plus a
        // grace period has passed (inside the lease) and reschedules the message under its still-valid lock token. The slow handler's
        // late "Delivered" is therefore never written, so it cannot overwrite the redelivery. The lock-token guard itself is covered
        // directly by Stale_lock_token_cannot_overwrite_while_the_new_owner_delivers_or_after_it_delivered (above).
        var lease = TimeSpan.FromSeconds(4);
        var timeout = TimeSpan.FromSeconds(2);
        await using var schema = await TestSchema.CreateAsync(fixture);
        Guid id;
        await using (var producer = schema.CreateProducer())
        {
            id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);
        }

        var deliveries = new ConcurrentQueue<(string Host, int Attempt)>();
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowLogs = new CapturingLoggerProvider();

        // Host A ignores its delivery timeout (a badly behaved handler) and returns "Delivered" long after its lease expired.
        using var slow = RelayHost.Build(
            schema,
            "dbrelay-it-slow",
            async (message, _) =>
            {
                deliveries.Enqueue(("slow", message.Attempt));
                if (message.Attempt == 1)
                {
                    // Only the first attempt misbehaves. Once its handler is abandoned the slot is free again, so this host may also
                    // win the redelivery, and that attempt must complete normally.
                    slowStarted.TrySetResult();
                    await Task.Delay(lease + TimeSpan.FromSeconds(3), CancellationToken.None);
                    slowFinished.TrySetResult();
                }

                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.LeaseDuration = lease;
                d.DeliveryTimeout = timeout;
                d.MaxConcurrency = 1;
                d.BatchSize = 1;
            },
            logs: slowLogs);
        using var fast = RelayHost.Build(
            schema,
            "dbrelay-it-fast",
            (message, _) =>
            {
                deliveries.Enqueue(("fast", message.Attempt));
                return Task.FromResult(DeliveryResult.Delivered());
            },
            d =>
            {
                d.LeaseDuration = lease;
                d.DeliveryTimeout = timeout;
            });

        await slow.StartAsync(Ct);
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await fast.StartAsync(Ct);

        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.Delivered,
            TimeSpan.FromSeconds(30),
            "the message to be redelivered after the slow handler was abandoned");
        var deliveredByFast = await schema.GetRequiredRowAsync(id);

        await slowFinished.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct); // give any (incorrect) late write a chance to land
        await fast.StopAsync(Ct);
        await slow.StopAsync(Ct);

        var final = await schema.GetRequiredRowAsync(id);
        Assert.Equal(1, slowLogs.Count(1013)); // the slow host abandoned its handler
        Assert.Equal(0, slowLogs.Count(1007)); // and never even attempted to record the late outcome
        var attempts = deliveries.ToArray();
        Assert.Equal(2, attempts.Length);      // at-least-once: one duplicate delivery, never a lost or overwritten update
        Assert.Equal(("slow", 1), attempts[0]);
        Assert.Equal(2, attempts[1].Attempt);  // attempt 2 may run on either host
        Assert.Equal(OutboxMessageStatus.Delivered, final.State);
        Assert.Equal(2, final.AttemptCount);
        Assert.Equal(deliveredByFast.DeliveredAt, final.DeliveredAt);
        Assert.Null(final.LockToken);
    }

    [Fact]
    public async Task Real_dispatcher_whose_lease_was_taken_over_cannot_overwrite_the_new_owner()
    {
        // FU-T1: exercise the dispatcher's lease-lost path (event 1007) against the real database. While a real dispatcher's
        // handler is mid-delivery, its lease is expired with SQL and another worker claims the message. When the handler then
        // returns "Delivered", the dispatcher's guarded update must match no rows and the new owner's row must be untouched.
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var store = producer.GetRequiredService<IOutboxStore>();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);

        var inHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new CapturingLoggerProvider();
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-lease-taken",
            async (_, _) =>
            {
                inHandler.TrySetResult();
                await release.Task; // well inside DeliveryTimeout, so the dispatcher is still waiting normally
                return DeliveryResult.Delivered();
            },
            d =>
            {
                d.LeaseDuration = TimeSpan.FromSeconds(60);
                d.DeliveryTimeout = TimeSpan.FromSeconds(20);
                d.MaxConcurrency = 1; // its only slot is busy, so it cannot reclaim the message itself after the lease expires
                d.BatchSize = 1;
            },
            logs: logs);
        await host.StartAsync(Ct);
        await inHandler.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Simulate a long pause (GC, VM freeze) on the dispatcher: its lease runs out on the database clock ...
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            var expired = await connection.ExecuteAsync(
                $"UPDATE {schema.Outbox} SET available_at = now() - interval '1 second' WHERE id = @id", new { id });
            Assert.Equal(1, expired);
        }

        // ... and another worker takes the message over.
        var newOwnerToken = Guid.NewGuid();
        var takenOver = Assert.Single(await store.ClaimAsync(new ClaimRequest(10, TimeSpan.FromMinutes(5), newOwnerToken, "new-owner"), Ct));
        Assert.Equal(id, takenOver.Id);
        var ownedByNewOwner = await schema.GetRequiredRowAsync(id);

        // The original dispatcher finishes and tries to record "Delivered".
        release.SetResult();
        await Wait.UntilAsync(() => logs.Count(1007) == 1, TimeSpan.FromSeconds(30), "the dispatcher to report the lost lease (1007)");
        await host.StopAsync(Ct);

        var after = await schema.GetRequiredRowAsync(id);
        Assert.Equal(OutboxMessageStatus.Processing, after.State); // the stale "Delivered" did not land
        Assert.Equal(newOwnerToken, after.LockToken);
        Assert.Equal(ownedByNewOwner.AttemptCount, after.AttemptCount);
        Assert.Equal(ownedByNewOwner.AvailableAt, after.AvailableAt);
        Assert.Null(after.DeliveredAt);

        // The new owner's own outcome still works.
        Assert.True(await store.MarkDeliveredAsync(id, newOwnerToken, Ct));
    }

    private static async Task AssertStaleTokenIsPowerlessAsync(IOutboxStore store, Guid id, Guid staleToken)
    {
        Assert.False(await store.MarkDeliveredAsync(id, staleToken, Ct));
        Assert.False(await store.ScheduleRetryAsync(id, staleToken, TimeSpan.Zero, "stale retry", Ct));
        Assert.False(await store.MarkDeadLetteredAsync(id, staleToken, "stale dead-letter", Ct));
        Assert.False(await store.ReleaseAsync(id, staleToken, Ct));
    }
}
