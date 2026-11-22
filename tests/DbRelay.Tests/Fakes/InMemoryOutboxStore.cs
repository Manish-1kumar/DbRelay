using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using DbRelay.Storage;

namespace DbRelay.Tests.Fakes;

/// <summary>
/// An in-memory <see cref="IOutboxStore"/> following the SPI semantics (lock-token guards, lease visibility), with failure injection.
/// Used to unit-test the dispatcher without a database. It does not model transactions.
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore, IOutboxBatchStore
{
    /// <summary>Number of batched "delivered" calls and their sizes (ADR 0003), for asserting that batching happens.</summary>
    public ConcurrentQueue<int> DeliveredBatchSizes { get; } = new();

    /// <summary>When set, batched releases throw (to test the dispatcher's per-message fallback).</summary>
    public bool FailBatchRelease { get; set; }

    public int BatchReleaseCalls => Volatile.Read(ref _batchReleaseCalls);

    private int _batchReleaseCalls;

    /// <summary>Batched operations reuse the single-item path, so every failure-injection hook applies to them too.</summary>
    public async Task<IReadOnlyCollection<OutboxAcknowledgement>> MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken)
    {
        DeliveredBatchSizes.Enqueue(items.Count);
        var updated = new HashSet<OutboxAcknowledgement>();
        foreach (var item in items)
        {
            if (await MarkDeliveredCoreAsync(item.MessageId, item.LockToken))
            {
                updated.Add(item);
            }
        }

        return updated;
    }

    public async Task<IReadOnlyCollection<OutboxAcknowledgement>> ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _batchReleaseCalls);
        if (FailBatchRelease)
        {
            throw new TimeoutException("simulated batch release failure");
        }

        var released = new HashSet<OutboxAcknowledgement>();
        foreach (var item in items)
        {
            if (await ReleaseAsync(item.MessageId, item.LockToken, cancellationToken))
            {
                released.Add(item);
            }
        }

        return released;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Row> _rows = [];
    private readonly TimeProvider _time;
    private int _claimCalls;

    public InMemoryOutboxStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Throws from ClaimAsync while it returns an exception.</summary>
    public Func<int, Exception?>? ClaimFailure { get; set; }

    /// <summary>Throws from outcome updates while it returns an exception.</summary>
    public Func<Guid, Exception?>? UpdateFailure { get; set; }

    /// <summary>Simulates another worker stealing the lease right before an outcome update.</summary>
    public bool StealLeaseBeforeUpdate { get; set; }

    /// <summary>The next successful outcome update is applied and then reported as a timeout (an ambiguous commit).</summary>
    public bool CommitThenFailOnce { get; set; }

    /// <summary>With <see cref="CommitThenFailOnce"/>: also increments the attempt count, as a reclaim would, before failing.</summary>
    public bool BumpAttemptAfterAmbiguousCommit { get; set; }

    private readonly HashSet<Guid> _unreadable = [];

    /// <summary>Makes claims return an <see cref="UnreadableRow"/> placeholder for this row, as a provider does for an unmappable row.</summary>
    public void MarkUnreadable(Guid id)
    {
        lock (_gate)
        {
            _unreadable.Add(id);
        }
    }

    public int ClaimCalls => Volatile.Read(ref _claimCalls);

    private List<(Guid Id, string Operation)> Operations { get; } = [];

    public bool HasOperation(Guid? id, string operation)
    {
        lock (_gate)
        {
            return Operations.Exists(o => (id is null || o.Id == id) && o.Operation == operation);
        }
    }

    public void ValidateCallerResources(DbConnection connection, DbTransaction? transaction)
    {
    }

    public Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken) =>
        InsertStandaloneAsync(records, cancellationToken);

    public Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var record in records)
            {
                if (_rows.ContainsKey(record.Id))
                {
                    throw new InvalidOperationException("duplicate id");
                }

                var available = record.DeliverAt ?? now + (record.Delay ?? TimeSpan.Zero);
                _rows[record.Id] = new Row(record, now, available);
            }
        }

        return Task.CompletedTask;
    }

    public Guid Add(string text = "payload", int attemptCount = 0, string messageType = "test.message")
    {
        var record = new OutboxRecord
        {
            Id = Guid.NewGuid(),
            MessageType = messageType,
            Payload = System.Text.Encoding.UTF8.GetBytes(text),
            ContentType = "text/plain",
        };
        InsertStandaloneAsync([record], CancellationToken.None).GetAwaiter().GetResult();
        lock (_gate)
        {
            _rows[record.Id].AttemptCount = attemptCount;
        }

        return record.Id;
    }

    public Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _claimCalls);
        if (ClaimFailure?.Invoke(call) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var claimed = _rows.Values
                .Where(r => r.Status is OutboxMessageStatus.Pending or OutboxMessageStatus.Processing && r.AvailableAt <= now)
                .OrderBy(r => r.AvailableAt)
                .Take(request.BatchSize)
                .ToList();
            foreach (var row in claimed)
            {
                row.Status = OutboxMessageStatus.Processing;
                row.LockToken = request.LockToken;
                row.LockedBy = request.WorkerId;
                row.AttemptCount++;
                row.LastAttemptAt = now;
                row.AvailableAt = now + request.LeaseDuration;
            }

            return Task.FromResult<IReadOnlyList<OutboxMessageDetails>>(claimed
                .Select(r => _unreadable.Contains(r.Record.Id)
                    ? UnreadableRow.Create(r.Record.Id, r.Status, r.AttemptCount, new InvalidOperationException("simulated mapping failure"))
                    : r.ToDetails())
                .ToList());
        }
    }

    /// <summary>Awaited before every single-row "delivered" update, to hold a flush open in tests.</summary>
    public Func<Task>? BeforeSingleDelivered { get; set; }

    public async Task<bool> MarkDeliveredAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken)
    {
        if (BeforeSingleDelivered is { } hook)
        {
            await hook();
        }

        return await MarkDeliveredCoreAsync(messageId, lockToken);
    }

    private Task<bool> MarkDeliveredCoreAsync(Guid messageId, Guid lockToken) =>
        Update(messageId, lockToken, "delivered", (row, now) =>
        {
            row.Status = OutboxMessageStatus.Delivered;
            row.DeliveredAt = now;
        });

    public Task<bool> ScheduleRetryAsync(Guid messageId, Guid lockToken, TimeSpan delay, string error, CancellationToken cancellationToken) =>
        Update(messageId, lockToken, "retry", (row, now) =>
        {
            row.Status = OutboxMessageStatus.Pending;
            row.AvailableAt = now + delay;
            row.LastError = error;
            row.LastDelay = delay;
        });

    public Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken) =>
        Update(messageId, lockToken, "dead-lettered", (row, now) =>
        {
            row.Status = OutboxMessageStatus.DeadLettered;
            row.DeadLetteredAt = now;
            row.LastError = error;
        });

    public Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        Update(messageId, lockToken, "released", (row, now) =>
        {
            row.Status = OutboxMessageStatus.Pending;
            row.AvailableAt = now;
            row.AttemptCount = Math.Max(row.AttemptCount - 1, 0);
        });

    public Task<int> DeleteDeliveredAsync(TimeSpan olderThan, int maxCount, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var cutoff = _time.GetUtcNow() - olderThan;
            var victims = _rows.Values.Where(r => r.Status == OutboxMessageStatus.Delivered && r.DeliveredAt < cutoff).Take(maxCount).ToList();
            foreach (var row in victims)
            {
                _rows.Remove(row.Record.Id);
            }

            return Task.FromResult(victims.Count);
        }
    }

    public Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(messageId, out var row) ? row.ToDetails() : null);
        }
    }

    public OutboxMessageDetails Get(Guid messageId) => GetAsync(messageId, CancellationToken.None).GetAwaiter().GetResult()!;

    public TimeSpan? LastDelay(Guid messageId)
    {
        lock (_gate)
        {
            return _rows[messageId].LastDelay;
        }
    }

    public Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IEnumerable<Row> rows = _rows.Values.OrderBy(r => r.CreatedAt);
            if (query.Status is { } status)
            {
                rows = rows.Where(r => r.Status == status);
            }

            return Task.FromResult<IReadOnlyList<OutboxMessageDetails>>(rows.Skip(query.Skip).Take(query.Take).Select(r => r.ToDetails()).ToList());
        }
    }

    public Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var due = _rows.Values.Where(r => r.Status is OutboxMessageStatus.Pending or OutboxMessageStatus.Processing && r.AvailableAt <= now).ToList();
            return Task.FromResult(new OutboxStatistics
            {
                Pending = _rows.Values.Count(r => r.Status == OutboxMessageStatus.Pending),
                Processing = _rows.Values.Count(r => r.Status == OutboxMessageStatus.Processing),
                Delivered = _rows.Values.Count(r => r.Status == OutboxMessageStatus.Delivered),
                DeadLettered = _rows.Values.Count(r => r.Status == OutboxMessageStatus.DeadLettered),
                OldestDueAge = due.Count == 0 ? null : now - due.Min(r => r.AvailableAt),
            });
        }
    }

    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(messageId, out var row) || row.Status != OutboxMessageStatus.DeadLettered)
            {
                return Task.FromResult(false);
            }

            row.Status = OutboxMessageStatus.Pending;
            row.AttemptCount = 0;
            row.AvailableAt = _time.GetUtcNow();
            row.DeadLetteredAt = null;
            return Task.FromResult(true);
        }
    }

    public Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task CheckHealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Dictionary<OutboxMessageStatus, int> CountByStatus()
    {
        lock (_gate)
        {
            return _rows.Values.GroupBy(r => r.Status).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    private Task<bool> Update(Guid messageId, Guid lockToken, string operation, Action<Row, DateTimeOffset> apply)
    {
        if (UpdateFailure?.Invoke(messageId) is { } failure)
        {
            throw failure;
        }

        lock (_gate)
        {
            if (StealLeaseBeforeUpdate && _rows.TryGetValue(messageId, out var stolen))
            {
                stolen.LockToken = Guid.NewGuid();
            }

            if (!_rows.TryGetValue(messageId, out var row) || row.LockToken != lockToken)
            {
                Operations.Add((messageId, operation + ":rejected"));
                return Task.FromResult(false);
            }

            apply(row, _time.GetUtcNow());
            row.LockToken = null;
            if (operation == "released")
            {
                row.LockedBy = null; // parity rule: only Release clears locked_by; completed rows remember their last claimer
            }

            Operations.Add((messageId, operation));
            if (CommitThenFailOnce)
            {
                CommitThenFailOnce = false;
                if (BumpAttemptAfterAmbiguousCommit)
                {
                    row.AttemptCount++;
                }

                throw new TimeoutException("simulated: the update committed but the acknowledgement was lost");
            }

            return Task.FromResult(true);
        }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated via collection.")]
    private sealed class Row(OutboxRecord record, DateTimeOffset createdAt, DateTimeOffset availableAt)
    {
        public OutboxRecord Record { get; } = record;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public DateTimeOffset AvailableAt { get; set; } = availableAt;

        public OutboxMessageStatus Status { get; set; }

        public int AttemptCount { get; set; }

        public Guid? LockToken { get; set; }

        public string? LockedBy { get; set; }

        public DateTimeOffset? LastAttemptAt { get; set; }

        public DateTimeOffset? DeliveredAt { get; set; }

        public DateTimeOffset? DeadLetteredAt { get; set; }

        public string? LastError { get; set; }

        public TimeSpan? LastDelay { get; set; }

        public OutboxMessageDetails ToDetails() => new()
        {
            Id = Record.Id,
            MessageType = Record.MessageType,
            Destination = Record.Destination,
            Payload = Record.Payload,
            ContentType = Record.ContentType,
            Headers = OutboxHeaders.Deserialize(Record.HeadersJson),
            CorrelationId = Record.CorrelationId,
            CausationId = Record.CausationId,
            Status = Status,
            AttemptCount = AttemptCount,
            CreatedAt = CreatedAt,
            ScheduledAt = Record.DeliverAt ?? CreatedAt + (Record.Delay ?? TimeSpan.Zero),
            AvailableAt = AvailableAt,
            LastAttemptAt = LastAttemptAt,
            DeliveredAt = DeliveredAt,
            DeadLetteredAt = DeadLetteredAt,
            LastError = LastError,
            LockedBy = LockedBy,
        };
    }
}

/// <summary>An open connection/transaction pair for validation tests.</summary>
public sealed class FakeConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Open;

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

    public override string Database => "fake";

    public override string DataSource => "fake";

    public override string ServerVersion => "0";

    public override ConnectionState State => _state;

    public bool WasDisposed { get; private set; }

    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

    public override void Close() => _state = ConnectionState.Closed;

    public override void Open() => _state = ConnectionState.Open;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);

    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        WasDisposed = true;
        base.Dispose(disposing);
    }
}

public sealed class FakeTransaction(FakeConnection connection) : DbTransaction
{
    private FakeConnection? _connection = connection;

    public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

    protected override DbConnection? DbConnection => _connection;

    public bool Committed { get; private set; }

    public bool RolledBack { get; private set; }

    public override void Commit()
    {
        Committed = true;
        _connection = null;
    }

    public override void Rollback()
    {
        RolledBack = true;
        _connection = null;
    }
}
