# Troubleshooting

Start with three questions: **what does the health check say**, **what does `IOutboxAdmin.GetStatisticsAsync` say**, and
**what do the DbRelay logs say** (category `DbRelay.*`, event IDs below).

## DbRelay log events

| ID | Level | Meaning |
|---|---|---|
| 1000 / 1001 | Information | Dispatcher started (with batch size, concurrency and lease) / stopped |
| 1002 | Error | A claim query failed; the dispatcher backs off and retries |
| 1003 | Debug | Messages claimed |
| 1004 | Debug | Message delivered |
| 1005 | Warning | Delivery failed; retry scheduled (attempt n/max, reason, delay) |
| 1006 | Error | Message dead-lettered (attempt, reason) |
| 1007 | Warning | **Lease lost**: another worker reclaimed the message before this outcome was recorded |
| 1008 | Warning | Recording an outcome failed; retrying |
| 1009 | Error | Recording an outcome failed for good; the message will be delivered again after its lease |
| 1010 | Debug | Claimed messages released without delivery (shutdown, or not enough lease left) |
| 1011 | Error | Unexpected error while processing a message |
| 1012 | Warning | Releasing a message failed; it becomes claimable when its lease expires |
| 1100 | Information | Cleanup deleted delivered messages |
| 1101 | Warning | A maintenance task failed |

## Inspecting messages

```csharp
OutboxStatistics stats = await admin.GetStatisticsAsync(ct);
// stats.Pending, stats.Processing, stats.Delivered, stats.DeadLettered, stats.OldestDueAge

OutboxMessageDetails? message = await admin.GetAsync(messageId, ct);
// message.Status, AttemptCount, AvailableAt, LastAttemptAt, LastError, LockedBy

IReadOnlyList<OutboxMessageDetails> deadLetters = await admin.QueryAsync(
    new OutboxQuery { Status = OutboxMessageStatus.DeadLettered, MessageType = "orders.created", Take = 50 }, ct);
```

`samples/PostgreSql.Sample` exposes these as `GET /outbox/stats` and `GET /outbox/messages/{id}`. Do not expose
`IOutboxAdmin` publicly without authorization: `OutboxMessageDetails` includes the payload.

## Health check states

| Status | Cause | What to check |
|---|---|---|
| **Healthy** | The database is reachable, the dispatcher is polling or delivering, and no threshold is exceeded | - |
| **Unhealthy** (or your `failureStatus`) | The health query (`SELECT` on the outbox table) failed: the database is unreachable, the credentials are wrong, or **the table does not exist** (did you call `EnsureCreatedAsync` or run the script?) | The exception attached to the health result |
| **Degraded**: "the dispatcher is not running" | The hosted service has not started yet or has stopped. Seen briefly at startup and shutdown | That the host is running; event 1000/1001 |
| **Degraded**: "the dispatcher's last N poll(s) failed" | Claim queries are failing while the health query still works. Typical causes: permissions (`UPDATE` missing), a dropped or renamed DbRelay index on SQL Server (ADR 0002), lock timeouts, or a database under heavy load | Event 1002 and its exception; `dbrelay.dispatcher.poll_failures` |
| **Degraded**: "no progress since ..." | No successful poll and no finished delivery for max(1 min, 10 × `PollingInterval`, 2 × `DeliveryTimeout`). Usually handlers hang and ignore their cancellation token | Handler code; thread pool starvation |
| **Degraded**: "the oldest due message has waited ..." | `MaxOldestDueAge` exceeded: delivery is falling behind or stalled | `OldestDueAge` trend, handler latency, `MaxConcurrency` |
| **Degraded**: "N messages are dead-lettered" | `MaxDeadLettered` exceeded | Dead letters (below) |

The dispatcher entries apply only to applications that run the dispatcher. With `DisableDispatcher()`, the health check covers
database reachability and the optional thresholds only. Degraded during a short database blip is normal: the dispatcher
recovers by itself, with backoff up to `MaxErrorBackoff`.

## Messages are not being delivered

Work through these in order:

1. **Was the transaction committed?** Uncommitted and rolled-back messages do not exist for the dispatcher. `GetAsync(id)`
   returns `null` for them.
2. **Is a dispatcher running** against the same database, schema and table name? Look for event 1000. Applications with
   `DisableDispatcher()` never deliver. Check that the enqueuing and delivering applications use the same `Schema` and
   `TableName`.
3. **Is the message scheduled for later?** With `Delay` or `DeliverAt`, `AvailableAt` is in the future. Retries also move
   `AvailableAt` forward: check `AttemptCount` and `LastError`.
4. **Is it Processing?** Then a worker holds a lease until `AvailableAt`. If that worker crashed, the message becomes claimable
   again automatically when the lease expires (up to `LeaseDuration`, default 2 minutes). It is **not** stuck forever, and you
   should not reset it by hand.
5. **Is the dispatcher saturated?** If `OldestDueAge` keeps growing while deliveries succeed, the handlers are slower than the
   arrival rate. Raise `MaxConcurrency`, run more instances, or speed up the handler.
6. **Is `dbrelay.messages.released` rising?** Claimed messages are being released because too little lease time was left to
   start them. Make `LeaseDuration` longer relative to `DeliveryTimeout`, lower `BatchSize`, or raise `MaxConcurrency`.

## Lease lost (event 1007, `dbrelay.lease.lost`)

A worker finished a delivery, but by the time it recorded the outcome, another worker had already reclaimed the message
because the lease had expired. DbRelay discards the late outcome; it never overwrites the newer owner's state. **The message
was, or will be, delivered more than once.** This is at-least-once delivery working as designed, but it should be rare.

Common causes:

- A handler that ignores its `CancellationToken` and runs past `LeaseDuration`. `DeliveryTimeout` only cancels the token; it
  cannot stop code that does not observe it.
- Long garbage-collection pauses or thread-pool starvation in the worker.
- A `LeaseDuration` that is too short for real delivery times. Validation requires at least 2 × `DeliveryTimeout`; give more
  headroom if your deliveries vary widely.

## Outcome could not be recorded (events 1008 and 1009)

The handler finished, but writing the result to the database failed three times (about 0.6 s). The message stays Processing
and is delivered again after its lease. Look for a database outage or failover at that time. Expect duplicates, and make sure
consumers deduplicate on `OutboxMessage.Id`.

## Dead letters

A message is dead-lettered when:

- the handler returned `DeliveryResult.DeadLetter(reason)`;
- the handler threw an exception that `RetryOptions.IsPermanentFailure` classifies as permanent;
- the last allowed attempt failed transiently (`last_error` starts with `Max attempts (N) reached. Last error:`);
- the message was claimed more than `MaxAttempts` times without any outcome being recorded, typically because it crashes or
  hangs its worker. Then `last_error` is
  `Exceeded the maximum of N attempts without a recorded outcome (possible worker crash loop).` and the handler is **not** called
  on that final claim.

`last_error` holds the most recent failure (exception type and message, or your reason, at most 2,000 characters). It is kept
even after a later attempt succeeds, so a Delivered message can show the error of an earlier attempt. If the reason itself could
not be stored, you will see `DbRelay could not store the original ... reason; see the application logs for details.`, and the
original is in the logs (event 1005 or 1006).

Dead letters stay in the outbox table (status 3) until you act. Retention cleanup never deletes them.

## Requeueing

Fix the cause first: deploy the handler fix, restore the downstream system, or correct the configuration. Then:

```csharp
// Requeue one dead-lettered message, or up to 100 of one type (oldest first). Attempts restart from 1.
bool requeued = await admin.RequeueAsync(messageId, ct);
int count = await admin.RequeueDeadLetteredAsync("orders.created", maxCount: 100, ct);
```

- Only **dead-lettered** messages are affected. `RequeueAsync` returns `false` for any other status, so it is safe to call twice.
- The message becomes Pending and due immediately, with `attempt_count = 0`. Its next delivery is attempt 1, and it has the
  full `MaxAttempts` budget again.
- Pass `null` as the message type to requeue all types. `maxCount` is 1 to 10,000 per call; loop for more.
- The message keeps its ID, so receivers that already processed it can recognise it.

Requeue through `IOutboxAdmin`, not with a hand-written `UPDATE`. The admin API only touches rows that are still
dead-lettered (`status = 3`), resets the attempt and lease fields consistently, and, for bulk requeue, skips rows locked by
another session. It is safe while dispatchers run.

## Deleting data

- Delivered messages are deleted automatically after `MaintenanceOptions.DeliveredRetention` (default 1 day). To purge now:
  `await admin.PurgeDeliveredAsync(TimeSpan.FromHours(1), ct)`.
- There is no API to delete dead letters. If you decide a dead letter must never be delivered, delete it deliberately with SQL
  (`DELETE ... WHERE id = @id AND status = 3`).

## Startup errors

| Error | Fix |
|---|---|
| `No DbRelay storage provider is configured.` | Call `relay.UsePostgreSql(...)` or `relay.UseSqlServer(...)` inside `AddDbRelay`. |
| `No delivery handler is configured.` | Call `relay.UseDeliveryHandler<T>()`, or `relay.DisableDispatcher()` for enqueue-only applications. |
| `AddDbRelay has already been called` | Register DbRelay once per service collection. |
| `OptionsValidationException: DbRelay: DispatcherOptions.LeaseDuration (...) must be at least twice DeliveryTimeout` | Raise `LeaseDuration` or lower `DeliveryTimeout`. Every option and its range is in [configuration.md](configuration.md). |
| `'Schema' must be 1 to 63 characters long, start with a letter or underscore ...` | Use a plain identifier; DbRelay does not accept quoted or dotted names. |

## Enqueue errors

| Error | Fix |
|---|---|
| `The transaction has already been committed or rolled back` | Enqueue before `CommitAsync`. |
| `The transaction belongs to a different connection than the one supplied` | Pass the connection that began the transaction, or use the `EnqueueAsync(transaction, message)` overload. |
| `The connection must be open` | Open the connection first; DbRelay never opens caller connections. |
| `DbRelay is configured for SQL Server and requires a Microsoft.Data.SqlClient.SqlConnection` (or the PostgreSQL equivalent) | Use the configured provider's types. `System.Data.SqlClient` and profiling wrappers are rejected (K10). |
| Unique-constraint violation on `id` | An `EnqueueOptions.MessageId` was used twice. Roll back; the message was already enqueued. |
| `The serialized payload is N bytes, which exceeds OutboxOptions.MaxPayloadBytes` | Store large data elsewhere and enqueue a reference, or raise `MaxPayloadBytes` (up to 256 MiB). |
| SQL Server Msg 1934 (`QUOTED_IDENTIFIER`) on insert | The connection has `QUOTED_IDENTIFIER` or `ANSI_NULLS` OFF. See [sqlserver.md](sqlserver.md#ansi_nulls-quoted_identifier-and-sqlcmd--i). |
