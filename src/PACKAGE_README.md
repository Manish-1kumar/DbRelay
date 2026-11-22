# DbRelay

**A transactional outbox for .NET teams that already own their SQL.** Enqueue a message on the `DbTransaction` you already have
(Dapper or plain ADO.NET). A background dispatcher delivers it to one interface you implement, **at least once**, with
database-clock leases, lock-token fencing, retries with jitter and dead-lettering.

- Packages: `DbRelay.Abstractions` (contracts, no dependencies), `DbRelay` (engine), `DbRelay.PostgreSql` (provider).
- Targets .NET 8, 9, 10 and 11. No EF Core, no broker or messaging framework required.

## Quick start (PostgreSQL)

```csharp
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), pg => pg.Schema = Schema);
    relay.UseDeliveryHandler<LoggingDeliveryHandler>();
});

// Explicit, idempotent schema creation (or run IOutboxSchemaManager.GetCreateScript() through your migration tool).
await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
```

Enqueue in **your** transaction. DbRelay never commits, rolls back or disposes it:

```csharp
await using var connection = await dataSource.OpenConnectionAsync(ct);
await using var transaction = await connection.BeginTransactionAsync(ct);

await connection.ExecuteAsync(
    "INSERT INTO sample.orders (id, customer, total) VALUES (@Id, @Customer, @Total)", order, transaction);

var messageId = await outbox.EnqueueAsync(transaction, new OrderCreated(order.Id, order.Customer, order.Total), ct);

await transaction.CommitAsync(ct); // the order and the message commit together, or neither does
```

Implement the delivery target:

```csharp
internal sealed class LoggingDeliveryHandler(ILogger<LoggingDeliveryHandler> logger) : IMessageDeliveryHandler
{
    public Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation("Delivering message {MessageId} ({MessageType}), attempt {Attempt}",
            message.Id, message.MessageType, message.Attempt);
        return Task.FromResult(DeliveryResult.Delivered());
    }
}
```

Return `DeliveryResult.Retry(reason, retryAfter)` for transient failures and `DeliveryResult.DeadLetter(reason)` for permanent
ones. Use `OutboxMessage.Id` as the idempotency key on the receiving side: delivery is at-least-once.

## Documentation

- [README and full documentation](https://github.com/Manish-1kumar/DbRelay#readme)
- [Getting started](https://github.com/Manish-1kumar/DbRelay/blob/main/docs/getting-started.md)
- [Configuration reference](https://github.com/Manish-1kumar/DbRelay/blob/main/docs/configuration.md)
- [Reliability guarantees and the tests that prove them](https://github.com/Manish-1kumar/DbRelay/blob/main/docs/reliability.md)
- [PostgreSQL guide](https://github.com/Manish-1kumar/DbRelay/blob/main/docs/postgres.md)
- [Troubleshooting](https://github.com/Manish-1kumar/DbRelay/blob/main/docs/troubleshooting.md)
- [Changelog](https://github.com/Manish-1kumar/DbRelay/blob/main/CHANGELOG.md)

Licensed under MIT.
