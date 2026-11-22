# Architecture

This document describes DbRelay **as built** in 0.1.0. The original design is in
[architecture-proposal.md](architecture-proposal.md), and the binding decisions are in [decisions/](decisions/). Where the
two differ, this document and the source code are correct. The main differences are listed at the end.

## Packages

| Package | Contents | Dependencies |
|---|---|---|
| `DbRelay.Abstractions` | Application-facing contracts: `IOutbox`, `OutboxExtensions`, `EnqueueOptions`, `RawOutboxMessage`, `OutboxMessage`, `IMessageDeliveryHandler`, `DeliveryResult`, `IMessageSerializer`, `IOutboxAdmin`, `IOutboxSchemaManager`, `OutboxMessageTypeAttribute` | none |
| `DbRelay` | Engine: `AddDbRelay`, `DbRelayBuilder`, options and validators, the System.Text.Json serializer, type-name registry, `Outbox`, `OutboxAdmin`, `OutboxDispatcher`, `OutboxMaintenanceService`, retry policy, health check, diagnostics, and the storage SPI (`IOutboxStore`, `OutboxRecord`, `SqlIdentifier`) | `Microsoft.Extensions.*` abstractions, Options, Logging.Abstractions, Diagnostics.HealthChecks |
| `DbRelay.PostgreSql` | `UsePostgreSql`, `PostgreSqlOutboxStore`, schema manager, embedded script | DbRelay, Npgsql |
| `DbRelay.SqlServer` | `UseSqlServer`, `SqlServerOutboxStore`, schema manager, embedded script | DbRelay, Microsoft.Data.SqlClient |

The libraries target `net8.0;net9.0;net10.0;net11.0` (ADR 0001, D1), and `Microsoft.Extensions.*` dependencies are pinned per
target framework. Providers use plain ADO.NET internally (D2), not Dapper. DbRelay has no OpenTelemetry, Polly or
Newtonsoft dependency.

## Components

```
 your code ──EnqueueAsync(tx, msg)──► Outbox ──InsertAsync(your conn, your tx)──► outbox table
                                                                                     │
           ┌─────────────────────────────── OutboxDispatcher (BackgroundService) ◄───┘ ClaimAsync (autocommit)
           │  per message: new DI scope ─► IMessageDeliveryHandler.DeliverAsync(msg, token)
           │  outcome ─► MarkDelivered / ScheduleRetry / MarkDeadLettered / Release   (guarded by lock token)
           │
 OutboxMaintenanceService ─► DeleteDelivered (retention), GetStatistics (gauges)
 OutboxAdmin ─► Get / Query / Statistics / Requeue / Purge
 DbRelayHealthCheck ─► CheckHealth (SELECT on the table) + dispatcher state + optional thresholds
```

- **`Outbox`** validates the caller's resources and the message, serializes it, builds an `OutboxRecord` (a UUIDv7 ID unless
  you supply one, plus the W3C trace context from `Activity.Current`) and calls the store with **your** connection and
  transaction. It never takes ownership of them.
- **`IOutboxStore`** is the storage SPI. A provider implements it with fixed, parameterized SQL. Its behaviour is pinned by the
  shared conformance suite (`tests/DbRelay.Storage.Conformance`), which both providers pass.
- **`OutboxDispatcher`** is the delivery loop (below).
- **`OutboxMaintenanceService`** deletes delivered rows older than the retention, in batches, and refreshes the statistics behind
  the gauges.

## The dispatcher loop

1. Wait until at least one concurrency slot is free. It never claims messages it cannot start soon.
2. **Claim** up to `BatchSize` due messages in one short autocommitted statement or batch. Each claim gets a new random
   **lock token**, `attempt_count + 1`, `status = Processing` and `available_at = db_now + LeaseDuration`.
3. For each claimed message, wait for a slot. If the remaining lease (measured locally and conservatively from before the claim)
   is shorter than `DeliveryTimeout` plus a safety margin, **release** the rest of the batch instead of starting it.
4. Run the handler in a **new DI scope**, with a token that fires after `DeliveryTimeout` or when the host's shutdown
   timeout expires. **No database transaction or connection is held while the handler runs.**
5. Record the outcome with an update guarded by `id AND lock_token` (and `status = Processing` on SQL Server). Zero rows
   affected means the lease was lost to another worker. That is logged (event 1007) and counted (`dbrelay.lease.lost`), and
   the other worker's state is left alone. Outcome writes are retried 3 times (200 ms, then 400 ms). If a reason cannot be
   stored, a fixed fallback reason is recorded instead, so the message is never left in Processing.
6. When every message of the batch has **started**, claim again: immediately if the batch was full, otherwise after
   `PollingInterval` ±10%.

Database errors during claims never stop the loop. It backs off exponentially from 1 s to `MaxErrorBackoff`, the health
check reports Degraded, and it recovers by itself.

**Shutdown.** On `StopAsync` the dispatcher stops claiming, releases claimed-but-unstarted messages (status Pending,
`attempt_count` restored), and lets in-flight deliveries finish and record their outcomes. If the host's shutdown timeout
expires first, in-flight handlers are cancelled. Their messages are returned to Pending **with the attempt still counted**,
because the handler ran and may have had its side effect. Without this, a handler that always outlasts the shutdown window
would never reach dead-letter. These messages may already have been delivered, which is one of the documented sources of
duplicates. A claim that completes while shutdown is starting is not cancelled midway: its messages are released unstarted.

**Handlers that ignore cancellation.** If a handler is still running after `DeliveryTimeout` plus a grace period of
min(5 s, timeout ÷ 2), which is always inside the lease, the dispatcher stops waiting. It frees the concurrency slot, schedules
a retry with its lock token, and leaves the orphaned task to finish on its own. The orphan's result is never recorded.

## State machine

`status`: `0 Pending`, `1 Processing`, `2 Delivered`, `3 DeadLettered`.

| From | Event | To | Guard | Effect |
|---|---|---|---|---|
| (new) | Enqueue in your transaction | Pending | - | `available_at = DeliverAt ?? db_now + Delay ?? db_now`, `attempt_count = 0` |
| Pending, or Processing with an expired lease | Claim | Processing | `available_at <= db_now`, row not locked | new `lock_token`, `attempt_count + 1`, `last_attempt_at = db_now`, `available_at = db_now + lease` |
| Processing | Handler returns `Delivered` | Delivered | lock token | `delivered_at = db_now`, token cleared |
| Processing | Transient failure, attempts < max | Pending | lock token | `available_at = db_now + delay`, `last_error` set, token cleared |
| Processing | `DeadLetter`, permanent exception, or transient failure on the last attempt | DeadLettered | lock token | `dead_lettered_at = db_now`, `last_error` set |
| Processing | Claimed with `attempt_count > MaxAttempts` | DeadLettered | lock token | Handler **not** called; crash-loop reason |
| Processing | Claimed but not started: shutdown, or not enough lease left to start | Pending | lock token | `available_at = db_now`, `attempt_count - 1`, `locked_by` cleared |
| Processing | In-flight handler cancelled by the shutdown timeout, or abandoned after ignoring cancellation | Pending | lock token | `available_at = db_now` (shutdown) or `db_now + backoff` (abandoned), **attempt kept**, `last_error` set |
| DeadLettered | `RequeueAsync` / `RequeueDeadLetteredAsync` | Pending | `status = 3` | `available_at = db_now`, `attempt_count = 0` |
| Delivered | Retention cleanup / `PurgeDeliveredAsync` | deleted | `delivered_at` older than retention | batched delete |

A single `available_at` column holds both the scheduled time and the lease expiry, like an SQS visibility timeout. The claim
predicate is therefore one indexable range, `status IN (0, 1) AND available_at <= now`. Crash recovery needs no separate
sweeper: an expired lease simply makes the row due again.

## Claim SQL

### PostgreSQL: one statement

```sql
WITH next AS MATERIALIZED (
    SELECT id, available_at
    FROM "dbrelay"."outbox_messages"
    WHERE status IN (0, 1) AND available_at <= now()
    ORDER BY available_at
    LIMIT $1
    FOR UPDATE SKIP LOCKED
), claimed AS (
    UPDATE "dbrelay"."outbox_messages" AS m
    SET status = 1, lock_token = $2, locked_by = $3, attempt_count = m.attempt_count + 1,
        last_attempt_at = now(), available_at = now() + $4
    FROM next
    WHERE m.id = next.id
    RETURNING m.<columns>, next.available_at AS claim_order
)
SELECT <columns> FROM claimed ORDER BY claim_order, id
```

The CTE is `MATERIALIZED`, so the locking subquery runs exactly once. An inlined `LIMIT ... FOR UPDATE SKIP LOCKED` subquery
can be re-scanned and lock more than `LIMIT` rows (bug B1, found by the conformance suite).

### SQL Server: two phases in one short transaction (ADR 0002)

```sql
SET NOCOUNT ON; SET XACT_ABORT ON;
DECLARE @_now datetime2(3) = <SYSUTCDATETIME() truncated to ms>;
DECLARE @_claimed TABLE ([sequence] bigint NOT NULL PRIMARY KEY);
BEGIN TRANSACTION;
INSERT INTO @_claimed ([sequence])
SELECT TOP (@batch_size) [sequence]
FROM [dbrelay].[outbox_messages] WITH (UPDLOCK, READPAST, ROWLOCK, INDEX([ix_outbox_messages_claim]))
WHERE [status] IN (0, 1) AND [available_at] <= @_now
ORDER BY [available_at];

UPDATE m
SET [status] = 1, [lock_token] = @lock_token, [locked_by] = @worker_id, [attempt_count] = m.[attempt_count] + 1,
    [last_attempt_at] = @_now, [available_at] = <@_now + @lease_ms>
OUTPUT inserted.<columns>, deleted.[available_at], inserted.[sequence]
FROM [dbrelay].[outbox_messages] AS m WITH (ROWLOCK, READPAST, FORCESEEK)
INNER JOIN @_claimed AS c ON c.[sequence] = m.[sequence]
WHERE m.[status] IN (0, 1) AND m.[available_at] <= @_now;
COMMIT TRANSACTION;
```

Why two phases: a single `UPDATE` over a `TOP (n)` CTE was measured on SQL Server 2022 and rejected. Because the update
changes `available_at`, the key of the claim index, SQL Server adds an eager spool for Halloween protection. Every due row
was then read and U-locked before `TOP` applied: 500 rows locked to claim 100, so a concurrent claimer got nothing, and a
large backlog risked lock escalation to a table lock. The two-phase form reads and locks exactly the batch. The `INDEX` and
`FORCESEEK` hints pin that access path. The status predicates are literals so that the filtered indexes match. Rows are
re-sorted on the client by the pre-update `available_at`. Bulk requeue uses the same pattern. See
[sqlserver.md](sqlserver.md) for the operational consequences.

The providers deliberately differ in SQL and are required to behave identically: the same conformance suite runs against both,
and on SQL Server it runs with `READ_COMMITTED_SNAPSHOT` both off and on.

## Time

Every lease, schedule, retry and retention comparison uses the **database clock**: `now()` on PostgreSQL, and
`SYSUTCDATETIME()` truncated to milliseconds on SQL Server. Durations are computed in the application but applied by the
database (`now() + @delay`), so application server clock skew cannot make a lease expire early or a retry fire early. The
dispatcher's local lease check (step 3) uses a monotonic timer started **before** the claim, which errs on the safe side.

## Serialization and types

The default serializer is System.Text.Json (camelCase unless you pass `JsonSerializerOptions`). The logical type name comes
from `MapMessageType<T>`, then `[OutboxMessageType]`, then `typeof(T).FullName`. The name is a label for handlers and
operators: **DbRelay never creates a CLR type from a stored string.** Handlers name the type they want
(`serializer.Deserialize<T>(payload)`). Payloads are stored byte for byte (`bytea` / `varbinary(max)`, ADR D4), so raw
protobuf, Avro or signed JSON survives unchanged.

## Observability

There is one `ActivitySource` and one `Meter`, both named `DbRelay`. Enqueue starts a Producer activity
(`DbRelay enqueue`) and stores `traceparent` and `tracestate` in the headers. Delivery starts a Consumer activity
(`DbRelay deliver`) whose parent is the stored context, tagged `messaging.system=dbrelay`, `messaging.message.id`,
`dbrelay.message.type`, `dbrelay.attempt`, and optionally `messaging.destination.name` and
`messaging.message.conversation_id`. The metric list is in the [README](../README.md#observability). Logs use
source-generated `LoggerMessage` with event IDs 1000 to 1012 and 1100 to 1101.

## Security properties

- All values are parameters. Schema and table names are validated by `SqlIdentifier` (`^[A-Za-z_][A-Za-z0-9_]{0,62}$`) and
  quoted.
- Payloads and connection strings are never logged.
- Stored error text is the exception type and message only (no stack trace). It is sanitized (NUL and unpaired surrogates are
  replaced with U+FFFD) and truncated to 2,000 characters without splitting a surrogate pair.
- Metadata strings containing NUL (destination, correlation and causation IDs, headers, content type) are rejected at enqueue on
  both providers, for parity with PostgreSQL `text`. Message type names reject all control characters.

## Differences from the original proposal

| Proposal | As built |
|---|---|
| Target `net10.0` only | `net8.0;net9.0;net10.0;net11.0` (D1, amended by the owner) |
| `DeliveryResult` as a `readonly record struct` | A sealed class with `Outcome`, `Reason` and `RetryAfter` |
| `RequeueAsync(id, resetAttempts)` | `RequeueAsync(id)` always resets attempts. Bulk requeue is `RequeueDeadLetteredAsync(string? messageType, int maxCount)` |
| `QueryAsync` returns `PagedResult<T>` | Returns `IReadOnlyList<OutboxMessageDetails>`; page with `OutboxQuery.Skip` and `Take` |
| `PurgeDeliveredAsync(DateTimeOffset)` | `PurgeDeliveredAsync(TimeSpan olderThan)` on the database clock (S3) |
| `AddDbRelay(IConfiguration, ...)` binding | Not implemented; options are set in code |
| `EnqueueOptions.DeliverAt` only | `DeliverAt` or `Delay` (database clock), mutually exclusive (S2) |
| SQL Server single-statement claim | Two-phase claim (ADR 0002) |
| Gauge `dbrelay.messages.dead_lettered` | `dbrelay.messages.dead_lettered.current` (the counter keeps the shorter name); `dbrelay.messages.processing`, `dbrelay.messages.released` and `dbrelay.dispatcher.poll_failures` were added |
