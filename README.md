# DbRelay

**A transactional outbox for .NET teams that already own their SQL.** Enqueue a message on the `DbTransaction` you already
have, using Dapper or plain ADO.NET. A background dispatcher delivers it to one interface you implement. Every reliability
guarantee is written down and mapped to a test that runs against real PostgreSQL and SQL Server.

> Status: **0.1.0, unreleased.** The packages are not on NuGet yet. Until they are, reference the projects in `src/`. The API
> may change before 1.0.

## The problem: dual writes

Illustration only, not DbRelay code:

```csharp
await transaction.CommitAsync();       // 1. the order is saved
await broker.PublishAsync(orderEvent); // 2. the process crashes here: the event is lost forever
```

Publish first and roll back, and you have announced an order that does not exist. Commit first and crash, and the event is
gone. No ordering of two independent writes is safe. The **transactional outbox** fixes this. You write the event to an
outbox table **in the same database transaction** as the business data. A background process later reads the table and
delivers what was committed.

## What DbRelay is, and is not

DbRelay **is**:

- an outbox insert that joins **your** `NpgsqlTransaction` or `SqlTransaction`; DbRelay never commits, rolls back or disposes it;
- a background dispatcher with atomic claims, database-clock leases, lock-token fencing, retries with jitter, dead-lettering,
  requeue and retention cleanup;
- one delivery interface, `IMessageDeliveryHandler`, so the target can be a broker, a webhook, a search index or email;
- a small, documented schema you create explicitly or through your migration tool;
- observable through an `ActivitySource`, a `Meter`, structured logs and a health check.

DbRelay is **not** a message broker, a messaging framework, an inbox or consumer-side deduplication, a saga engine, or an EF
Core integration. It does not make delivery exactly-once: it delivers **at least once** (see [Delivery guarantees](#delivery-guarantees)).

## Quick start (PostgreSQL)

**1. Reference the provider.** It brings in `DbRelay` and `DbRelay.Abstractions`.

```xml
<ProjectReference Include="path/to/src/DbRelay.PostgreSql/DbRelay.PostgreSql.csproj" />
<!-- after the 0.1.0 release: <PackageReference Include="DbRelay.PostgreSql" Version="0.1.0" /> -->
```

**2. Register DbRelay, a delivery handler and the health check** (from [`samples/PostgreSql.Sample/Program.cs`](samples/PostgreSql.Sample/Program.cs)):

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

**3. Create the outbox table** explicitly at startup. Alternatively, ship the script through your migration tool; see
[docs/postgres.md](docs/postgres.md).

```csharp
// Explicit, idempotent schema creation. It never drops or alters anything. If the application's database user may not run
// DDL, run IOutboxSchemaManager.GetCreateScript() through your migration tool instead.
await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
```

**4. Enqueue in your transaction:**

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

**5. Implement the handler** (from [`LoggingDeliveryHandler.cs`](samples/PostgreSql.Sample/LoggingDeliveryHandler.cs)):

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

Run it with `dotnet run --project samples/PostgreSql.Sample` and `POST /orders`. A real run's output is recorded in
[samples/README.md](samples/README.md). The full walkthrough is in [docs/getting-started.md](docs/getting-started.md).

## SQL Server

The same API, with `UseSqlServer` and a `SqlTransaction` (from [`samples/SqlServer.Sample`](samples/SqlServer.Sample)):

```csharp
builder.Services.AddDbRelay(relay =>
{
    // DbRelay opens its own connections from this string for dispatching, administration and schema creation.
    // Transactional enqueues always use the caller's SqlConnection and SqlTransaction.
    relay.UseSqlServer(connectionString, sql => sql.Schema = "sample");

    // This worker enqueues and delivers. In an application that should only enqueue (for example a web API whose messages
    // are delivered by a separate worker), remove UseDeliveryHandler and call DisableDispatcher instead:
    // relay.DisableDispatcher();
    relay.UseDeliveryHandler<ConsoleDeliveryHandler>();
    // ...
});
```

```csharp
// The application owns the connection and the transaction: it opens, commits and disposes them.
// DbRelay inserts the outbox row on this connection, inside this SqlTransaction, and never commits,
// rolls back or disposes either of them.
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(ct);
await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

await connection.ExecuteAsync(
    "INSERT INTO [sample].[orders] ([id], [customer], [total]) VALUES (@Id, @Customer, @Total)", order, transaction);

var messageId = await outbox.EnqueueAsync(
    transaction,
    new OrderPlaced(order.Id, order.Customer, order.Total),
    new EnqueueOptions { CorrelationId = order.Id.ToString(), Destination = "orders" },
    ct);

// Both rows commit together. Without this call, disposing the transaction rolls both back.
await transaction.CommitAsync(ct);
```

SQL Server specifics (RCSI, index hints, `QUOTED_IDENTIFIER`) are in [docs/sqlserver.md](docs/sqlserver.md).

## Transaction ownership rules

These rules hold for every `EnqueueAsync`, `EnqueueManyAsync` and `EnqueueRawAsync` call:

1. **You own the connection and the transaction.** You open, commit, roll back and dispose them. DbRelay never does any of
   these to a connection or transaction you pass in.
2. **The outbox row is inserted with exactly your connection and transaction**, so it commits or rolls back with your business
   data. Nothing is visible to the dispatcher until you commit.
3. **The pairing is validated.** The connection must be open and of the provider's type (`NpgsqlConnection` or `SqlConnection`).
   The transaction must still be active and must belong to that connection. Violations throw `ArgumentException` or
   `InvalidOperationException` with a message saying what to fix.
4. **Enqueue before you commit.** Enqueueing on a committed or rolled-back transaction throws.
5. **`transaction: null` means auto-commit.** The `IOutbox.EnqueueAsync(connection, null, ...)` overload inserts on its own and is
   **not** atomic with anything else.
6. **`EnqueueStandaloneAsync` is not atomic with anything.** DbRelay opens and commits its own connection. Use it only when
   there is no business write.
7. **No `TransactionScope` enlistment.** DbRelay never opens a second connection to join an ambient transaction (that would
   escalate to a distributed transaction). Pass the transaction explicitly.

DbRelay-owned connections (dispatcher, admin, schema creation, standalone enqueue) come from the connection string,
`NpgsqlDataSource` or `SqlConnection` factory you configure. DbRelay disposes a data source only when it built one itself from
a connection string.

## A custom delivery handler

One method: return `Delivered`, `Retry(reason, retryAfter)` or `DeadLetter(reason)`. A thrown exception counts as a transient
failure unless `RetryOptions.IsPermanentFailure` says otherwise. This webhook handler is from
[`samples/CustomHandler.Sample`](samples/CustomHandler.Sample/WebhookDeliveryHandler.cs) (abridged; `GetRetryAfter` reads
`Retry-After` as a delay or a date):

```csharp
internal sealed class WebhookDeliveryHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    TimeProvider time) : IMessageDeliveryHandler
{
    public const string HttpClientName = "webhook";

    public async Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Url);
        request.Headers.Add("Idempotency-Key", message.Id.ToString());
        request.Headers.Add("X-Message-Type", message.MessageType);
        if (message.CorrelationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", message.CorrelationId);
        }

        // The stored bytes are sent unchanged.
        request.Content = new ReadOnlyMemoryContent(message.Payload);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(message.ContentType);

        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            // Pass the token: DbRelay cancels it when DispatcherOptions.DeliveryTimeout elapses or the host shuts down.
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Connection refused, DNS failure, reset: transient. The reason is stored, so keep it free of secrets.
            return DeliveryResult.Retry($"Webhook unreachable: {exception.HttpRequestError}");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return DeliveryResult.Delivered();
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout)
            {
                // RetryAfter overrides the computed backoff; DbRelay caps it at RetryOptions.MaxDelay.
                return DeliveryResult.Retry($"Webhook returned {status}", GetRetryAfter(response));
            }

            if (status is >= 400 and < 500)
            {
                // Do not store the response body: it may echo the payload.
                return DeliveryResult.DeadLetter($"Webhook rejected the message with {status} {response.ReasonPhrase}");
            }

            return DeliveryResult.Retry($"Webhook returned {status}");
        }
    }
}
```

Things to know about handlers:

- A **new DI scope** is created for every delivery, so scoped dependencies work.
- **No database transaction is open** while the handler runs.
- There is **one handler per application**. Route inside it on `message.Destination` or `message.MessageType`.
- For a typed payload, inject `IMessageSerializer` and call `serializer.Deserialize<T>(message.Payload.Span)`. DbRelay never
  creates a type from the stored type name. See [`ConsoleDeliveryHandler.cs`](samples/SqlServer.Sample/ConsoleDeliveryHandler.cs).
- Reasons and exception messages are **stored and logged**. Keep them free of payload data and secrets.

## Retry configuration

From [`samples/SqlServer.Sample/Program.cs`](samples/SqlServer.Sample/Program.cs):

```csharp
relay.ConfigureRetry(retry =>
{
    retry.MaxAttempts = 8;                        // including the first attempt; then the message is dead-lettered
    retry.InitialDelay = TimeSpan.FromSeconds(2); // delay before the first retry
    retry.BackoffMultiplier = 3;                  // 2 s, 6 s, 18 s, ... before jitter
    retry.MaxDelay = TimeSpan.FromMinutes(10);    // cap for computed delays and for handler-supplied RetryAfter

    // Exceptions are transient by default. Classify the ones that can never succeed as permanent.
    retry.IsPermanentFailure = exception => exception is FormatException or NotSupportedException;
});
```

After failed attempt *n*, the delay is `d = min(MaxDelay, InitialDelay × BackoffMultiplier^(n-1))`, randomised to
`[d/2, d]` ("equal jitter"). Retries keep a floor, and messages that failed together do not retry in lockstep. The delay is
applied by the database (`now() + delay`), so application clock skew does not matter. The defaults are 10 attempts, 1 s
initial delay, ×2 and a 5 min cap. Every option, with its valid range, is in [docs/configuration.md](docs/configuration.md).

## Delivery guarantees

- **At-least-once.** Every committed message is delivered one or more times until it is delivered or dead-lettered.
  **Duplicates are possible.** For example, a worker can deliver a message and then crash (or lose the database) before it
  records success. After the lease expires, another worker delivers it again.
- **Use `OutboxMessage.Id` as the idempotency key.** It is stable across every attempt and redelivery. It is a UUIDv7 by
  default, or your own value via `EnqueueOptions.MessageId`. Receivers must be idempotent: deduplicate on it, as
  `CustomHandler.Sample` does with the `Idempotency-Key` header.
- **No ordering guarantee.** Claims are roughly ordered by due time, but concurrency, several workers and retries reorder
  messages. In the recorded PostgreSQL sample run, two messages were delivered in the opposite order to the one they were
  enqueued in.
- **Only committed messages are delivered.** A rolled-back or never-committed transaction leaves nothing to deliver.
- **Crashes are bounded.** If a worker dies mid-delivery, its claim expires after `LeaseDuration` (database clock) and the
  message is claimed again with `Attempt` incremented. Attempts are counted when a message is claimed. A message that crashes
  its worker every time therefore still ends in the dead-letter state after `MaxAttempts`.
- **A stale worker cannot overwrite a newer outcome.** Every update after a claim is fenced by that claim's lock token.
- **Dead letters are kept** until you requeue them. They are never deleted automatically.

Each guarantee is mapped to the test that proves it in [docs/reliability.md](docs/reliability.md).

## Operations

```csharp
// Requeue one dead-lettered message, or up to 100 of one type (oldest first). Attempts restart from 1.
bool requeued = await admin.RequeueAsync(messageId, ct);
int count = await admin.RequeueDeadLetteredAsync("orders.created", maxCount: 100, ct);
```

`IOutboxAdmin` also offers `GetAsync`, `QueryAsync`, `GetStatisticsAsync` and `PurgeDeliveredAsync`. See
[docs/troubleshooting.md](docs/troubleshooting.md).

## Observability

| Signal | Name |
|---|---|
| `ActivitySource` | `DbRelay` (`DbRelayDiagnostics.ActivitySourceName`): `DbRelay enqueue` (Producer) and `DbRelay deliver` (Consumer, parented to the enqueuing trace through the stored `traceparent`) |
| `Meter` | `DbRelay` (`DbRelayDiagnostics.MeterName`) |
| Logs | Event IDs 1000 to 1012 (dispatcher) and 1100 to 1101 (maintenance). Message ID, type and attempt are logged; payloads and connection strings never are |
| Health check | `AddHealthChecks().AddDbRelay(...)` |

With OpenTelemetry (`OpenTelemetry.Extensions.Hosting`; DbRelay itself has no OpenTelemetry dependency):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(DbRelayDiagnostics.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter(DbRelayDiagnostics.MeterName));
```

Metrics:

| Instrument | Type | Unit | Meaning |
|---|---|---|---|
| `dbrelay.messages.enqueued` | counter | `{message}` | Messages written (counted before your transaction commits) |
| `dbrelay.messages.delivered` | counter | `{message}` | Messages delivered and recorded |
| `dbrelay.messages.retried` | counter | `{message}` | Failed attempts scheduled for retry |
| `dbrelay.messages.dead_lettered` | counter | `{message}` | Messages moved to dead-letter |
| `dbrelay.messages.released` | counter | `{message}` | Claims returned unattempted (shutdown, not enough lease left) |
| `dbrelay.lease.lost` | counter | `{message}` | Outcomes rejected because another worker had reclaimed the message |
| `dbrelay.dispatcher.poll_failures` | counter | `{failure}` | Failed claim queries |
| `dbrelay.delivery.duration` | histogram | `s` | Handler duration (tag `dbrelay.outcome`) |
| `dbrelay.claim.duration` | histogram | `s` | Claim query duration |
| `dbrelay.claim.batch_size` | histogram | `{message}` | Messages per claim |
| `dbrelay.ack.batch_size` | histogram | `{message}` | Delivered acknowledgements written per round trip (batched acknowledgements) |
| `dbrelay.delivery.abandoned` | counter | `{delivery}` | Handler invocations abandoned because they ignored cancellation |
| `dbrelay.delivery.abandoned.active` | gauge | `{handler}` | Abandoned handlers still running (delivery pauses at the limit) |
| `dbrelay.messages.pending` | gauge | `{message}` | Pending messages* |
| `dbrelay.messages.processing` | gauge | `{message}` | Claimed messages* |
| `dbrelay.messages.dead_lettered.current` | gauge | `{message}` | Messages currently dead-lettered* |
| `dbrelay.messages.oldest_due_age` | gauge | `s` | How long the oldest due message has waited* |

Counters and the delivery histogram carry the `dbrelay.message.type` tag. *Gauges come from a periodic statistics query
(`MaintenanceOptions.StatisticsInterval`, default 1 min, `null` disables them), so scrapes never hit the database.

**Health check:** **Unhealthy** (or your `failureStatus`) when the database or outbox table is unreachable. **Degraded** when
the dispatcher is not running, its last polls failed, or it has made no progress for a while, or when an optional threshold
(`MaxOldestDueAge`, `MaxDeadLettered`) is exceeded. Otherwise **Healthy**. What Degraded means and what to do about it is in
[docs/troubleshooting.md](docs/troubleshooting.md).

## Limitations

- **At-least-once only**, with **no ordering** guarantee. Per-key ordering is on the roadmap.
- **One delivery handler per application.**
- **Polling.** Latency is bounded by `PollingInterval` (default 1 s). There are no LISTEN/NOTIFY wake-ups yet.
- **Tested on PostgreSQL 17.5 and SQL Server 2022 Express (Windows) only.** PostgreSQL 15/16/18, SQL Server 2019 and Azure SQL are
  supported targets but have not been tested yet.
- **SQL Server index hints.** DbRelay's statements name its indexes, so they **fail** (rather than slow down) if a DBA drops one
  (ADR 0002).
- **SQL Server: `EnqueueManyAsync` without a transaction and with more than 200 messages** begins its own transaction on your
  connection to stay all-or-nothing. That can conflict with an ambient `TransactionScope` enlistment.
- **Wrapped connections are rejected.** Profiling wrappers such as MiniProfiler's `ProfiledDbConnection` fail the provider type
  check. Pass the inner `NpgsqlConnection` or `SqlConnection` and its transaction.
- **Error text is stored.** Handler exception messages and reasons go into `last_error` (truncated to 2,000 characters) and the
  logs. Keep secrets and payload data out of them.
- **`GetStatisticsAsync` counts every row by status.** On very large tables, keep `StatisticsInterval` long and threshold-based
  health checks infrequent.
- **No configuration binding** from `IConfiguration` yet. Options are set in code; bind your own settings and assign them.

The full list is in [KNOWN_ISSUES.md](KNOWN_ISSUES.md).

## When not to use DbRelay

DbRelay is deliberately narrow. These mature projects solve bigger problems, and might suit you better:

- **[Brighter](https://github.com/BrighterCommand/Brighter)** (MIT): a command processor with an outbox, an inbox, many
  transports and Dapper samples. Choose it if you want a command/message pipeline, not only an outbox.
- **[MassTransit](https://github.com/MassTransit/MassTransit)**: the most complete .NET messaging framework, with sagas,
  consumer inbox/outbox, routing and many brokers. Its outbox is EF Core or MongoDB based. v9 and later are commercially
  licensed; check the terms.
- **[Wolverine](https://github.com/JasperFx/wolverine)** (MIT): messaging and a mediator with a durable inbox/outbox, rich
  retry policies, dead-letter replay, and local or database queues. It is a good fit if you adopt its runtime and handler
  model, especially with Marten or EF Core.
- **[CAP](https://github.com/dotnetcore/CAP)** (MIT): an outbox plus an event bus over RabbitMQ, Kafka and others, with a
  dashboard and a received-message store. It requires a broker, and its database providers depend on EF Core Relational.

Choose one of them if you need consumer-side exactly-once processing (an inbox), sagas, broker routing topologies, or a
dashboard. Choose DbRelay if you use Dapper or ADO.NET, want to keep your own transaction and your own delivery code, and
want a small dependency whose failure behaviour is documented and tested.

## Documentation

| Guide | Contents |
|---|---|
| [Getting started](docs/getting-started.md) | From zero to a delivered message |
| [Configuration](docs/configuration.md) | Every option, default and valid range |
| [Architecture](docs/architecture.md) | Components, state machine, claim SQL, design decisions |
| [PostgreSQL](docs/postgres.md) | Schema, indexes, permissions, autovacuum, migration tools |
| [SQL Server](docs/sqlserver.md) | RCSI, index hints, `QUOTED_IDENTIFIER`, `sqlcmd -I` |
| [Reliability](docs/reliability.md) | Every guarantee and the test that proves it |
| [Troubleshooting](docs/troubleshooting.md) | Stuck messages, lost leases, dead letters, health states, requeue |
| [Migrating from a hand-rolled outbox](docs/migrating-from-hand-rolled-outbox.md) | The classic bugs and how DbRelay handles them |
| [Samples](samples/README.md) | Three runnable samples and their recorded output |

## Roadmap

Planned for 0.x, and designed for but not built yet: per-key ordering, LISTEN/NOTIFY wake-ups for lower latency, optional
transport packages (RabbitMQ, Kafka, Azure Service Bus, HTTP), an inbox for consumer deduplication, and management endpoints.
Breadth comes after the core contract is proven in production. Version 1.0 will follow real-world feedback.

## Contributing

Contributions are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md) for the build, the database test setup, how to add a
provider through the conformance suite, and the pull request process. Please follow the [Code of Conduct](CODE_OF_CONDUCT.md),
and report vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)
