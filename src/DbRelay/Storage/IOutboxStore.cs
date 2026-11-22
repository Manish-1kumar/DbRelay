using System.Data.Common;

namespace DbRelay.Storage;

/// <summary>
/// The storage provider contract (SPI) implemented by database providers such as DbRelay.PostgreSql.
/// Application code does not use this interface directly.
/// </summary>
/// <remarks>
/// Implementations must satisfy the behaviour verified by the DbRelay storage conformance test suite. In particular:
/// <list type="bullet">
/// <item><description>All time comparisons (availability, leases, retention) use the database clock.</description></item>
/// <item><description>Every operation that takes a lock token only changes the row if its current lock token equals that token, and returns <see langword="false"/> otherwise.</description></item>
/// <item><description>Claiming is atomic: under concurrent callers, a row is returned to at most one caller per lease.</description></item>
/// <item><description><see cref="InsertAsync"/> uses exactly the caller's connection and transaction and never commits, rolls back, opens, closes or disposes them.</description></item>
/// </list>
/// </remarks>
public interface IOutboxStore
{
    /// <summary>
    /// Verifies that the caller's connection and transaction are of this provider's types. Throws <see cref="ArgumentException"/> if not.
    /// General checks (open connection, transaction belongs to the connection) are performed by DbRelay before this is called.
    /// </summary>
    /// <param name="connection">The caller's connection.</param>
    /// <param name="transaction">The caller's transaction, if any.</param>
    void ValidateCallerResources(DbConnection connection, DbTransaction? transaction);

    /// <summary>Inserts records using the caller's connection and transaction.</summary>
    /// <param name="connection">The caller's open connection.</param>
    /// <param name="transaction">The caller's transaction, or <see langword="null"/>.</param>
    /// <param name="records">One or more validated records.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>A task that completes when the rows are inserted (but not committed).</returns>
    Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken);

    /// <summary>Inserts records on a provider-owned connection and commits them.</summary>
    /// <param name="records">One or more validated records.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims up to <see cref="ClaimRequest.BatchSize"/> due messages: rows with status Pending or Processing whose
    /// <c>available_at</c> is at or before the database's current time, oldest <c>available_at</c> first, skipping rows locked by
    /// concurrent claimers. Each claimed row gets status Processing, the request's lock token and worker ID,
    /// <c>attempt_count + 1</c>, <c>last_attempt_at = now</c> and <c>available_at = now + lease</c>.
    /// The batch is also limited by <see cref="ClaimRequest.MaxPayloadBytes"/>: in claim order, a message is included only if the
    /// payloads of the messages before it total less than that budget (so the first message is always included). Messages cut by
    /// the budget must be left untouched.
    /// </summary>
    /// <param name="request">The claim parameters.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    /// <returns>The claimed messages with their updated values.</returns>
    Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken);

    /// <summary>Marks a claimed message delivered: status Delivered, <c>delivered_at = now</c>, lock cleared.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="lockToken">The lock token from the claim.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="false"/> if the row no longer holds <paramref name="lockToken"/>.</returns>
    Task<bool> MarkDeliveredAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken);

    /// <summary>Schedules a retry: status Pending, <c>available_at = now + delay</c>, <c>last_error</c> set, lock cleared.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="lockToken">The lock token from the claim.</param>
    /// <param name="delay">The delay from the database's current time.</param>
    /// <param name="error">The failure description (already truncated).</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="false"/> if the row no longer holds <paramref name="lockToken"/>.</returns>
    Task<bool> ScheduleRetryAsync(Guid messageId, Guid lockToken, TimeSpan delay, string error, CancellationToken cancellationToken);

    /// <summary>Dead-letters a claimed message: status DeadLettered, <c>dead_lettered_at = now</c>, <c>last_error</c> set, lock cleared.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="lockToken">The lock token from the claim.</param>
    /// <param name="error">The failure description (already truncated).</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="false"/> if the row no longer holds <paramref name="lockToken"/>.</returns>
    Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a claimed but undelivered message to the queue without counting the attempt: status Pending,
    /// <c>available_at = now</c>, <c>attempt_count = max(attempt_count - 1, 0)</c>, lock cleared.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="lockToken">The lock token from the claim.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="false"/> if the row no longer holds <paramref name="lockToken"/>.</returns>
    Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes up to <paramref name="maxCount"/> delivered messages whose <c>delivered_at</c> is older than now minus
    /// <paramref name="olderThan"/>. Safe to run concurrently from several instances.
    /// </summary>
    /// <param name="olderThan">The retention period.</param>
    /// <param name="maxCount">The maximum number of rows to delete in this call.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    /// <returns>The number of rows deleted.</returns>
    Task<int> DeleteDeliveredAsync(TimeSpan olderThan, int maxCount, CancellationToken cancellationToken);

    /// <summary>Gets a message by ID.</summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The message, or <see langword="null"/>.</returns>
    Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Lists messages matching a validated query, ordered by <c>created_at</c>, then a stable provider-defined tiebreaker (insertion order or ID).</summary>
    /// <param name="query">The query (already validated).</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching messages.</returns>
    Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken);

    /// <summary>Computes counts by status and the age of the oldest due message.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The statistics.</returns>
    Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Requeues a dead-lettered message: status Pending, <c>available_at = now</c>, <c>attempt_count = 0</c>,
    /// <c>dead_lettered_at</c> cleared. Only changes rows whose status is DeadLettered.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns><see langword="true"/> if a row was requeued.</returns>
    Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken);

    /// <summary>Requeues up to <paramref name="maxCount"/> dead-lettered messages, oldest <c>dead_lettered_at</c> first.</summary>
    /// <param name="messageType">Optional message type filter.</param>
    /// <param name="maxCount">The maximum number of rows to requeue.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The number of rows requeued.</returns>
    Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken);

    /// <summary>Runs a trivial query to verify connectivity and that the outbox table exists.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that faults if the database is unreachable or the table is missing.</returns>
    Task CheckHealthAsync(CancellationToken cancellationToken);
}
