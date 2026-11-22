namespace DbRelay;

/// <summary>
/// Delivers outbox messages to an external system: a message broker, an HTTP endpoint, a search index, or anything else.
/// </summary>
/// <remarks>
/// <para>
/// A new dependency-injection scope is created for every delivery, so the handler can depend on scoped services.
/// </para>
/// <para>
/// Delivery is <b>at-least-once</b>: the same message (same <see cref="OutboxMessage.Id"/>) can be delivered more than
/// once, for example when a worker crashes after delivering but before recording success. Make the receiving side idempotent.
/// </para>
/// <para>
/// No database transaction is open while this method runs.
/// </para>
/// </remarks>
public interface IMessageDeliveryHandler
{
    /// <summary>
    /// Delivers <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The message to deliver.</param>
    /// <param name="cancellationToken">
    /// Cancelled when the delivery timeout elapses or the host is shutting down. Pass it to network calls.
    /// </param>
    /// <returns>
    /// <see cref="DeliveryResult.Delivered"/> on success, <see cref="DeliveryResult.Retry(string, TimeSpan?)"/> for a transient
    /// failure, or <see cref="DeliveryResult.DeadLetter(string)"/> for a permanent failure. A thrown exception is treated as a
    /// transient failure unless <c>RetryOptions.IsPermanentFailure</c> says otherwise.
    /// </returns>
    Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken);
}
