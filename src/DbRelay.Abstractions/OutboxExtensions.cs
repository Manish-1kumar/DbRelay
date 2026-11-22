using System.Data;

namespace DbRelay;

/// <summary>
/// Convenience overloads for <see cref="IOutbox"/> that take the connection from the caller's transaction.
/// </summary>
public static class OutboxExtensions
{
    /// <summary>
    /// Enqueues <paramref name="message"/> in the caller's transaction. The connection is taken from
    /// <see cref="IDbTransaction.Connection"/>. DbRelay never commits, rolls back or disposes the transaction.
    /// </summary>
    /// <param name="outbox">The outbox.</param>
    /// <param name="transaction">The caller's active transaction.</param>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID.</returns>
    /// <exception cref="InvalidOperationException">The transaction has already been committed or rolled back.</exception>
    public static Task<Guid> EnqueueAsync<TMessage>(
        this IOutbox outbox,
        IDbTransaction transaction,
        TMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return outbox.EnqueueAsync(GetConnection(transaction), transaction, message, null, cancellationToken);
    }

    /// <summary>
    /// Enqueues <paramref name="message"/> with <paramref name="options"/> in the caller's transaction.
    /// </summary>
    /// <param name="outbox">The outbox.</param>
    /// <param name="transaction">The caller's active transaction.</param>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="options">Message metadata.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID.</returns>
    public static Task<Guid> EnqueueAsync<TMessage>(
        this IOutbox outbox,
        IDbTransaction transaction,
        TMessage message,
        EnqueueOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return outbox.EnqueueAsync(GetConnection(transaction), transaction, message, options, cancellationToken);
    }

    /// <summary>
    /// Enqueues several messages in the caller's transaction in a single round trip.
    /// </summary>
    /// <param name="outbox">The outbox.</param>
    /// <param name="transaction">The caller's active transaction.</param>
    /// <param name="messages">The messages to enqueue.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message IDs, in input order.</returns>
    public static Task<IReadOnlyList<Guid>> EnqueueManyAsync<TMessage>(
        this IOutbox outbox,
        IDbTransaction transaction,
        IEnumerable<TMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return outbox.EnqueueManyAsync(GetConnection(transaction), transaction, messages, null, cancellationToken);
    }

    /// <summary>
    /// Enqueues a pre-serialized payload in the caller's transaction.
    /// </summary>
    /// <param name="outbox">The outbox.</param>
    /// <param name="transaction">The caller's active transaction.</param>
    /// <param name="message">The raw message.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID.</returns>
    public static Task<Guid> EnqueueRawAsync(
        this IOutbox outbox,
        IDbTransaction transaction,
        RawOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbox);
        return outbox.EnqueueRawAsync(GetConnection(transaction), transaction, message, null, cancellationToken);
    }

    private static IDbConnection GetConnection(IDbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection
            ?? throw new InvalidOperationException(
                "The transaction has already been committed or rolled back (its Connection is null). " +
                "Enqueue outbox messages before committing the transaction.");
    }
}
