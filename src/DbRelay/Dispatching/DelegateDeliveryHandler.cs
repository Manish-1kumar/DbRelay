namespace DbRelay.Dispatching;

/// <summary>Adapts a delegate registered with <c>UseDeliveryHandler(Func&lt;...&gt;)</c>.</summary>
internal sealed class DelegateDeliveryHandler : IMessageDeliveryHandler
{
    private readonly Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> _handler;

    public DelegateDeliveryHandler(Func<OutboxMessage, CancellationToken, Task<DeliveryResult>> handler)
    {
        _handler = handler;
    }

    public Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken) => _handler(message, cancellationToken);
}
