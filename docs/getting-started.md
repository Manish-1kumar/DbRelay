# Getting started

This guide takes you from an empty ASP.NET Core project to a delivered message on PostgreSQL. The code is from
[`samples/PostgreSql.Sample`](../samples/PostgreSql.Sample), which builds and runs as shown here. SQL Server is identical
apart from the registration call; see [the SQL Server section](#sql-server).

## 1. Prerequisites

- .NET 8 or later (the samples target `net10.0`).
- PostgreSQL 15 or later. Tests and samples ran on 17.5.
- A database user that can create a schema, or a DBA who runs the script for you (see [postgres.md](postgres.md)).

## 2. Add DbRelay

Add the provider package, which brings in `DbRelay` and `DbRelay.Abstractions`:

```shell
dotnet add package DbRelay.PostgreSql --version 0.2.0
```

For SQL Server, add `DbRelay.SqlServer` instead (see [SQL Server](#sql-server)):
`dotnet add package DbRelay.SqlServer --version 0.2.0`.

The samples also use Dapper for business writes. DbRelay itself does not depend on Dapper: it works with any code that holds
an `NpgsqlTransaction` or `SqlTransaction`.

## 3. Define a message

Any type that System.Text.Json can serialize. Give it a stable logical name. Without one, the full CLR name is stored, and
it changes when you rename or move the class.

```csharp
[OutboxMessageType("orders.created")]
public sealed record OrderCreated(Guid OrderId, string Customer, decimal Total);
```

You can also map names at registration with `relay.MapMessageType<OrderCreated>("orders.created")`. The mapping takes
precedence over the attribute. The name is only a label: DbRelay never creates a type from it.

## 4. Register DbRelay

```csharp
// One NpgsqlDataSource for business writes and for DbRelay. The DI container owns it; DbRelay never disposes it.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), pg => pg.Schema = Schema);
    relay.UseDeliveryHandler<LoggingDeliveryHandler>();
    relay.ConfigureDispatcher(d => d.PollingInterval = TimeSpan.FromMilliseconds(500));
});

// Unhealthy when the database is unreachable; Degraded when the dispatcher stalls or the backlog is too old.
builder.Services.AddHealthChecks().AddDbRelay(configure: health => health.MaxOldestDueAge = TimeSpan.FromMinutes(5));
```

`UsePostgreSql` has three overloads:

| Overload | Who owns the data source |
|---|---|
| `UsePostgreSql(string connectionString, ...)` | DbRelay builds it and disposes it with the service provider |
| `UsePostgreSql(NpgsqlDataSource dataSource, ...)` | You do; DbRelay never disposes it |
| `UsePostgreSql(Func<IServiceProvider, NpgsqlDataSource> factory, ...)` | You (or your container) do; DbRelay never disposes it |

`AddDbRelay` throws at registration if no storage provider is configured, or if no delivery handler is configured while the
dispatcher is enabled. Options are validated when the host starts (see [configuration.md](configuration.md)).

## 5. Create the outbox table

```csharp
// Explicit, idempotent schema creation. It never drops or alters anything. If the application's database user may not run
// DDL, run IOutboxSchemaManager.GetCreateScript() through your migration tool instead.
await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
```

DbRelay never creates tables on its own. You call this, or you run the script yourself.

## 6. Enqueue inside your transaction

```csharp
app.MapPost("/orders", async (CreateOrderRequest request, NpgsqlDataSource dataSource, IOutbox outbox, CancellationToken ct) =>
{
    var order = new { Id = Guid.NewGuid(), request.Customer, request.Total };

    // The application owns the connection and the transaction: it opens, commits and disposes them.
    // DbRelay only inserts the outbox row with this connection, inside this transaction. It never commits,
    // rolls back or disposes either of them.
    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);

    await connection.ExecuteAsync(
        "INSERT INTO sample.orders (id, customer, total) VALUES (@Id, @Customer, @Total)", order, transaction);

    var messageId = await outbox.EnqueueAsync(transaction, new OrderCreated(order.Id, order.Customer, order.Total), ct);

    // The order and the message commit together, or neither does. If this line is never reached,
    // disposing the transaction rolls both back and nothing is delivered.
    await transaction.CommitAsync(ct);

    return Results.Created($"/orders/{order.Id}", new { orderId = order.Id, messageId });
});
```

`outbox.EnqueueAsync(transaction, message)` takes the connection from `transaction.Connection`. The resource rules:

- You open the connection, begin, commit or roll back, and dispose. DbRelay does none of these.
- The connection must be open and an `NpgsqlConnection`. The transaction must be active and belong to it.
- Enqueue **before** committing. A completed transaction is rejected with `InvalidOperationException`.

## 7. Deliver

```csharp
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
```

Replace the body with your broker, webhook or index call, and return `Delivered`, `Retry` or `DeadLetter`.
[`samples/CustomHandler.Sample`](../samples/CustomHandler.Sample/WebhookDeliveryHandler.cs) is a complete HTTP webhook handler.
Remember that delivery is **at-least-once**: send `message.Id` to the receiver so it can drop duplicates.

## 8. Run it

```powershell
$env:ConnectionStrings__Outbox = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"
dotnet run --project samples/PostgreSql.Sample -c Release
Invoke-RestMethod -Method Post http://localhost:5070/orders -ContentType application/json -Body '{"customer":"Ada","total":42.50}'
```

Output of a real run (the machine name in the worker ID is replaced with `<machine>`):

```text
info: DbRelay.Dispatching.OutboxDispatcher[1000]
      DbRelay dispatcher <machine>:17196:28887047c979493c99db18b5a5e0277a started (batch size 100, max concurrency 8, lease 00:02:00).
info: PostgreSql.Sample.LoggingDeliveryHandler[0]
      Delivering message 01a1fee6-a98c-78e6-8ae6-3aa792043e85 (orders.created), attempt 1, 81 payload bytes
```

Check the result through `IOutboxAdmin`:

```text
GET /outbox/messages/01a1fee6-a98c-78e6-8ae6-3aa792043e85
{"id":"01a1fee6-...","messageType":"orders.created","status":"Delivered","attemptCount":1,...,"lastError":null}
GET /outbox/stats
{"pending":0,"processing":0,"delivered":3,"deadLettered":0,"oldestDueAge":null}
```

## More ways to enqueue

All of these take your transaction. These snippets are compile-checked against the current source.

```csharp
// Metadata: correlation, a routing hint for the handler, custom headers, and a delay measured by the database clock.
await outbox.EnqueueAsync(transaction, new OrderCreated(orderId, "Ada", 42.50m), new EnqueueOptions
{
    CorrelationId = correlationId,
    Destination = "orders-topic",
    Headers = new Dictionary<string, string> { ["tenant"] = "contoso" },
    Delay = TimeSpan.FromMinutes(10), // or DeliverAt = an absolute time; not both
}, ct);

// A caller-chosen ID derived from business data makes the producer idempotent: a second insert with the same ID fails
// with the provider's unique-constraint error, so roll back and treat it as "already enqueued".
await outbox.EnqueueAsync(transaction, new OrderCreated(orderId, "Ada", 42.50m), new EnqueueOptions { MessageId = orderId }, ct);

// Up to 1000 messages in one call, one round trip on PostgreSQL. The IDs are returned in input order.
IReadOnlyList<Guid> ids = await outbox.EnqueueManyAsync(transaction, events, ct);

// Bytes you serialized yourself (protobuf, Avro, signed JSON) are stored and delivered unchanged.
await outbox.EnqueueRawAsync(transaction, new RawOutboxMessage("orders.created.v2", protobufBytes, "application/x-protobuf"), ct);
```

Without a business transaction, `EnqueueStandaloneAsync` opens and commits its own connection. It is durable, but **not**
atomic with any other write:

```csharp
await outbox.EnqueueStandaloneAsync(new OrderCreated(orderId, "Ada", 42.50m), cancellationToken: ct);
```

For small handlers, a delegate also works:

```csharp
relay.UseDeliveryHandler((message, ct) => Task.FromResult(DeliveryResult.Delivered()));
```

## Enqueue-only applications

To keep delivery out of a web tier, call `relay.DisableDispatcher()` in that application and run the dispatcher (with the
handler) in a separate worker that points to the same outbox table. Several dispatchers can run against one table at the same
time; claims never overlap. Retention cleanup still runs in every application unless you turn it off with
`ConfigureMaintenance(m => m.CleanupEnabled = false)`.

## SQL Server

Add the `DbRelay.SqlServer` package and use `UseSqlServer`. Everything else is the same, with `SqlConnection` and
`SqlTransaction`. From [`samples/SqlServer.Sample`](../samples/SqlServer.Sample):

```csharp
// The application owns the connection and the transaction: it opens, commits and disposes them.
// DbRelay inserts the outbox row on this connection, inside this SqlTransaction, and never commits,
// rolls back or disposes either of them.
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(ct);
await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
```

`UseSqlServer(string connectionString, ...)` or `UseSqlServer(Func<IServiceProvider, SqlConnection> factory, ...)`. The
factory must return a **new, unopened** connection each time; DbRelay opens and disposes it. Read
[sqlserver.md](sqlserver.md) before going to production.

## Next steps

- [configuration.md](configuration.md): tune batch size, concurrency, leases and retries.
- [reliability.md](reliability.md): what is guaranteed, and the test behind each guarantee.
- [troubleshooting.md](troubleshooting.md): when something is stuck.
