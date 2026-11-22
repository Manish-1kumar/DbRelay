# DbRelay samples

Each sample targets `net10.0` and references the projects under `src/` directly. The documentation snippets in the
repository README and in `docs/` are copied from these files (look for `// snippet:` markers).

| Sample | Kind | Database | Shows |
|---|---|---|---|
| [`PostgreSql.Sample`](PostgreSql.Sample) | ASP.NET Core minimal API | PostgreSQL | `POST /orders` writes an order with Dapper and enqueues `OrderCreated` in the same `NpgsqlTransaction`; logging handler; health check; `GET /outbox/stats` and `GET /outbox/messages/{id}` via `IOutboxAdmin` |
| [`SqlServer.Sample`](SqlServer.Sample) | Worker Service | SQL Server | Enqueue inside a `SqlTransaction` with Dapper; typed deserialization in the handler; explicit retry configuration; `DisableDispatcher()` as the enqueue-only alternative |
| [`CustomHandler.Sample`](CustomHandler.Sample) | ASP.NET Core host | PostgreSQL | A webhook `IMessageDeliveryHandler` using `IHttpClientFactory`: 2xx is Delivered, 408/429/503 is Retry with `Retry-After`, other 4xx is DeadLetter, and the message ID is sent as `Idempotency-Key`. Includes an in-process webhook receiver |

Every sample calls `IOutboxSchemaManager.EnsureCreatedAsync()` at startup, which creates the outbox schema (for example
`sample`) if it does not exist. To clean up afterwards, drop that schema.

## Running

Set the connection string with configuration, for example an environment variable (two underscores):

```powershell
# PostgreSQL sample (listens on http://localhost:5070)
$env:ConnectionStrings__Outbox = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"
dotnet run --project samples/PostgreSql.Sample -c Release

# in another terminal
Invoke-RestMethod -Method Post http://localhost:5070/orders -ContentType application/json -Body '{"customer":"Ada","total":42.50}'
Invoke-RestMethod http://localhost:5070/outbox/stats
Invoke-WebRequest http://localhost:5070/health
```

```powershell
# SQL Server sample: places 3 orders (Producer:Orders) and delivers them
$env:ConnectionStrings__Outbox = "Server=.\SQLEXPRESS;Database=dbrelay_sample;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project samples/SqlServer.Sample -c Release
```

```powershell
# Custom webhook handler (listens on http://localhost:5080 and posts to its own /webhook endpoint)
$env:ConnectionStrings__Outbox = "Host=localhost;Username=postgres;Password=postgres;Database=postgres"
dotnet run --project samples/CustomHandler.Sample -c Release
```

To post to a real webhook instead of the in-process receiver, set `Webhook__Url` to its absolute URL and
`Webhook__HostLocalReceiver=false`. The SQL Server database in the connection string must already exist.

## Recorded output

These are real runs from 2026-11-21: PostgreSQL 17.5 and SQL Server 2022 Express on Windows. The worker ID's machine name
is replaced with `<machine>`.

`PostgreSql.Sample` after three `POST /orders` calls. The second and third messages were delivered in the opposite order to
the one they were enqueued in: DbRelay does not guarantee ordering.

```text
info: DbRelay.Dispatching.OutboxDispatcher[1000]
      DbRelay dispatcher <machine>:17196:28887047c979493c99db18b5a5e0277a started (batch size 100, max concurrency 8, lease 00:02:00).
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5070
info: PostgreSql.Sample.LoggingDeliveryHandler[0]
      Delivering message 01a1fee6-a98c-78e6-8ae6-3aa792043e85 (orders.created), attempt 1, 81 payload bytes
info: PostgreSql.Sample.LoggingDeliveryHandler[0]
      Delivering message 01a1fee6-d7c5-7058-a035-6cce44529016 (orders.created), attempt 1, 85 payload bytes
info: PostgreSql.Sample.LoggingDeliveryHandler[0]
      Delivering message 01a1fee6-d7a5-7011-9575-68e6a2dac80a (orders.created), attempt 1, 85 payload bytes
```

```text
GET /outbox/messages/01a1fee6-a98c-78e6-8ae6-3aa792043e85
{"id":"01a1fee6-a98c-78e6-8ae6-3aa792043e85","messageType":"orders.created","status":"Delivered","attemptCount":1,"createdAt":"2026-11-21T16:18:11.960509+00:00","deliveredAt":"2026-11-21T16:18:12.448084+00:00","lastError":null}
GET /outbox/stats
{"pending":0,"processing":0,"delivered":3,"deadLettered":0,"oldestDueAge":null}
GET /health
200 Healthy
```

`SqlServer.Sample`:

```text
info: DbRelay.Dispatching.OutboxDispatcher[1000]
      DbRelay dispatcher <machine>:13392:f1ad42aae51a4f1db91087d4604f189e started (batch size 100, max concurrency 8, lease 00:01:00).
info: SqlServer.Sample.OrderProducer[0]
      Order 1 committed with outbox message 01a1fef2-d6c6-7dd5-a7e5-d2bcffe53f3e
info: SqlServer.Sample.ConsoleDeliveryHandler[0]
      Delivered message 01a1fef2-d6c6-7dd5-a7e5-d2bcffe53f3e (orders.placed) for order 5234079b-f7e3-4ed2-9bef-ba1b3c4b90a0, attempt 1, correlation 5234079b-f7e3-4ed2-9bef-ba1b3c4b90a0
info: SqlServer.Sample.OrderProducer[0]
      Order 2 committed with outbox message 01a1fef2-daca-74d1-b7b2-2aa2f9595eae
info: SqlServer.Sample.ConsoleDeliveryHandler[0]
      Delivered message 01a1fef2-daca-74d1-b7b2-2aa2f9595eae (orders.placed) for order a7f91f3b-6ab6-4971-8a05-8fbf06bdef08, attempt 1, correlation a7f91f3b-6ab6-4971-8a05-8fbf06bdef08
info: SqlServer.Sample.OrderProducer[0]
      Order 3 committed with outbox message 01a1fef2-debe-79a0-8b29-df163a85cb5f
info: SqlServer.Sample.ConsoleDeliveryHandler[0]
      Delivered message 01a1fef2-debe-79a0-8b29-df163a85cb5f (orders.placed) for order f2f24034-5b0c-4e82-a998-060ca5cadd0a, attempt 1, correlation f2f24034-5b0c-4e82-a998-060ca5cadd0a
```

`CustomHandler.Sample`: one message is accepted, one is throttled and then accepted after `Retry-After: 2`, and one is rejected
and dead-lettered.

```text
info: CustomHandler.Sample.DemoWebhookReceiver[0]
      Webhook: 01a1fef4-5ee3-7bb0-8e4a-16264c1a49e2 (invoices.issued, 67 bytes) -> 429
info: CustomHandler.Sample.DemoWebhookReceiver[0]
      Webhook: 01a1fef4-5eea-7d2f-b7e6-8b1159a063db (legacy.event, 50 bytes) -> 422
info: CustomHandler.Sample.DemoWebhookReceiver[0]
      Webhook: 01a1fef4-5e83-7af3-b61d-4d2fa30b98ae (customers.registered, 79 bytes) -> 200
fail: DbRelay.Dispatching.OutboxDispatcher[1006]
      Message 01a1fef4-5eea-7d2f-b7e6-8b1159a063db (legacy.event) was dead-lettered after attempt 1: Webhook rejected the message with 422 Unprocessable Entity
warn: DbRelay.Dispatching.OutboxDispatcher[1005]
      Delivery of message 01a1fef4-5ee3-7bb0-8e4a-16264c1a49e2 (invoices.issued) failed on attempt 1/5: Webhook returned 429. Retrying in 00:00:02.
info: CustomHandler.Sample.DemoWebhookReceiver[0]
      Webhook: 01a1fef4-5ee3-7bb0-8e4a-16264c1a49e2 (invoices.issued, 67 bytes) -> 200
```
