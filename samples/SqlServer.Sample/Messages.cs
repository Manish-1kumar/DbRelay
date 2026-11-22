using DbRelay;

namespace SqlServer.Sample;

/// <summary>The event written to the outbox when an order is placed.</summary>
[OutboxMessageType("orders.placed")]
public sealed record OrderPlaced(Guid OrderId, string Customer, decimal Total);
