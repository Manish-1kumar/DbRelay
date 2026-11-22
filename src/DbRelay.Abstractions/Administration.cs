namespace DbRelay;

/// <summary>The lifecycle status of an outbox message.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting for delivery (possibly scheduled or waiting for a retry).</summary>
    Pending = 0,

    /// <summary>Claimed by a worker that holds a lease. If the lease expires, the message becomes claimable again.</summary>
    Processing = 1,

    /// <summary>Delivered successfully.</summary>
    Delivered = 2,

    /// <summary>Failed permanently or exhausted its attempts. It is kept until it is requeued.</summary>
    DeadLettered = 3,
}

/// <summary>
/// Inspection and recovery operations for operators. These operations do not participate in caller transactions.
/// </summary>
public interface IOutboxAdmin
{
    /// <summary>Gets a message by ID.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The message, or <see langword="null"/> if it does not exist (or has been purged).</returns>
    Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Lists messages matching <paramref name="query"/>, ordered by creation time.</summary>
    /// <param name="query">Filters and paging.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching messages.</returns>
    Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken = default);

    /// <summary>Gets message counts by status and the age of the oldest due message.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The statistics.</returns>
    Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a dead-lettered message back to <see cref="OutboxMessageStatus.Pending"/> with its attempt count reset, so it is
    /// delivered again. Messages in any other status are not changed.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> if the message was dead-lettered and has been requeued.</returns>
    Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>Requeues up to <paramref name="maxCount"/> dead-lettered messages, oldest first.</summary>
    /// <param name="messageType">Only requeue messages of this type, or <see langword="null"/> for all types.</param>
    /// <param name="maxCount">The maximum number of messages to requeue (1 to 10000).</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of messages requeued.</returns>
    Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken = default);

    /// <summary>Deletes delivered messages that were delivered more than <paramref name="olderThan"/> ago (database clock).</summary>
    /// <param name="olderThan">The minimum age since delivery.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The number of messages deleted.</returns>
    Task<int> PurgeDeliveredAsync(TimeSpan olderThan, CancellationToken cancellationToken = default);
}

/// <summary>Filters for <see cref="IOutboxAdmin.QueryAsync"/>.</summary>
public sealed class OutboxQuery
{
    /// <summary>Only messages with this status.</summary>
    public OutboxMessageStatus? Status { get; init; }

    /// <summary>Only messages of this type.</summary>
    public string? MessageType { get; init; }

    /// <summary>Only messages created at or after this time.</summary>
    public DateTimeOffset? CreatedAfter { get; init; }

    /// <summary>Only messages created before this time.</summary>
    public DateTimeOffset? CreatedBefore { get; init; }

    /// <summary>
    /// The number of matching messages to skip (0 to 10,000). Results are ordered by creation time, which has no dedicated index,
    /// so a page costs a scan of the matching rows. Filter by <see cref="Status"/> (Pending, Processing or DeadLettered use
    /// partial indexes), <see cref="MessageType"/> or a creation-time range rather than paging deep into a large table.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>The maximum number of messages to return (1 to 1000). Defaults to 100.</summary>
    public int Take { get; init; } = 100;
}

/// <summary>Full details of a stored outbox message.</summary>
public sealed class OutboxMessageDetails
{
    /// <summary>The message ID.</summary>
    public required Guid Id { get; init; }

    /// <summary>The logical message type name.</summary>
    public required string MessageType { get; init; }

    /// <summary>The routing hint, if any.</summary>
    public string? Destination { get; init; }

    /// <summary>The stored payload.</summary>
    public required ReadOnlyMemory<byte> Payload { get; init; }

    /// <summary>The payload's media type.</summary>
    public required string ContentType { get; init; }

    /// <summary>The stored headers.</summary>
    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    /// <summary>The correlation ID, if any.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>The causation ID, if any.</summary>
    public string? CausationId { get; init; }

    /// <summary>The current status.</summary>
    public required OutboxMessageStatus Status { get; init; }

    /// <summary>The number of delivery attempts started (counted when a worker claims the message).</summary>
    public required int AttemptCount { get; init; }

    /// <summary>When the message was inserted (UTC).</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The earliest delivery time requested at enqueue (UTC).</summary>
    public required DateTimeOffset ScheduledAt { get; init; }

    /// <summary>
    /// When the message next becomes claimable (UTC). For <see cref="OutboxMessageStatus.Processing"/> this is when the current lease expires.
    /// </summary>
    public required DateTimeOffset AvailableAt { get; init; }

    /// <summary>When the most recent attempt started (UTC).</summary>
    public DateTimeOffset? LastAttemptAt { get; init; }

    /// <summary>When the message was delivered (UTC).</summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>When the message was dead-lettered (UTC).</summary>
    public DateTimeOffset? DeadLetteredAt { get; init; }

    /// <summary>The most recent failure description (truncated to 2000 characters).</summary>
    public string? LastError { get; init; }

    /// <summary>The worker that most recently claimed the message. For diagnostics only.</summary>
    public string? LockedBy { get; init; }
}

/// <summary>Outbox counts by status.</summary>
public sealed class OutboxStatistics
{
    /// <summary>Messages waiting for delivery, including scheduled and retry-delayed messages.</summary>
    public required long Pending { get; init; }

    /// <summary>Messages currently claimed by a worker (including ones whose lease has expired but which have not been reclaimed yet).</summary>
    public required long Processing { get; init; }

    /// <summary>Delivered messages not yet purged.</summary>
    public required long Delivered { get; init; }

    /// <summary>Dead-lettered messages.</summary>
    public required long DeadLettered { get; init; }

    /// <summary>
    /// How long the oldest due message (pending or processing, and claimable now) has been waiting since it became due.
    /// <see langword="null"/> when nothing is due. A steadily growing value means delivery is falling behind or stalled.
    /// </summary>
    public TimeSpan? OldestDueAge { get; init; }
}

/// <summary>
/// Creates the outbox database objects.
/// </summary>
public interface IOutboxSchemaManager
{
    /// <summary>
    /// Creates the schema, table and indexes if they do not exist. This is idempotent and never drops or alters existing objects.
    /// The connecting user needs DDL permissions; if it does not have them, run <see cref="GetCreateScript"/> through your
    /// migration process instead.
    /// </summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task that completes when the objects exist.</returns>
    Task EnsureCreatedAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the idempotent DDL script for the configured schema and table names.</summary>
    /// <returns>The SQL script.</returns>
    string GetCreateScript();
}
