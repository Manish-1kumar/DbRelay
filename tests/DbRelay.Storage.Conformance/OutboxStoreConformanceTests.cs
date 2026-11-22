using System.Collections.Concurrent;
using System.Data.Common;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DbRelay.Storage.Conformance;

[OutboxMessageType("conformance.test")]
public sealed record TestMessage(string Text);

[OutboxMessageType("conformance.other")]
public sealed record OtherMessage(int Value);

/// <summary>
/// The behavioural contract every <see cref="IOutboxStore"/> must satisfy, exercised through the public DbRelay API against a real database.
/// Each test instance gets its own schema, so tests (and target frameworks) can run in parallel against one database.
/// </summary>
public abstract class OutboxStoreConformanceTests : IAsyncLifetime
{
    /// <summary>Non-ASCII text: Latin-1 accents, a BMP symbol and an astral emoji (surrogate pair). Escaped so file encoding can't corrupt it.</summary>
    protected const string UnicodeSample = "ünïcødé ✓ 😀";

    private ServiceProvider? _services;

    protected string Schema { get; } = DbRelay.Testing.TestSchemaNames.New("t");

    protected IOutbox Outbox => _services!.GetRequiredService<IOutbox>();

    protected IOutboxAdmin Admin => _services!.GetRequiredService<IOutboxAdmin>();

    protected IOutboxStore Store => _services!.GetRequiredService<IOutboxStore>();

    protected IOutboxSchemaManager SchemaManager => _services!.GetRequiredService<IOutboxSchemaManager>();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Provider hooks -------------------------------------------------------------------------------------------

    /// <summary>Non-null when the database is unavailable; tests are then reported as skipped.</summary>
    protected abstract string? SkipReason { get; }

    /// <summary>Registers the provider with <paramref name="schema"/> as its schema name.</summary>
    protected abstract void UseStorage(DbRelayBuilder relay, string schema);

    /// <summary>Opens a new connection to the test database.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync();

    /// <summary>DDL creating a business table <c>{schema}.orders (id &lt;guid&gt; primary key, note &lt;text&gt; not null)</c>.</summary>
    protected abstract string CreateOrdersTableSql(string schema);

    /// <summary>Sets <c>available_at</c> of a message to one second before the database's current time (simulates lease expiry).</summary>
    protected abstract Task ExpireAsync(string schema, Guid messageId);

    /// <summary>
    /// Overwrites the stored <c>headers</c> column of a message with raw JSON text, bypassing DbRelay, to simulate rows written by other
    /// producers, manual fixes or migrations.
    /// </summary>
    protected abstract Task SetRawHeadersAsync(string schema, Guid messageId, string headersJson);

    /// <summary>Returns the database's current UTC time.</summary>
    protected abstract Task<DateTimeOffset> GetDatabaseNowAsync();

    /// <summary>Drops <paramref name="schema"/> and everything in it.</summary>
    protected abstract Task DropSchemaAsync(string schema);

    // ---- Lifecycle ---------------------------------------------------------------------------------------------------

    public async ValueTask InitializeAsync()
    {
        if (SkipReason is not null)
        {
            return;
        }

        var services = new ServiceCollection();
        services.AddLogging(); // hosts always register logging; DbRelay's hosted services depend on ILogger<T>
        services.AddDbRelay(relay =>
        {
            UseStorage(relay, Schema);
            relay.DisableDispatcher();
        });
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        await SchemaManager.EnsureCreatedAsync(Ct);
        await using var connection = await OpenConnectionAsync();
        await connection.ExecuteAsync(CreateOrdersTableSql(Schema));
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_services is null)
        {
            return;
        }

        await _services.DisposeAsync();
        await DropSchemaAsync(Schema);
    }

    private void RequireDatabase() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    private string InsertOrderSql => $"INSERT INTO {Schema}.orders (id, note) VALUES (@Id, @Note)";

    /// <summary>
    /// Counts committed rows in the business table from a separate connection without blocking on uncommitted rows
    /// (SQL Server without READ_COMMITTED_SNAPSHOT would otherwise wait for the caller's lock).
    /// </summary>
    protected virtual string CountCommittedOrdersSql(string schema) => $"SELECT COUNT(*) FROM {schema}.orders";

    private async Task<int> CountOrdersAsync()
    {
        await using var connection = await OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(CountCommittedOrdersSql(Schema));
    }

    private async Task<Guid> EnqueueStandaloneAsync(object message, EnqueueOptions? options = null) =>
        await Outbox.EnqueueStandaloneAsync(message, options, Ct);

    private Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(int batchSize = 100, TimeSpan? lease = null, Guid? token = null) =>
        Store.ClaimAsync(new ClaimRequest(batchSize, lease ?? TimeSpan.FromMinutes(5), token ?? Guid.NewGuid(), "conformance-worker"), Ct);

    // ---- Schema ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Schema_EnsureCreated_is_idempotent()
    {
        RequireDatabase();
        await SchemaManager.EnsureCreatedAsync(Ct);
        await SchemaManager.EnsureCreatedAsync(Ct);
        var id = await EnqueueStandaloneAsync(new TestMessage("after re-create"));
        Assert.NotNull(await Admin.GetAsync(id, Ct));
    }

    [Fact]
    public void Schema_create_script_uses_configured_schema()
    {
        RequireDatabase();
        var script = SchemaManager.GetCreateScript();
        Assert.Contains(Schema, script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP ", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Health_check_succeeds_when_table_exists()
    {
        RequireDatabase();
        await Store.CheckHealthAsync(Ct);
    }

    // ---- Transactions ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enqueue_commits_together_with_business_data_written_by_Dapper()
    {
        RequireDatabase();
        Guid messageId;
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await connection.ExecuteAsync(InsertOrderSql, new { Id = Guid.NewGuid(), Note = "order" }, transaction);
            messageId = await Outbox.EnqueueAsync(transaction, new TestMessage("committed"), Ct);
            await transaction.CommitAsync(Ct);
        }

        Assert.Equal(1, await CountOrdersAsync());
        var stored = await Admin.GetAsync(messageId, Ct);
        Assert.NotNull(stored);
        Assert.Equal(OutboxMessageStatus.Pending, stored.Status);
        Assert.Equal("conformance.test", stored.MessageType);
    }

    [Fact]
    public async Task Enqueue_is_rolled_back_with_business_data()
    {
        RequireDatabase();
        Guid messageId;
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await connection.ExecuteAsync(InsertOrderSql, new { Id = Guid.NewGuid(), Note = "order" }, transaction);
            messageId = await Outbox.EnqueueAsync(transaction, new TestMessage("rolled back"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(0, await CountOrdersAsync());
        Assert.Null(await Admin.GetAsync(messageId, Ct));
    }

    [Fact]
    public async Task Enqueue_is_invisible_to_claimers_until_commit()
    {
        RequireDatabase();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var messageId = await Outbox.EnqueueAsync(transaction, new TestMessage("uncommitted"), Ct);

        var whileOpen = await ClaimAsync();
        Assert.DoesNotContain(whileOpen, m => m.Id == messageId);

        await transaction.CommitAsync(Ct);
        var afterCommit = await ClaimAsync();
        Assert.Contains(afterCommit, m => m.Id == messageId);
    }

    [Fact]
    public async Task Enqueue_never_commits_closes_or_disposes_caller_resources()
    {
        RequireDatabase();
        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);

        await Outbox.EnqueueAsync(transaction, new TestMessage("ownership"), Ct);

        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);

        // The transaction is still active and usable for more business work.
        await connection.ExecuteAsync(InsertOrderSql, new { Id = Guid.NewGuid(), Note = "after enqueue" }, transaction);
        Assert.Equal(0, await CountOrdersAsync()); // not committed by DbRelay

        await transaction.CommitAsync(Ct);
        Assert.Equal(1, await CountOrdersAsync());
    }

    [Fact]
    public async Task Enqueue_with_duplicate_message_id_fails_and_caller_can_roll_back()
    {
        RequireDatabase();
        var id = Guid.NewGuid();
        await EnqueueStandaloneAsync(new TestMessage("first"), new EnqueueOptions { MessageId = id });

        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await connection.ExecuteAsync(InsertOrderSql, new { Id = Guid.NewGuid(), Note = "order" }, transaction);

        await Assert.ThrowsAnyAsync<DbException>(() =>
            Outbox.EnqueueAsync(transaction, new TestMessage("duplicate"), new EnqueueOptions { MessageId = id }, Ct));

        await transaction.RollbackAsync(Ct);
        Assert.Equal(0, await CountOrdersAsync());
        var stored = await Admin.GetAsync(id, Ct);
        Assert.NotNull(stored);
        Assert.Equal("{\"text\":\"first\"}", System.Text.Encoding.UTF8.GetString(stored.Payload.Span));
    }

    [Fact]
    public async Task Enqueue_rejects_completed_transaction()
    {
        RequireDatabase();
        await using var connection = await OpenConnectionAsync();
        var transaction = await connection.BeginTransactionAsync(Ct);
        await transaction.CommitAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Outbox.EnqueueAsync(transaction, new TestMessage("late"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Outbox.EnqueueAsync(connection, transaction, new TestMessage("late"), null, Ct));
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task Enqueue_rejects_transaction_from_another_connection()
    {
        RequireDatabase();
        await using var first = await OpenConnectionAsync();
        await using var second = await OpenConnectionAsync();
        await using var transaction = await first.BeginTransactionAsync(Ct);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Outbox.EnqueueAsync(second, transaction, new TestMessage("mismatch"), null, Ct));
    }

    [Fact]
    public async Task Enqueue_rejects_closed_connection()
    {
        RequireDatabase();
        await using var connection = await OpenConnectionAsync();
        await connection.CloseAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Outbox.EnqueueAsync(connection, null, new TestMessage("closed"), null, Ct));
    }

    [Fact]
    public async Task Enqueue_rejects_connection_of_another_provider()
    {
        RequireDatabase();
        using var foreign = new FakeDbConnection();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Outbox.EnqueueAsync(foreign, null, new TestMessage("foreign"), null, Ct));
    }

    [Fact]
    public async Task Enqueue_without_transaction_autocommits_on_caller_connection()
    {
        RequireDatabase();
        await using var connection = await OpenConnectionAsync();
        var id = await Outbox.EnqueueAsync(connection, null, new TestMessage("autocommit"), null, Ct);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.NotNull(await Admin.GetAsync(id, Ct));
    }

    [Fact]
    public async Task EnqueueMany_inserts_all_messages_atomically()
    {
        RequireDatabase();
        IReadOnlyList<Guid> ids;
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            ids = await Outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, 250).Select(i => new TestMessage($"m{i}")), Ct);
            await transaction.RollbackAsync(Ct);
        }

        Assert.Equal(250, ids.Count);
        Assert.Equal(250, ids.Distinct().Count());
        Assert.Null(await Admin.GetAsync(ids[0], Ct));

        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            ids = await Outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, 250).Select(i => new TestMessage($"m{i}")), Ct);
            await transaction.CommitAsync(Ct);
        }

        var stats = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(250, stats.Pending);
        var last = await Admin.GetAsync(ids[^1], Ct);
        Assert.Equal("{\"text\":\"m249\"}", System.Text.Encoding.UTF8.GetString(last!.Payload.Span));
    }

    [Fact]
    public async Task EnqueueRaw_preserves_payload_bytes_exactly()
    {
        RequireDatabase();
        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        bytes[0] = 0x00;
        bytes[1] = 0xFF;

        await using var connection = await OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        var id = await Outbox.EnqueueRawAsync(transaction, new RawOutboxMessage("conformance.raw", bytes, "application/octet-stream"), Ct);
        await transaction.CommitAsync(Ct);

        var stored = await Admin.GetAsync(id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(bytes, stored.Payload.ToArray());
        Assert.Equal("application/octet-stream", stored.ContentType);
        Assert.Equal("conformance.raw", stored.MessageType);
    }

    [Fact]
    public async Task Enqueue_stores_all_metadata()
    {
        RequireDatabase();
        var options = new EnqueueOptions
        {
            Destination = "orders-topic",
            CorrelationId = "corr-1",
            CausationId = "cause-1",
            Headers = new Dictionary<string, string> { ["tenant"] = "acme", ["unicode"] = UnicodeSample },
        };

        var before = await GetDatabaseNowAsync();
        var id = await EnqueueStandaloneAsync(new TestMessage("meta"), options);
        var after = await GetDatabaseNowAsync();

        var stored = await Admin.GetAsync(id, Ct);
        Assert.NotNull(stored);
        Assert.Equal(id, stored.Id);
        Assert.Equal("orders-topic", stored.Destination);
        Assert.Equal("corr-1", stored.CorrelationId);
        Assert.Equal("cause-1", stored.CausationId);
        Assert.Equal("acme", stored.Headers["tenant"]);
        Assert.Equal(UnicodeSample, stored.Headers["unicode"]);
        Assert.Equal("application/json", stored.ContentType);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Equal(TimeSpan.Zero, stored.CreatedAt.Offset);
        Assert.InRange(stored.CreatedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.InRange(stored.ScheduledAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(stored.ScheduledAt, stored.AvailableAt);
        Assert.Null(stored.DeliveredAt);
        Assert.Null(stored.DeadLetteredAt);
        Assert.Null(stored.LastError);
    }

    // ---- Claiming --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Claim_marks_messages_processing_and_counts_the_attempt()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("claim me"));
        var token = Guid.NewGuid();
        var before = await GetDatabaseNowAsync();

        var claimed = Assert.Single(await ClaimAsync(token: token, lease: TimeSpan.FromMinutes(2)));

        Assert.Equal(id, claimed.Id);
        Assert.Equal(OutboxMessageStatus.Processing, claimed.Status);
        Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal("conformance-worker", claimed.LockedBy);
        Assert.NotNull(claimed.LastAttemptAt);
        Assert.InRange(claimed.AvailableAt, before.AddMinutes(2).AddSeconds(-2), before.AddMinutes(2).AddSeconds(30));
        Assert.Equal("{\"text\":\"claim me\"}", System.Text.Encoding.UTF8.GetString(claimed.Payload.Span));

        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.Processing, stored!.Status);
        Assert.Equal(1, stored.AttemptCount);
    }

    [Fact]
    public async Task Claim_stops_at_the_payload_budget_but_always_takes_the_first_message()
    {
        // CR-18 / ADR 0004: a message is claimed only if the payloads before it in the batch total less than the budget.
        RequireDatabase();
        var ids = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            ids.Add(await EnqueueStandaloneAsync(new TestMessage(new string('x', 280) + i)));
        }

        var size = (await Admin.GetAsync(ids[0], Ct))!.Payload.Length;

        // Budget 1 byte: only the first message (nothing precedes it), however large it is.
        var first = await Store.ClaimAsync(new ClaimRequest(100, TimeSpan.FromMinutes(5), Guid.NewGuid(), "w") { MaxPayloadBytes = 1 }, Ct);
        Assert.Equal([ids[0]], first.Select(m => m.Id));

        // Budget 3 x size + 1: the next four (0, size, 2 x size and 3 x size bytes precede them), not the fifth.
        var next = await Store.ClaimAsync(new ClaimRequest(100, TimeSpan.FromMinutes(5), Guid.NewGuid(), "w") { MaxPayloadBytes = (3L * size) + 1 }, Ct);
        Assert.Equal(ids.Skip(1).Take(4), next.Select(m => m.Id));

        // The cut message was not touched and is claimed normally.
        var rest = await ClaimAsync();
        var last = Assert.Single(rest);
        Assert.Equal(ids[5], last.Id);
        Assert.Equal(1, last.AttemptCount);

        // The default budget (64 MiB) does not limit ordinary batches.
        Assert.Equal(OutboxLimits.BatchPayloadBudgetBytes, new ClaimRequest(1, TimeSpan.FromSeconds(1), Guid.NewGuid(), "w").MaxPayloadBytes);
    }

    [Fact]
    public async Task Claim_respects_batch_size_and_availability_order()
    {
        RequireDatabase();
        var now = await GetDatabaseNowAsync();
        var third = await EnqueueStandaloneAsync(new TestMessage("3"), new EnqueueOptions { DeliverAt = now.AddMinutes(-1) });
        var first = await EnqueueStandaloneAsync(new TestMessage("1"), new EnqueueOptions { DeliverAt = now.AddMinutes(-3) });
        var second = await EnqueueStandaloneAsync(new TestMessage("2"), new EnqueueOptions { DeliverAt = now.AddMinutes(-2) });

        var batch = await ClaimAsync(batchSize: 2);
        Assert.Equal([first, second], batch.Select(m => m.Id));

        var rest = await ClaimAsync(batchSize: 2);
        Assert.Equal([third], rest.Select(m => m.Id));
    }

    [Fact]
    public async Task Claim_skips_messages_scheduled_in_the_future()
    {
        RequireDatabase();
        var now = await GetDatabaseNowAsync();
        var delayed = await EnqueueStandaloneAsync(new TestMessage("delay"), new EnqueueOptions { Delay = TimeSpan.FromHours(1) });
        var scheduled = await EnqueueStandaloneAsync(new TestMessage("at"), new EnqueueOptions { DeliverAt = now.AddHours(1) });
        var due = await EnqueueStandaloneAsync(new TestMessage("due"), new EnqueueOptions { Delay = TimeSpan.Zero });

        var claimed = await ClaimAsync();
        Assert.Equal([due], claimed.Select(m => m.Id));

        var delayedRow = await Admin.GetAsync(delayed, Ct);
        Assert.InRange(delayedRow!.ScheduledAt, now.AddMinutes(59), now.AddMinutes(61));
        var scheduledRow = await Admin.GetAsync(scheduled, Ct);
        Assert.InRange(scheduledRow!.AvailableAt, now.AddHours(1).AddSeconds(-1), now.AddHours(1).AddSeconds(1));
    }

    [Fact]
    public async Task Claim_does_not_return_messages_under_a_valid_lease()
    {
        RequireDatabase();
        await EnqueueStandaloneAsync(new TestMessage("once"));
        Assert.Single(await ClaimAsync());
        Assert.Empty(await ClaimAsync());
    }

    [Fact]
    public async Task Claim_reclaims_messages_whose_lease_expired()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("crash"));
        var firstToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: firstToken));

        await ExpireAsync(Schema, id); // the first worker "crashed"

        var secondToken = Guid.NewGuid();
        var reclaimed = Assert.Single(await ClaimAsync(token: secondToken));
        Assert.Equal(id, reclaimed.Id);
        Assert.Equal(2, reclaimed.AttemptCount);

        // The crashed worker's stale token can no longer change the row.
        Assert.False(await Store.MarkDeliveredAsync(id, firstToken, Ct));
        Assert.True(await Store.MarkDeliveredAsync(id, secondToken, Ct));
    }

    [Fact]
    public async Task Claim_never_returns_delivered_or_dead_lettered_messages()
    {
        RequireDatabase();
        var delivered = await EnqueueStandaloneAsync(new TestMessage("delivered"));
        var dead = await EnqueueStandaloneAsync(new TestMessage("dead"));
        var token = Guid.NewGuid();
        Assert.Equal(2, (await ClaimAsync(token: token)).Count);
        Assert.True(await Store.MarkDeliveredAsync(delivered, token, Ct));
        Assert.True(await Store.MarkDeadLetteredAsync(dead, token, "boom", Ct));

        await ExpireAsync(Schema, delivered);
        await ExpireAsync(Schema, dead);
        Assert.Empty(await ClaimAsync());
    }

    [Fact]
    public async Task Claim_tolerates_rows_with_headers_DbRelay_did_not_write()
    {
        // CR-01: rows mapped after the claim commits must never fail the whole batch.
        RequireDatabase();
        var numeric = await EnqueueStandaloneAsync(new TestMessage("numeric headers"));
        var array = await EnqueueStandaloneAsync(new TestMessage("array headers"));
        var healthy = await EnqueueStandaloneAsync(new TestMessage("healthy"), new EnqueueOptions { Headers = new Dictionary<string, string> { ["k"] = "v" } });
        await SetRawHeadersAsync(Schema, numeric, "{\"retries\": 1, \"source\": \"legacy\"}");
        await SetRawHeadersAsync(Schema, array, "[1, 2, 3]");

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(token: token);

        Assert.Equal(3, claimed.Count);
        var numericRow = claimed.Single(m => m.Id == numeric);
        Assert.Equal("1", numericRow.Headers["retries"]);
        Assert.Equal("legacy", numericRow.Headers["source"]);
        Assert.True(claimed.Single(m => m.Id == array).Headers.ContainsKey(OutboxHeaders.ErrorHeader));
        Assert.Equal("v", claimed.Single(m => m.Id == healthy).Headers["k"]);
        foreach (var message in claimed)
        {
            Assert.True(await Store.MarkDeliveredAsync(message.Id, token, Ct));
        }

        Assert.Equal(3, (await Admin.GetStatisticsAsync(Ct)).Delivered);
        Assert.NotNull(await Admin.GetAsync(array, Ct)); // admin reads tolerate the row too
    }

    // ---- Optional capability: IOutboxBatchStore (ADR 0003) -----------------------------------------------------------

    private IOutboxBatchStore RequireBatchStore()
    {
        RequireDatabase();
        Assert.SkipUnless(Store is IOutboxBatchStore, "This provider does not implement the optional IOutboxBatchStore capability.");
        return (IOutboxBatchStore)Store;
    }

    [Fact]
    public async Task Batch_mark_delivered_updates_only_rows_that_still_hold_their_token()
    {
        var batch = RequireBatchStore();
        for (var i = 0; i < 5; i++)
        {
            await EnqueueStandaloneAsync(new TestMessage($"b{i}"));
        }

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(token: token);
        Assert.Equal(5, claimed.Count);

        // One message's lease expires and another worker takes it over.
        var stolen = claimed[0].Id;
        await ExpireAsync(Schema, stolen);
        var newToken = Guid.NewGuid();
        Assert.Equal(stolen, Assert.Single(await ClaimAsync(token: newToken)).Id);

        var items = claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).Append(new OutboxAcknowledgement(Guid.NewGuid(), token)).ToList();
        var updated = await batch.MarkDeliveredBatchAsync(items, Ct);

        Assert.Equal(claimed.Skip(1).Select(m => m.Id).OrderBy(i => i), updated.Select(a => a.MessageId).OrderBy(i => i)); // not the stolen one, not the unknown one
        foreach (var message in claimed.Skip(1))
        {
            var row = await Admin.GetAsync(message.Id, Ct);
            Assert.Equal(OutboxMessageStatus.Delivered, row!.Status);
            Assert.NotNull(row.DeliveredAt);
        }

        var stolenRow = await Admin.GetAsync(stolen, Ct);
        Assert.Equal(OutboxMessageStatus.Processing, stolenRow!.Status); // the new owner's lease is untouched
        Assert.True(await Store.MarkDeliveredAsync(stolen, newToken, Ct));
        Assert.Empty(await batch.MarkDeliveredBatchAsync(items, Ct)); // a repeated acknowledgement changes nothing
    }

    [Fact]
    public async Task Batch_acknowledgements_check_each_rows_own_token()
    {
        var batch = RequireBatchStore();
        var first = await EnqueueStandaloneAsync(new TestMessage("first"));
        var firstToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(batchSize: 1, token: firstToken));
        var second = await EnqueueStandaloneAsync(new TestMessage("second"));
        var secondToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(batchSize: 1, token: secondToken));

        // Mixed claims in one batch, plus a swapped token that must not match.
        var updated = await batch.MarkDeliveredBatchAsync(
            [new OutboxAcknowledgement(first, firstToken), new OutboxAcknowledgement(second, firstToken)], Ct);
        Assert.Equal([new OutboxAcknowledgement(first, firstToken)], updated);
        Assert.Equal(OutboxMessageStatus.Processing, (await Admin.GetAsync(second, Ct))!.Status);

        Assert.Equal([new OutboxAcknowledgement(second, secondToken)], await batch.MarkDeliveredBatchAsync([new OutboxAcknowledgement(second, secondToken)], Ct));
        Assert.Empty(await batch.MarkDeliveredBatchAsync([], Ct));
    }

    [Fact]
    public async Task Batch_release_returns_messages_without_counting_the_attempt()
    {
        var batch = RequireBatchStore();
        for (var i = 0; i < 3; i++)
        {
            await EnqueueStandaloneAsync(new TestMessage($"r{i}"));
        }

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(token: token);
        var released = await batch.ReleaseBatchAsync(
            claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).Append(new OutboxAcknowledgement(claimed[0].Id, Guid.NewGuid())).ToList(), Ct);

        Assert.Equal(claimed.Select(m => m.Id).OrderBy(i => i), released.Select(a => a.MessageId).OrderBy(i => i));
        foreach (var message in claimed)
        {
            var row = await Admin.GetAsync(message.Id, Ct);
            Assert.Equal(OutboxMessageStatus.Pending, row!.Status);
            Assert.Equal(0, row.AttemptCount);
            Assert.Null(row.LockedBy);
        }

        Assert.Equal(3, (await ClaimAsync()).Count); // immediately claimable again
    }

    [Fact]
    public async Task Batch_acknowledgements_handle_a_full_batch()
    {
        var batch = RequireBatchStore();
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await Outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, OutboxLimits.MaxClaimBatchSize).Select(i => new TestMessage($"f{i}")), Ct);
            await transaction.CommitAsync(Ct);
        }

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(batchSize: OutboxLimits.MaxClaimBatchSize, token: token);
        Assert.Equal(OutboxLimits.MaxClaimBatchSize, claimed.Count);

        var updated = await batch.MarkDeliveredBatchAsync(claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).ToList(), Ct);

        Assert.Equal(OutboxLimits.MaxClaimBatchSize, updated.Count);
        Assert.Equal(OutboxLimits.MaxClaimBatchSize, (await Admin.GetStatisticsAsync(Ct)).Delivered);
    }

    [Fact]
    public async Task Batch_release_never_touches_a_message_reclaimed_by_another_worker()
    {
        // BA-02: the guard on the batched release path. It is load-bearing for "insufficient lease time remaining", where another
        // worker may already own the message. Removing the token predicate previously survived every suite.
        var batch = RequireBatchStore();
        var reclaimedId = await EnqueueStandaloneAsync(new TestMessage("reclaimed"));
        var validId = await EnqueueStandaloneAsync(new TestMessage("valid"));
        var staleToken = Guid.NewGuid();
        Assert.Equal(2, (await ClaimAsync(token: staleToken)).Count);

        await ExpireAsync(Schema, reclaimedId);
        var ownerToken = Guid.NewGuid();
        Assert.Equal(reclaimedId, Assert.Single(await ClaimAsync(token: ownerToken)).Id);
        var ownedBefore = await Admin.GetAsync(reclaimedId, Ct);

        var released = await batch.ReleaseBatchAsync(
            [new OutboxAcknowledgement(reclaimedId, staleToken), new OutboxAcknowledgement(validId, staleToken)], Ct);

        Assert.Equal([new OutboxAcknowledgement(validId, staleToken)], released);
        var owned = await Admin.GetAsync(reclaimedId, Ct);
        Assert.Equal(OutboxMessageStatus.Processing, owned!.Status);                 // still the new owner's
        Assert.Equal(ownedBefore!.AttemptCount, owned.AttemptCount);                   // attempt not given back
        Assert.Equal(ownedBefore.AvailableAt, owned.AvailableAt);                      // lease untouched
        Assert.Equal(ownedBefore.LockedBy, owned.LockedBy);
        Assert.True(await Store.MarkDeliveredAsync(reclaimedId, ownerToken, Ct));      // the owner's token still works
        Assert.Equal(OutboxMessageStatus.Pending, (await Admin.GetAsync(validId, Ct))!.Status);
    }

    [Fact]
    public async Task Batch_results_distinguish_a_stale_and_a_current_token_for_the_same_message()
    {
        // BA-03: results are (id, token) pairs, so a stale duplicate is never reported as recorded.
        var batch = RequireBatchStore();
        var id = await EnqueueStandaloneAsync(new TestMessage("dup"));
        var staleToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: staleToken));
        await ExpireAsync(Schema, id);
        var currentToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: currentToken));
        var other = await EnqueueStandaloneAsync(new TestMessage("other"));
        var otherToken = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: otherToken));

        var updated = await batch.MarkDeliveredBatchAsync(
            [new OutboxAcknowledgement(id, staleToken), new OutboxAcknowledgement(id, currentToken), new OutboxAcknowledgement(other, otherToken)], Ct);

        Assert.Equal(2, updated.Count);
        Assert.Contains(new OutboxAcknowledgement(id, currentToken), updated);
        Assert.Contains(new OutboxAcknowledgement(other, otherToken), updated);
        Assert.DoesNotContain(new OutboxAcknowledgement(id, staleToken), updated);
    }

    [Theory]
    [InlineData(65)]   // SQL Server: one 64-item statement plus a one-item tail chunk (single-row path)
    [InlineData(129)]  // two full chunks plus a one-item tail
    [InlineData(100)]  // a full chunk plus a padded partial chunk
    public async Task Batch_acknowledgements_handle_chunk_boundaries(int count)
    {
        var batch = RequireBatchStore();
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await Outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, count).Select(i => new TestMessage($"k{i}")), Ct);
            await transaction.CommitAsync(Ct);
        }

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(batchSize: count, token: token);
        Assert.Equal(count, claimed.Count);
        var stale = claimed[^1].Id; // make the very last item (the tail) stale
        await ExpireAsync(Schema, stale);
        Assert.Single(await ClaimAsync(token: Guid.NewGuid()));

        var updated = await batch.MarkDeliveredBatchAsync(claimed.Select(m => new OutboxAcknowledgement(m.Id, token)).ToList(), Ct);

        Assert.Equal(count - 1, updated.Count);
        Assert.DoesNotContain(updated, a => a.MessageId == stale);
        Assert.Equal(count - 1, (await Admin.GetStatisticsAsync(Ct)).Delivered);
    }

    [Fact]
    public async Task Concurrent_claimers_never_receive_the_same_message()
    {
        RequireDatabase();
        const int MessageCount = 300;
        await using (var connection = await OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync(Ct))
        {
            await Outbox.EnqueueManyAsync(transaction, Enumerable.Range(0, MessageCount).Select(i => new TestMessage($"c{i}")), Ct);
            await transaction.CommitAsync(Ct);
        }

        var claimed = new ConcurrentBag<Guid>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            while (claimed.Count < MessageCount && !timeout.IsCancellationRequested)
            {
                var batch = await Store.ClaimAsync(new ClaimRequest(7, TimeSpan.FromMinutes(5), Guid.NewGuid(), $"w{worker}"), timeout.Token);
                foreach (var message in batch)
                {
                    claimed.Add(message.Id);
                }
            }
        }, timeout.Token));

        await Task.WhenAll(workers);

        Assert.Equal(MessageCount, claimed.Count);
        Assert.Equal(MessageCount, claimed.Distinct().Count());
        var stats = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(MessageCount, stats.Processing);
        Assert.Equal(0, stats.Pending);
    }

    // ---- Lock-token guarded transitions ----------------------------------------------------------------------------

    [Fact]
    public async Task MarkDelivered_records_delivery_and_clears_lock()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("deliver"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));

        Assert.True(await Store.MarkDeliveredAsync(id, token, Ct));
        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.Delivered, stored!.Status);
        Assert.NotNull(stored.DeliveredAt);

        // A second acknowledgement with the same token is rejected (the lock was cleared).
        Assert.False(await Store.MarkDeliveredAsync(id, token, Ct));
    }

    [Fact]
    public async Task Transitions_with_a_wrong_token_change_nothing()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("guarded"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));
        var wrong = Guid.NewGuid();

        Assert.False(await Store.MarkDeliveredAsync(id, wrong, Ct));
        Assert.False(await Store.ScheduleRetryAsync(id, wrong, TimeSpan.Zero, "x", Ct));
        Assert.False(await Store.MarkDeadLetteredAsync(id, wrong, "x", Ct));
        Assert.False(await Store.ReleaseAsync(id, wrong, Ct));

        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.Processing, stored!.Status);
        Assert.Equal(1, stored.AttemptCount);
        Assert.Null(stored.LastError);
        Assert.True(await Store.MarkDeliveredAsync(id, token, Ct));
    }

    [Fact]
    public async Task Transitions_on_unknown_message_return_false()
    {
        RequireDatabase();
        Assert.False(await Store.MarkDeliveredAsync(Guid.NewGuid(), Guid.NewGuid(), Ct));
        Assert.False(await Store.ReleaseAsync(Guid.NewGuid(), Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task ScheduleRetry_returns_message_to_pending_after_the_delay()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("retry"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));
        var now = await GetDatabaseNowAsync();

        Assert.True(await Store.ScheduleRetryAsync(id, token, TimeSpan.FromMinutes(10), "broker unavailable ✗ 😀", Ct));

        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.Pending, stored!.Status);
        Assert.Equal("broker unavailable ✗ 😀", stored.LastError);
        Assert.Equal(1, stored.AttemptCount);
        Assert.InRange(stored.AvailableAt, now.AddMinutes(10).AddSeconds(-2), now.AddMinutes(10).AddSeconds(30));
        Assert.Empty(await ClaimAsync());

        await ExpireAsync(Schema, id);
        var again = Assert.Single(await ClaimAsync());
        Assert.Equal(2, again.AttemptCount);
    }

    [Fact]
    public async Task ScheduleRetry_with_zero_delay_is_immediately_claimable()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("retry-now"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));
        Assert.True(await Store.ScheduleRetryAsync(id, token, TimeSpan.Zero, "transient", Ct));
        Assert.Equal(id, Assert.Single(await ClaimAsync()).Id);
    }

    [Fact]
    public async Task MarkDeadLettered_records_reason_and_time()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("poison"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));
        var longError = new string('e', 2000);

        Assert.True(await Store.MarkDeadLetteredAsync(id, token, longError, Ct));

        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.DeadLettered, stored!.Status);
        Assert.NotNull(stored.DeadLetteredAt);
        Assert.Equal(longError, stored.LastError);
    }

    [Fact]
    public async Task Release_returns_message_without_counting_the_attempt()
    {
        RequireDatabase();
        var id = await EnqueueStandaloneAsync(new TestMessage("release"));
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));

        Assert.True(await Store.ReleaseAsync(id, token, Ct));

        var stored = await Admin.GetAsync(id, Ct);
        Assert.Equal(OutboxMessageStatus.Pending, stored!.Status);
        Assert.Equal(0, stored.AttemptCount);
        var reclaimed = Assert.Single(await ClaimAsync());
        Assert.Equal(1, reclaimed.AttemptCount);
    }

    // ---- Administration --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_returns_null_for_unknown_id()
    {
        RequireDatabase();
        Assert.Null(await Admin.GetAsync(Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task Query_filters_by_status_and_type_and_pages()
    {
        RequireDatabase();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await EnqueueStandaloneAsync(new TestMessage($"q{i}")));
        }

        var other = await EnqueueStandaloneAsync(new OtherMessage(1));
        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(batchSize: 1, token: token);
        Assert.True(await Store.MarkDeadLetteredAsync(claimed[0].Id, token, "dead", Ct));

        var dead = await Admin.QueryAsync(new OutboxQuery { Status = OutboxMessageStatus.DeadLettered }, Ct);
        Assert.Equal([claimed[0].Id], dead.Select(m => m.Id));

        var others = await Admin.QueryAsync(new OutboxQuery { MessageType = "conformance.other" }, Ct);
        Assert.Equal([other], others.Select(m => m.Id));

        var pending = await Admin.QueryAsync(new OutboxQuery { Status = OutboxMessageStatus.Pending, MessageType = "conformance.test" }, Ct);
        Assert.Equal(4, pending.Count);

        var page1 = await Admin.QueryAsync(new OutboxQuery { Take = 2 }, Ct);
        var page2 = await Admin.QueryAsync(new OutboxQuery { Skip = 2, Take = 2 }, Ct);
        var page4 = await Admin.QueryAsync(new OutboxQuery { Skip = 6, Take = 2 }, Ct);
        Assert.Equal(2, page1.Count);
        Assert.Equal(2, page2.Count);
        Assert.Empty(page4);
        Assert.Empty(page1.Select(m => m.Id).Intersect(page2.Select(m => m.Id)));
        Assert.All(page1, m => Assert.Contains(m.Id, ids)); // first page holds the oldest messages (ties are provider-ordered)

        var future = await Admin.QueryAsync(new OutboxQuery { CreatedAfter = DateTimeOffset.UtcNow.AddHours(1) }, Ct);
        Assert.Empty(future);
        var past = await Admin.QueryAsync(new OutboxQuery { CreatedBefore = DateTimeOffset.UtcNow.AddHours(1), Take = 1000 }, Ct);
        Assert.Equal(6, past.Count);

        // Pages are consecutive slices of one stable order (oldest first), with full rows.
        var all = await Admin.QueryAsync(new OutboxQuery { Take = 1000 }, Ct);
        Assert.Equal(all.Select(m => m.CreatedAt).Order(), all.Select(m => m.CreatedAt));
        var page3 = await Admin.QueryAsync(new OutboxQuery { Skip = 4, Take = 2 }, Ct);
        Assert.Equal(all.Select(m => m.Id), page1.Concat(page2).Concat(page3).Select(m => m.Id));
        Assert.All(all, m => Assert.False(m.Payload.IsEmpty));

        // CR-21: offsets are bounded.
        Assert.Empty(await Admin.QueryAsync(new OutboxQuery { Skip = 10_000 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Admin.QueryAsync(new OutboxQuery { Skip = 10_001 }, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Admin.QueryAsync(new OutboxQuery { Skip = -1 }, Ct));
    }

    [Fact]
    public async Task Statistics_count_messages_by_status()
    {
        RequireDatabase();
        var empty = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(0, empty.Pending + empty.Processing + empty.Delivered + empty.DeadLettered);
        Assert.Null(empty.OldestDueAge);

        var now = await GetDatabaseNowAsync();
        await EnqueueStandaloneAsync(new TestMessage("old"), new EnqueueOptions { DeliverAt = now.AddMinutes(-10) });
        for (var i = 0; i < 4; i++)
        {
            await EnqueueStandaloneAsync(new TestMessage($"s{i}"));
        }

        await EnqueueStandaloneAsync(new TestMessage("future"), new EnqueueOptions { Delay = TimeSpan.FromHours(1) });

        var token = Guid.NewGuid();
        var claimed = await ClaimAsync(batchSize: 3, token: token); // includes "old" (earliest available_at)
        Assert.True(await Store.MarkDeliveredAsync(claimed[1].Id, token, Ct));
        Assert.True(await Store.MarkDeadLetteredAsync(claimed[2].Id, token, "dead", Ct));

        var stats = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(3, stats.Pending);       // 2 due + 1 future
        Assert.Equal(1, stats.Processing);
        Assert.Equal(1, stats.Delivered);
        Assert.Equal(1, stats.DeadLettered);
        Assert.NotNull(stats.OldestDueAge);
        Assert.InRange(stats.OldestDueAge.Value, TimeSpan.Zero, TimeSpan.FromMinutes(5));

        await ExpireAsync(Schema, claimed[0].Id); // the processing "old" message's lease expires: it is due again
        var later = await Admin.GetStatisticsAsync(Ct);
        Assert.NotNull(later.OldestDueAge);
    }

    [Fact]
    public async Task Requeue_only_affects_dead_lettered_messages_and_resets_attempts()
    {
        RequireDatabase();
        var dead = await EnqueueStandaloneAsync(new TestMessage("dead"));
        var pending = await EnqueueStandaloneAsync(new TestMessage("pending"), new EnqueueOptions { Delay = TimeSpan.FromHours(1) });
        var token = Guid.NewGuid();
        Assert.Single(await ClaimAsync(token: token));
        Assert.True(await Store.MarkDeadLetteredAsync(dead, token, "poison", Ct));

        Assert.False(await Admin.RequeueAsync(pending, Ct));
        Assert.False(await Admin.RequeueAsync(Guid.NewGuid(), Ct));
        Assert.True(await Admin.RequeueAsync(dead, Ct));
        Assert.False(await Admin.RequeueAsync(dead, Ct)); // already requeued

        var stored = await Admin.GetAsync(dead, Ct);
        Assert.Equal(OutboxMessageStatus.Pending, stored!.Status);
        Assert.Equal(0, stored.AttemptCount);
        Assert.Null(stored.DeadLetteredAt);

        var reclaimed = Assert.Single(await ClaimAsync());
        Assert.Equal(dead, reclaimed.Id);
        Assert.Equal(1, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task RequeueDeadLettered_respects_type_filter_and_max_count()
    {
        RequireDatabase();
        for (var i = 0; i < 3; i++)
        {
            await EnqueueStandaloneAsync(new TestMessage($"d{i}"));
        }

        await EnqueueStandaloneAsync(new OtherMessage(7));
        var token = Guid.NewGuid();
        foreach (var message in await ClaimAsync(token: token))
        {
            Assert.True(await Store.MarkDeadLetteredAsync(message.Id, token, "dead", Ct));
        }

        Assert.Equal(2, await Admin.RequeueDeadLetteredAsync("conformance.test", 2, Ct));
        Assert.Equal(1, await Admin.RequeueDeadLetteredAsync("conformance.test", 10, Ct));
        Assert.Equal(0, await Admin.RequeueDeadLetteredAsync("conformance.test", 10, Ct));
        Assert.Equal(1, await Admin.RequeueDeadLetteredAsync(null, 10, Ct));

        var stats = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(0, stats.DeadLettered);
        Assert.Equal(4, stats.Pending);
    }

    [Fact]
    public async Task DeleteDelivered_respects_retention_and_status()
    {
        RequireDatabase();
        var token = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
        {
            await EnqueueStandaloneAsync(new TestMessage($"r{i}"));
        }

        var claimed = await ClaimAsync(batchSize: 4, token: token);
        foreach (var message in claimed.Take(3))
        {
            Assert.True(await Store.MarkDeliveredAsync(message.Id, token, Ct));
        }

        Assert.Equal(0, await Store.DeleteDeliveredAsync(TimeSpan.FromHours(1), 100, Ct)); // too recent
        Assert.Equal(2, await Store.DeleteDeliveredAsync(TimeSpan.Zero, 2, Ct));             // max count
        Assert.Equal(1, await Admin.PurgeDeliveredAsync(TimeSpan.Zero, Ct));
        Assert.Equal(0, await Admin.PurgeDeliveredAsync(TimeSpan.Zero, Ct));

        var stats = await Admin.GetStatisticsAsync(Ct);
        Assert.Equal(0, stats.Delivered);
        Assert.Equal(1, stats.Processing);
        Assert.Equal(1, stats.Pending);
    }
}
