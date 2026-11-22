using System.Data;

namespace DbRelay;

/// <summary>
/// Writes messages to the transactional outbox.
/// </summary>
/// <remarks>
/// <para>
/// The atomic methods take the caller's open connection and, normally, the caller's active transaction.
/// The outbox row is inserted with exactly that connection and transaction, so it commits or rolls back
/// together with the caller's business data.
/// </para>
/// <para>
/// <b>Resource ownership:</b> DbRelay never opens, closes, commits, rolls back or disposes a connection
/// or transaction passed to it. The caller remains responsible for them.
/// </para>
/// <para>
/// Most callers use the convenience overloads in <see cref="OutboxExtensions"/>, such as
/// <c>outbox.EnqueueAsync(transaction, message)</c>, which take the connection from the transaction.
/// </para>
/// </remarks>
public interface IOutbox
{
    /// <summary>
    /// Serializes <paramref name="message"/> and inserts it into the outbox using the caller's connection and transaction.
    /// </summary>
    /// <typeparam name="TMessage">The message type. The runtime type of <paramref name="message"/> is serialized.</typeparam>
    /// <param name="connection">An open connection owned by the caller. It must be the provider's connection type (for example <c>NpgsqlConnection</c>).</param>
    /// <param name="transaction">
    /// The caller's active transaction on <paramref name="connection"/>. Pass <see langword="null"/> only when the connection has
    /// <b>no</b> transaction in progress: the insert then auto-commits on its own and is <b>not</b> atomic with other work.
    /// If a transaction is in progress, always pass it. Passing <see langword="null"/> then is provider-specific: PostgreSQL runs the
    /// insert inside that transaction anyway, while SQL Server rejects the command.
    /// </param>
    /// <param name="message">The message to enqueue. Must not be <see langword="null"/>.</param>
    /// <param name="options">Optional metadata such as correlation ID, destination or scheduled delivery time.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID. Consumers can use it as an idempotency key because it stays the same across redeliveries.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> or <paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The connection or transaction is not of the configured provider's type, the transaction belongs to a different connection, or <paramref name="options"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">The connection is not open, or the transaction has already completed.</exception>
    Task<Guid> EnqueueAsync<TMessage>(
        IDbConnection connection,
        IDbTransaction? transaction,
        TMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serializes and inserts several messages in a single round trip using the caller's connection and transaction.
    /// </summary>
    /// <param name="connection">An open connection owned by the caller.</param>
    /// <param name="transaction">The caller's active transaction on <paramref name="connection"/>, or <see langword="null"/> to auto-commit.</param>
    /// <param name="messages">The messages to enqueue (1 to 1000). <see langword="null"/> elements are not allowed.</param>
    /// <param name="options">Metadata applied to every message. <see cref="EnqueueOptions.MessageId"/> must be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The generated message IDs, in the same order as <paramref name="messages"/>.</returns>
    Task<IReadOnlyList<Guid>> EnqueueManyAsync<TMessage>(
        IDbConnection connection,
        IDbTransaction? transaction,
        IEnumerable<TMessage> messages,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a pre-serialized payload (for example protobuf or Avro, or JSON you serialized yourself) without using the
    /// configured serializer. The bytes are stored and delivered unchanged.
    /// </summary>
    /// <param name="connection">An open connection owned by the caller.</param>
    /// <param name="transaction">The caller's active transaction on <paramref name="connection"/>, or <see langword="null"/> to auto-commit.</param>
    /// <param name="message">The raw message.</param>
    /// <param name="options">Optional metadata.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID.</returns>
    Task<Guid> EnqueueRawAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        RawOutboxMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a message using a connection that DbRelay opens and commits itself.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> atomic with any other database work. Use it when there is no business transaction and you only
    /// want durable delivery with retries. To make a message atomic with your own writes, use
    /// <see cref="EnqueueAsync{TMessage}(IDbConnection, IDbTransaction?, TMessage, EnqueueOptions?, CancellationToken)"/>.
    /// </remarks>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="options">Optional metadata.</param>
    /// <param name="cancellationToken">Cancels the insert.</param>
    /// <returns>The message ID.</returns>
    Task<Guid> EnqueueStandaloneAsync<TMessage>(
        TMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default);
}
