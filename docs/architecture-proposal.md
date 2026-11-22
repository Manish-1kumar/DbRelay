# DbRelay Architecture Proposal (v0.1)

Status: **ACCEPTED 2026-11-18** (see `docs/decisions/0001-initial-architecture-decisions.md`; D1 amended by owner to `net8.0;net9.0;net10.0;net11.0`) · Owner: Principal Architect

This document defines the architecture, public API, database contracts and delivery semantics for DbRelay's first release. Items marked **[DECISION]** need explicit approval; they are also summarised in section 12.

---

## 1. Scope of the first release

In scope (v0.1):
- Transactional outbox: enqueue inside a caller-owned ADO.NET transaction (works with Dapper, raw ADO.NET, or any code holding a `DbTransaction`).
- PostgreSQL provider (packaged in the first release).
- SQL Server provider (built and tested in this cycle; **packaged in the next release** per the package strategy).
- Background dispatcher: polling, atomic claiming, leases with lock tokens, bounded concurrency, graceful shutdown.
- User-supplied delivery handler (transport-independent).
- Retry with exponential backoff + jitter, failure classification, dead-lettering, manual requeue.
- Basic retention cleanup of delivered messages.
- Observability: structured logs, `ActivitySource`/`Meter` ("DbRelay"), health check, trace-context propagation.

Out of scope (designed for, not built): inbox, per-aggregate ordering, LISTEN/NOTIFY wake-ups, broker adapters (RabbitMQ, Kafka, Azure Service Bus, HTTP), management endpoints, dashboard.

---

## 2. Packages and dependencies

| Package | Contents | Direct dependencies |
|---|---|---|
| `DbRelay.Abstractions` | Contracts application code and handler authors use: `IOutbox`, `OutboxMessage`, `IMessageDeliveryHandler`, `DeliveryResult`, `EnqueueOptions`, `OutboxMessageStatus`, `IMessageSerializer`, `IOutboxAdmin` + query models. | **none** |
| `DbRelay` | Engine: DI registration/builder, options + validation, System.Text.Json serializer, type-name registry, dispatcher hosted service, retry policy, diagnostics, health check, storage SPI (`IOutboxStore`). | Abstractions, `Microsoft.Extensions.Hosting.Abstractions`, `.Options`, `.Logging.Abstractions`, `.Diagnostics.HealthChecks` |
| `DbRelay.PostgreSql` | `UsePostgreSql(...)`, store implementation, schema manager, SQL scripts. | DbRelay, `Npgsql` |
| `DbRelay.SqlServer` | `UseSqlServer(...)`, store implementation, schema manager, SQL scripts. | DbRelay, `Microsoft.Data.SqlClient` |

- **[DECISION D1] Target framework: `net10.0` only.** .NET 10 is the current LTS (supported to 2028-11-14). .NET 11.0.0 GA'd on 2026-11-10 as STS; a `net10.0` library runs on .NET 11 unchanged, and its SDK is not installed locally. Add a `net11.0` TFM only if we need an 11-only API. .NET 8/9 reached end of support on 2026-11-10, so we do not target them.
- **[DECISION D2] Providers use ADO.NET internally, not Dapper.** DbRelay is *built for* Dapper users (the public API takes the same `DbConnection`/`DbTransaction` Dapper uses, and all tests and samples write business data with Dapper), but the providers execute their ~12 fixed statements with plain `DbCommand`. Rationale: zero risk of Dapper version conflicts in consumer apps, no reflection on hot paths, trimming/AOT friendliness, one less transitive dependency. The provider code is small and fixed, so Dapper adds little there. *Alternative:* use Dapper inside providers as the brief suggests. That's viable, but it adds a dependency without making things better for the user.
- Microsoft.Extensions.* dependencies reference **10.0.x** lower bounds so .NET 10 apps are not forced onto 11.0 packages.
- No OpenTelemetry package dependency: we emit through BCL `ActivitySource`/`Meter`; users enable with `.AddSource("DbRelay")` / `.AddMeter("DbRelay")`.

---

## 3. Public API (proposed)

### 3.1 Registration

```csharp
builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(connectionString, pg =>
    {
        pg.Schema = "dbrelay";             // validated identifier, default "dbrelay"
        pg.TableName = "outbox_messages";  // validated identifier
    });

    relay.UseDeliveryHandler<RabbitMqPublisher>();      // required when dispatcher enabled

    relay.MapMessageType<OrderCreated>("orders.created"); // optional explicit names

    relay.ConfigureDispatcher(d =>
    {
        d.BatchSize = 100;
        d.MaxConcurrency = 8;
        d.PollingInterval = TimeSpan.FromSeconds(1);
        d.LeaseDuration = TimeSpan.FromMinutes(2);
        d.DeliveryTimeout = TimeSpan.FromSeconds(30);
    });

    relay.ConfigureRetry(r =>
    {
        r.MaxAttempts = 10;
        r.InitialDelay = TimeSpan.FromSeconds(1);
        r.MaxDelay = TimeSpan.FromMinutes(5);
    });
});

// Explicit, idempotent, non-destructive schema creation (or ship the SQL script to your DBA / migration tool):
await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
```

- Overloads: `UsePostgreSql(NpgsqlDataSource)`, `UsePostgreSql(Func<IServiceProvider, NpgsqlDataSource>)`; `UseSqlServer(string)`; `UseSqlServer(Func<IServiceProvider, SqlConnection>)`.
- `relay.DisableDispatcher()` lets web/API nodes enqueue only while a separate worker service delivers.
- Options can also bind from `IConfiguration` (`AddDbRelay(configuration.GetSection("DbRelay"), relay => ...)`). All options are validated at startup (`ValidateOnStart`) with actionable messages.

### 3.2 Enqueue - the core contract

```csharp
public interface IOutbox
{
    // Primary: join the caller's transaction. The connection is taken from transaction.Connection.
    Task<Guid> EnqueueAsync<TMessage>(IDbTransaction transaction, TMessage message,
        EnqueueOptions? options = null, CancellationToken cancellationToken = default);

    // Explicit connection + optional transaction. Validates transaction.Connection == connection.
    Task<Guid> EnqueueAsync<TMessage>(IDbConnection connection, IDbTransaction? transaction, TMessage message,
        EnqueueOptions? options = null, CancellationToken cancellationToken = default);

    // Batch variants (single round trip).
    Task<IReadOnlyList<Guid>> EnqueueManyAsync<TMessage>(IDbTransaction transaction,
        IEnumerable<TMessage> messages, EnqueueOptions? options = null, CancellationToken cancellationToken = default);

    // Pre-serialized payloads (protobuf, Avro, raw JSON) - no serializer involved.
    Task<Guid> EnqueueRawAsync(IDbTransaction transaction, RawOutboxMessage message,
        CancellationToken cancellationToken = default);

    // Standalone: DbRelay opens and commits its own connection. NOT atomic with any business write.
    Task<Guid> EnqueueStandaloneAsync<TMessage>(TMessage message,
        EnqueueOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed record EnqueueOptions
{
    public Guid? MessageId { get; init; }            // default: UUIDv7 generated by DbRelay
    public string? Destination { get; init; }        // topic/queue/route hint for the handler
    public string? CorrelationId { get; init; }
    public string? CausationId { get; init; }
    public DateTimeOffset? DeliverAt { get; init; }  // scheduled delivery
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
}
```

Usage with Dapper:

```csharp
await using var connection = await dataSource.OpenConnectionAsync(ct);
await using var transaction = await connection.BeginTransactionAsync(ct);

await connection.ExecuteAsync("INSERT INTO orders (id, total) VALUES (@Id, @Total)", order, transaction);
await outbox.EnqueueAsync(transaction, new OrderCreated(order.Id), cancellationToken: ct);

await transaction.CommitAsync(ct);   // the caller commits; DbRelay never does
```

Contract guarantees, which are documented and tested:
- DbRelay never commits, rolls back or disposes the caller's connection or transaction.
- It validates: the transaction is non-null and active (`transaction.Connection != null`), is the provider's type (`NpgsqlTransaction`/`SqlTransaction`), belongs to the supplied connection, and the connection is open. Each violation throws `ArgumentException`/`InvalidOperationException` with a message saying what to fix.
- `IDbTransaction` is accepted (Dapper's type) and must be a `DbTransaction` at runtime, because async execution requires `DbCommand`.
- **[DECISION D3] No hidden ambient transaction in v0.1.** The prompt's "automatic transaction participation" is met by deriving the connection from the transaction you pass in. We deliberately do *not* enlist in `TransactionScope` from a second connection, because that escalates to a distributed transaction (unsupported on Linux). An AsyncLocal "unit of work" scope (`outbox.UseTransaction(tx)` + parameterless enqueue) can be added later without breaking changes. The non-transactional path is named **`EnqueueStandaloneAsync`** so nobody mistakes it for an atomic one.

### 3.3 Delivery

```csharp
public interface IMessageDeliveryHandler
{
    Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public readonly record struct DeliveryResult
{
    public static DeliveryResult Delivered();
    public static DeliveryResult Retry(string reason, TimeSpan? retryAfter = null);
    public static DeliveryResult DeadLetter(string reason);   // permanent failure
}

public sealed class OutboxMessage                // immutable view handed to handlers
{
    public Guid Id { get; }                      // stable across retries -> consumer idempotency key
    public string MessageType { get; }           // logical name, never used for type activation
    public string? Destination { get; }
    public ReadOnlyMemory<byte> Payload { get; }
    public string ContentType { get; }           // e.g. "application/json"
    public IReadOnlyDictionary<string, string> Headers { get; }  // includes traceparent when captured
    public string? CorrelationId { get; }
    public string? CausationId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ScheduledAt { get; }
    public int Attempt { get; }                  // 1-based attempt number of this delivery
    public T Deserialize<T>();                   // caller states T explicitly; uses configured serializer
}
```

- A thrown exception is treated as `Retry` unless `RetryOptions.IsPermanentFailure(Exception)` returns true (default: false for all exceptions). Retries stop at `MaxAttempts`, after which the message is dead-lettered.
- The handler is resolved from a **fresh DI scope per delivery**, so scoped dependencies (HttpClient handlers, DbContexts of other systems) work.
- One handler per outbox in v0.1. Handlers route on `Destination`/`MessageType`. A built-in routing handler is a candidate for v0.2.

### 3.4 Operations / recovery

```csharp
public interface IOutboxAdmin
{
    Task<OutboxMessageDetails?> GetAsync(Guid id, CancellationToken ct = default);
    Task<PagedResult<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken ct = default); // status, type, age, paging
    Task<OutboxStatistics> GetStatisticsAsync(CancellationToken ct = default);   // counts by status, oldest pending age
    Task<bool> RequeueAsync(Guid id, bool resetAttempts = true, CancellationToken ct = default); // DeadLettered -> Pending only
    Task<int> RequeueDeadLetteredAsync(OutboxQuery filter, int maxCount, CancellationToken ct = default);
    Task<int> PurgeDeliveredAsync(DateTimeOffset olderThan, CancellationToken ct = default);
}
```

---

## 4. Message lifecycle and state machine

`status` values: `0 Pending`, `1 Processing`, `2 Delivered`, `3 DeadLettered`.

| From | Event | To | Guard | Effect |
|---|---|---|---|---|
| (new) | Enqueue in caller tx | Pending | - | `available_at = deliver_at ?? now()`, `attempt_count = 0` |
| Pending | Claim | Processing | `available_at <= now()`, row not locked | new `lock_token`, `attempt_count += 1`, `available_at = now() + lease` |
| Processing (lease expired) | Claim | Processing | `available_at <= now()` | same as above (crash recovery) |
| Processing | Handler `Delivered` | Delivered | `lock_token = @token` | `delivered_at = now()`, clear token |
| Processing | `Retry` and attempts < max | Pending | `lock_token = @token` | `available_at = now() + backoff`, `last_error` |
| Processing | `Retry` and attempts >= max, or `DeadLetter` | DeadLettered | `lock_token = @token` | `dead_lettered_at = now()`, `last_error` |
| Processing | Claimed with `attempt_count >= max` (crash-looping) | DeadLettered | `lock_token = @token` | "max attempts exceeded without completion" |
| Processing | Graceful shutdown / lease too short to start | Pending | `lock_token = @token` | `available_at = now()`, `attempt_count -= 1` |
| DeadLettered | Admin requeue | Pending | `status = 3` | optionally reset attempts |
| Delivered | Retention cleanup | (deleted) | `delivered_at < cutoff` | batched delete |

A single `available_at` column serves both scheduling and lease expiry, the same idea as SQS visibility timeouts. The claim predicate is therefore one indexable range: `status IN (0,1) AND available_at <= now()`. A crashed worker's message becomes claimable again once its lease passes, so recovery needs no separate sweeper.

A guarded update that affects 0 rows means the lease was lost to another worker. The dispatcher logs it, counts it (`dbrelay.lease.lost`), and moves on. It never overwrites the other worker's state.

---

## 5. Delivery guarantees

- **At-least-once.** A message is delivered one or more times. Duplicates occur when a worker delivers and then fails to record success (crash, DB outage, lease expiry during a slow delivery).
- **Stable `Id`** (UUIDv7, or caller-supplied) is the idempotency key consumers should use.
- **No ordering guarantee** in v0.1. Claims are ordered by `available_at`, but concurrency, multiple workers and retries reorder messages. Per-aggregate ordering is a roadmap item.
- **Bounded attempts.** Attempts are counted at claim time, so a message that crashes the process every time still reaches `DeadLettered`.
- **Atomicity** with business data holds only for `EnqueueAsync(transaction, ...)`. `EnqueueStandaloneAsync` gives durable delivery, but it isn't atomic with anything.

---

## 6. Dispatcher design

```
loop until stopping:
  wait until no claimed-but-unstarted messages remain locally   -- backpressure: never hoard leases
  batch = store.Claim(BatchSize, lease, token)                   -- one autocommit statement
  for each message (bounded by SemaphoreSlim(MaxConcurrency)):
      if localLeaseDeadline - now < DeliveryTimeout + safety: release(message); continue
      scope = services.CreateAsyncScope()
      result = handler.DeliverAsync(message, linked(stopping, DeliveryTimeout))
      ack/retry/dead-letter with lock token (short autocommit statement)
  if batch.Count == requested: poll again immediately
  else: wait PollingInterval (+/-10% jitter)
on DB error: log, exponential backoff of the loop (1s -> 30s), mark health Degraded, keep running
on stop: stop claiming, wait for in-flight up to host shutdown timeout, release unfinished leases
```

- No DB transaction spans `DeliverAsync`. Claim and acknowledgement are separate short statements.
- `DeliveryTimeout` must be less than `LeaseDuration`; validation enforces `LeaseDuration >= DeliveryTimeout * 2`.
- Acknowledgements are per message in v0.1. Batched acknowledgements are a measured optimisation for M10.
- Defaults: `BatchSize 100` (max 1000), `MaxConcurrency 8` (max 256), `PollingInterval 1s`, `LeaseDuration 2m`, `DeliveryTimeout 30s`, `MaxPayloadBytes 1 MiB`, `MaxAttempts 10`.
- The worker id (`machine:process:guid`) is stored in `locked_by` for diagnostics only. Correctness never depends on it.

---

## 7. Retry policy

`delay(n) = min(MaxDelay, InitialDelay * Multiplier^(n-1))`, then **equal jitter**: `delay/2 + random(0, delay/2)`.

Equal jitter keeps a floor of half the delay, which avoids the near-zero retries that full jitter allows. The random half spreads out retries that were scheduled together, so a broker outage doesn't come back as a synchronized retry storm. The handler's `retryAfter` overrides the computed delay, capped at `MaxDelay`. `IRetryPolicy` is replaceable. Delays are computed in the app but applied by the database (`now() + @delay`), so app-server clock skew never affects scheduling.

---

## 8. Database contracts

### 8.1 PostgreSQL (15+; tested on 15 and 17/18)

```sql
CREATE SCHEMA IF NOT EXISTS dbrelay;

CREATE TABLE IF NOT EXISTS dbrelay.outbox_messages (
    id               uuid         PRIMARY KEY,           -- UUIDv7: time-ordered, good B-tree locality
    message_type     text         NOT NULL,
    destination      text         NULL,
    payload          bytea        NOT NULL,
    content_type     text         NOT NULL,
    headers          jsonb        NULL,
    correlation_id   text         NULL,
    causation_id     text         NULL,
    status           smallint     NOT NULL DEFAULT 0,
    attempt_count    integer      NOT NULL DEFAULT 0,
    created_at       timestamptz  NOT NULL DEFAULT now(),
    scheduled_at     timestamptz  NOT NULL,
    available_at     timestamptz  NOT NULL,
    lock_token       uuid         NULL,
    locked_by        text         NULL,
    last_attempt_at  timestamptz  NULL,
    delivered_at     timestamptz  NULL,
    dead_lettered_at timestamptz  NULL,
    last_error       text         NULL,                  -- truncated to 2000 chars
    CONSTRAINT ck_outbox_status CHECK (status BETWEEN 0 AND 3)
);

CREATE INDEX IF NOT EXISTS ix_outbox_messages_claim   ON dbrelay.outbox_messages (available_at) WHERE status IN (0, 1);
CREATE INDEX IF NOT EXISTS ix_outbox_messages_cleanup ON dbrelay.outbox_messages (delivered_at) WHERE status = 2;
CREATE INDEX IF NOT EXISTS ix_outbox_messages_dead    ON dbrelay.outbox_messages (dead_lettered_at) WHERE status = 3;

CREATE TABLE IF NOT EXISTS dbrelay.schema_version (component text PRIMARY KEY, version integer NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());
```

Claim (single autocommitted statement):

```sql
WITH next AS (
    SELECT id FROM dbrelay.outbox_messages
    WHERE status IN (0, 1) AND available_at <= now()
    ORDER BY available_at
    LIMIT @batch_size
    FOR UPDATE SKIP LOCKED
)
UPDATE dbrelay.outbox_messages m
SET status = 1, lock_token = @lock_token, locked_by = @worker_id,
    attempt_count = m.attempt_count + 1, last_attempt_at = now(),
    available_at = now() + @lease
FROM next WHERE m.id = next.id
RETURNING m.*;
```

**[DECISION D4] Payload stored as `bytea`, not `jsonb`.** We looked at `jsonb` as the brief asked, and recommend against it for the payload column:
1. `jsonb` rewrites the document: it reorders keys, drops whitespace and collapses duplicate keys. A relay should deliver exactly the bytes the app serialized, which matters for signed payloads, hashes and schema-registry framing.
2. It rejects non-JSON payloads such as protobuf, Avro or MessagePack.
3. It adds parse cost on insert.

Payloads stay inspectable with `convert_from(payload, 'UTF8')::jsonb`, and we will document that. `jsonb` is still used for `headers`, which is DbRelay-owned metadata.

### 8.2 SQL Server (2019+, Azure SQL; tested on 2022)

```sql
CREATE TABLE [dbrelay].[outbox_messages] (
    [sequence]         bigint IDENTITY(1,1) NOT NULL,
    [id]               uniqueidentifier NOT NULL,
    [message_type]     nvarchar(256)  NOT NULL,
    [destination]      nvarchar(512)  NULL,
    [payload]          varbinary(max) NOT NULL,
    [content_type]     nvarchar(128)  NOT NULL,
    [headers]          nvarchar(max)  NULL,       -- JSON text
    [correlation_id]   nvarchar(128)  NULL,
    [causation_id]     nvarchar(128)  NULL,
    [status]           tinyint        NOT NULL CONSTRAINT [df_outbox_status] DEFAULT 0,
    [attempt_count]    int            NOT NULL CONSTRAINT [df_outbox_attempts] DEFAULT 0,
    [created_at]       datetime2(3)   NOT NULL,
    [scheduled_at]     datetime2(3)   NOT NULL,
    [available_at]     datetime2(3)   NOT NULL,
    [lock_token]       uniqueidentifier NULL,
    [locked_by]        nvarchar(256)  NULL,
    [last_attempt_at]  datetime2(3)   NULL,
    [delivered_at]     datetime2(3)   NULL,
    [dead_lettered_at] datetime2(3)   NULL,
    [last_error]       nvarchar(2000) NULL,
    CONSTRAINT [pk_outbox_messages] PRIMARY KEY CLUSTERED ([sequence]),
    CONSTRAINT [ck_outbox_status] CHECK ([status] BETWEEN 0 AND 3)
);
CREATE UNIQUE NONCLUSTERED INDEX [ux_outbox_messages_id] ON [dbrelay].[outbox_messages] ([id]);
CREATE NONCLUSTERED INDEX [ix_outbox_messages_claim] ON [dbrelay].[outbox_messages] ([available_at]) INCLUDE ([status]) WHERE [status] IN (0, 1);
```

Claim: **two-phase within one short transaction** (revised during M8, see ADR 0002):

```sql
SET XACT_ABORT ON; BEGIN TRAN;
INSERT INTO @_claimed ([sequence])
SELECT TOP (@batch_size) [sequence]
FROM [dbrelay].[outbox_messages] WITH (UPDLOCK, READPAST, ROWLOCK, INDEX([ix_outbox_messages_claim]))
WHERE [status] IN (0, 1) AND [available_at] <= @_now
ORDER BY [available_at];

UPDATE m SET [status] = 1, [lock_token] = @lock_token, [locked_by] = @worker_id,
    [attempt_count] = m.[attempt_count] + 1, [last_attempt_at] = @_now, [available_at] = <@_now + lease>
OUTPUT inserted.<columns>, deleted.[available_at]
FROM [dbrelay].[outbox_messages] AS m WITH (ROWLOCK, READPAST, FORCESEEK)
JOIN @_claimed c ON c.[sequence] = m.[sequence]
WHERE m.[status] IN (0, 1) AND m.[available_at] <= @_now;
COMMIT;
```

The originally proposed single-statement `UPDATE`-over-CTE was measured with actual plans and **rejected**. SQL Server adds an eager spool for Halloween protection because the update changes the key of the index being read, so every due row was read and U-locked before `TOP` applied. In the test that meant 500 rows locked to claim 100, a concurrent claimer starved, and with a large backlog the locks could escalate to a table lock. The two-phase form reads and locks exactly the batch.

SQL Server-specific choices:
- Clustered on an identity column, because GUIDs, including v7, don't sort sequentially in `uniqueidentifier` ordering.
- `datetime2` in UTC from `SYSUTCDATETIME()`.
- The batch is capped at 1000 to stay well under the 5000-lock escalation threshold.
- The engineer must verify `READPAST` behaviour with `READ_COMMITTED_SNAPSHOT ON`, which is the Azure SQL default.

### 8.3 Schema management and security

- **Explicit initialization only.** `EnsureCreatedAsync()` runs idempotent `IF NOT EXISTS` DDL and never drops or alters existing columns. The same SQL ships as an embedded resource and in `docs/` for DBA-run migrations. A future schema version bump will ship as an explicit, additive script recorded in `schema_version`.
- Schema/table names are validated (`^[A-Za-z_][A-Za-z0-9_]{0,62}$`) and quoted. Everything else is a parameter.
- No CLR type is ever resolved from `message_type`. Typed reads require the caller to name `T`.

---

## 9. Serialization

`IMessageSerializer` (`ContentType`, `Serialize<T>`, `Deserialize<T>`). The default is System.Text.Json with configurable `JsonSerializerOptions`, and it works with source-generated `JsonSerializerContext`. Message type names come from `MapMessageType<T>("name")`, then `[OutboxMessageType("name")]`, then `typeof(T).FullName`. We'll document that renaming a class changes the fallback name. Payloads over `MaxPayloadBytes` are rejected at enqueue.

---

## 10. Observability

- **Traces:** `ActivitySource("DbRelay")`. Enqueue captures W3C `traceparent`/`tracestate` into headers. Delivery starts a `Consumer`-kind activity linked to the producer context, tagged with `messaging.message.id`, `messaging.destination.name`, `dbrelay.attempt`, following OTel messaging semantic conventions where they apply.
- **Metrics:** `Meter("DbRelay")`:
  - counters: `dbrelay.messages.enqueued`, `.delivered`, `.retried`, `.dead_lettered`, `dbrelay.lease.lost`
  - histograms: `dbrelay.delivery.duration`, `dbrelay.claim.duration`, `dbrelay.claim.batch_size`
  - observable gauges: `dbrelay.messages.pending`, `dbrelay.messages.dead_lettered`, `dbrelay.messages.oldest_pending_age`. These come from an opt-in periodic statistics query (default every 30s) so scrapes never hit the database.
- **Health check:** `services.AddHealthChecks().AddDbRelay(o => { o.MaxOldestPendingAge = ...; o.MaxDeadLettered = ...; })`. It checks database reachability, dispatcher heartbeat and the optional thresholds.
- **Logging:** source-generated `LoggerMessage`. Logs include the message id, type and attempt, never payloads or connection strings. Handler exception text is truncated to 2000 characters before it's persisted.

---

## 11. Testing strategy (summary)

- **Unit** (`DbRelay.Tests`): options validation, retry math with a fake `TimeProvider`/random, state transitions, serializer, identifier validation, and dispatcher behaviour against an in-memory fake store.
- **Provider** (`DbRelay.PostgreSql.Tests`, `DbRelay.SqlServer.Tests`): real databases via Testcontainers, falling back to `DBRELAY_TEST_*` connection strings. Covers schema creation idempotency, commit/rollback atomicity with Dapper business writes, transaction validation, claim exclusivity under parallel claimers, lock-token guards, lease expiry, and cleanup.
- **Integration** (`DbRelay.IntegrationTests`): full host with multiple dispatchers against one database, plus a **real process-kill crash test**. A child worker process is killed mid-delivery, and the test checks the message is redelivered after lease expiry. Also a database-interruption recovery test.
- **Shared provider conformance suite**, so both providers must pass identical behavioural tests (parity by construction).

---

## 12. Decisions requiring approval

| ID | Decision | Recommendation |
|---|---|---|
| D1 | Target framework | `net10.0` only (LTS); revisit `net11.0` when an 11-only API is needed |
| D2 | Dapper inside providers | No: plain ADO.NET internally; Dapper used in tests/samples to prove compatibility |
| D3 | Ambient transaction support | Not in v0.1. Explicit transaction parameter; standalone path named `EnqueueStandaloneAsync` |
| D4 | PostgreSQL payload column | `bytea` (byte-faithful, any content type); `jsonb` for headers only |
| D5 | Dead letters storage | Same table, `status = 3` (simple requeue, no cross-table moves) |
| D6 | Attempt history | `attempt_count` + `last_error` + `last_attempt_at` in v0.1; per-attempt history table deferred (logs/traces carry full history) |
| D7 | Package split | Keep `DbRelay.Abstractions` (zero-dependency contracts) separate from `DbRelay` (engine) |
| D8 | First version number | `0.1.0` (pre-1.0 signals API may still change based on feedback); 1.0 after real-world use |
| D9 | License | MIT |
| D10 | Supported databases | PostgreSQL 15+ (14 reached EOL in Nov 2026), SQL Server 2019+ / Azure SQL |
