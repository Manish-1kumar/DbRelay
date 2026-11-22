namespace DbRelay.Storage;

/// <summary>
/// One message and the lock token of the claim that owns it, for batched outcome updates.
/// </summary>
/// <param name="MessageId">The message ID.</param>
/// <param name="LockToken">The lock token of the claim that owns the message.</param>
public readonly record struct OutboxAcknowledgement(Guid MessageId, Guid LockToken);

/// <summary>
/// An optional storage capability: guarded outcome updates for many messages in one round trip (ADR 0003).
/// A store that implements this interface in addition to <see cref="IOutboxStore"/> lets the dispatcher batch its
/// "delivered" acknowledgements and shutdown releases. Stores that don't are used one message at a time.
/// </summary>
/// <remarks>
/// Every row is still guarded by <b>its own</b> lock token: a row whose current lock token differs from the supplied one (the lease
/// was lost) must not change and must not appear in the result. Duplicate entries may be ignored. Implementations must verify
/// this behaviour with the storage conformance suite.
/// </remarks>
public interface IOutboxBatchStore
{
    /// <summary>
    /// Marks each message delivered (status Delivered, <c>delivered_at = now</c>, lock token cleared) if, and only if, it still holds
    /// the supplied lock token. Equivalent to calling <see cref="IOutboxStore.MarkDeliveredAsync"/> for each item.
    /// </summary>
    /// <param name="items">1 to <see cref="OutboxLimits.MaxClaimBatchSize"/> acknowledgements.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>
    /// The (message ID, lock token) pairs that were updated. Items not returned had lost their lease or did not exist. Pairs, not IDs,
    /// so that two items for the same message with different tokens (a stale and a current claim) can be told apart.
    /// </returns>
    Task<IReadOnlyCollection<OutboxAcknowledgement>> MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken);

    /// <summary>
    /// Releases each claimed message without counting the attempt (status Pending, <c>available_at = now</c>,
    /// <c>attempt_count = max(attempt_count - 1, 0)</c>, lock token and <c>locked_by</c> cleared) if, and only if, it still holds the
    /// supplied lock token. Equivalent to calling <see cref="IOutboxStore.ReleaseAsync"/> for each item.
    /// </summary>
    /// <param name="items">1 to <see cref="OutboxLimits.MaxClaimBatchSize"/> acknowledgements.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>The (message ID, lock token) pairs that were released.</returns>
    Task<IReadOnlyCollection<OutboxAcknowledgement>> ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken);
}
