using DbRelay;

namespace PostgreSql.Sample;

/// <summary>
/// A delivery handler that only logs. Replace the body with a call to your broker, webhook or search index.
/// </summary>
/// <remarks>
/// DbRelay resolves the handler from a new DI scope for every delivery and holds no database transaction while it runs.
/// Delivery is at-least-once: the same <see cref="OutboxMessage.Id"/> can arrive more than once, so receivers must be idempotent.
/// </remarks>
internal sealed class LoggingDeliveryHandler(ILogger<LoggingDeliveryHandler> logger) : IMessageDeliveryHandler
{
    public Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // Log identifiers and sizes, never the payload: it can contain personal or secret data.
        logger.LogInformation(
            "Delivering message {MessageId} ({MessageType}), attempt {Attempt}, {PayloadLength} payload bytes",
            message.Id,
            message.MessageType,
            message.Attempt,
            message.Payload.Length);

        return Task.FromResult(DeliveryResult.Delivered());
    }
}
