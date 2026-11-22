using DbRelay;

namespace SqlServer.Sample;

/// <summary>
/// Deserializes the payload into the type the handler expects and logs identifiers. Replace the body with your real delivery.
/// </summary>
/// <remarks>
/// <see cref="IMessageSerializer"/> is the serializer DbRelay used at enqueue time. The handler always names the target type;
/// DbRelay never creates a type from the stored message type name.
/// </remarks>
internal sealed class ConsoleDeliveryHandler(IMessageSerializer serializer, ILogger<ConsoleDeliveryHandler> logger) : IMessageDeliveryHandler
{
    public Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.MessageType != "orders.placed")
        {
            // Nothing else is enqueued by this sample; anything else can never succeed, so do not retry it.
            return Task.FromResult(DeliveryResult.DeadLetter($"Unknown message type '{message.MessageType}'."));
        }

        var order = serializer.Deserialize<OrderPlaced>(message.Payload.Span);

        // Log identifiers, not payload contents.
        logger.LogInformation(
            "Delivered message {MessageId} ({MessageType}) for order {OrderId}, attempt {Attempt}, correlation {CorrelationId}",
            message.Id,
            message.MessageType,
            order.OrderId,
            message.Attempt,
            message.CorrelationId);

        return Task.FromResult(DeliveryResult.Delivered());
    }
}
