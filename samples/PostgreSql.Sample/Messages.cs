using DbRelay;

namespace PostgreSql.Sample;

/// <summary>The request body of <c>POST /orders</c>.</summary>
public sealed record CreateOrderRequest(string Customer, decimal Total);

/// <summary>
/// The event written to the outbox when an order is created. The explicit name is stored with the message and stays stable
/// if the class is renamed or moved.
/// </summary>
[OutboxMessageType("orders.created")]
public sealed record OrderCreated(Guid OrderId, string Customer, decimal Total);
