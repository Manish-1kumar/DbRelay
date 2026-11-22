# Migrating from a hand-rolled outbox

Many teams on Dapper or ADO.NET already have an outbox: an `outbox` table, an `INSERT` in the business transaction, and a
`BackgroundService` that polls, publishes and marks rows as sent. It usually works in development. In production, it tends
to fail in the same handful of ways. This guide lists those failure modes, shows how DbRelay handles each one, and ends with
a migration plan.

## The classic bugs

### 1. Holding a transaction open across the network call

```csharp
// Typical hand-rolled loop (an anti-pattern for illustration, not DbRelay code)
await using var tx = await connection.BeginTransactionAsync();
var rows = await connection.QueryAsync<Row>("SELECT ... FOR UPDATE SKIP LOCKED LIMIT 100", transaction: tx);
foreach (var row in rows) await broker.PublishAsync(row);        // seconds, under row locks
await connection.ExecuteAsync("UPDATE outbox SET sent = true ...", transaction: tx);
await tx.CommitAsync();
```

A slow broker holds locks and an open transaction for the whole batch. On PostgreSQL that blocks vacuum and bloats the table.
On SQL Server it blocks other readers and risks lock escalation. One poison message then rolls back the whole batch.

**DbRelay:** the claim is one short autocommitted statement that marks rows Processing with a lease. **No transaction or
connection is held while handlers run**, and each outcome is its own short update. Proven by
`GracefulShutdownTests.StopAsync_finishes_in_flight_deliveries_and_releases_the_rest_with_attempts_restored`, which checks that
no transaction or lock exists during delivery.

### 2. No fencing: a stale worker overwrites newer state

Without a transaction, hand-rolled loops claim with `UPDATE ... SET locked_until = now() + 1 minute` and finish with
`UPDATE ... SET sent = true WHERE id = @id`. Suppose worker A stalls past its lease, worker B reclaims the row, and B's
delivery fails and schedules a retry. A then wakes up and marks the row sent, erasing B's state.

**DbRelay:** every claim gets a unique lock token, and every later update is `WHERE id = @id AND lock_token = @token`. A stale
worker's update affects 0 rows. It is logged as "lease lost" (event 1007) and changes nothing. Proven by
`StaleWorkerTests.Stale_lock_token_cannot_overwrite_while_the_new_owner_delivers_or_after_it_delivered` and
`StaleWorkerTests.Handler_that_ignores_its_timeout_is_abandoned_and_its_late_result_never_overwrites_the_redelivery`.

### 3. Crash loops that never end

The attempt counter is incremented **after** a failed publish. A message that crashes the process (out of memory, a native
crash, a stack overflow in serialization) never reaches the increment, so it is retried forever and takes the worker down
every time.

**DbRelay:** attempts are counted **at claim time**. A message that keeps killing its worker is claimed one more time after
`MaxAttempts` and dead-lettered **without** calling the handler. Proven by
`CrashRecoveryTests.Message_that_kills_its_worker_every_time_ends_dead_lettered_after_MaxAttempts`, which uses a real child
process that kills itself.

### 4. Comparing application clocks across servers

`WHERE locked_until < @now`, with `@now = DateTime.UtcNow` from each app server. A server whose clock runs 30 s fast steals
leases 30 s early. A slow one leaves messages waiting.

**DbRelay:** every lease, schedule and retry comparison uses the **database clock**. Delays are computed in the application
but applied as `now() + @delay` in SQL. Proven on the database clock by
`CrashRecoveryTests.Killed_worker_process_message_is_redelivered_after_lease_expiry` and
`RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds`.

### 5. Retries in lockstep

Fixed-interval retries, or exponential backoff without jitter. After a broker outage, thousands of messages retry at the same
instant, again and again.

**DbRelay:** exponential backoff with **equal jitter**: a random delay in `[d/2, d]`. Retries keep a floor and spread out.
`Retry-After` from the handler is honoured and capped. Proven by `RetryPolicyTests.*` and
`RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds`.

### 6. No dead-letter state, and no way to requeue

Failed rows either retry forever or are flagged `failed = true`, and "requeue" means someone runs an `UPDATE` in production
by hand.

**DbRelay:** permanent failures and exhausted retries move to `DeadLettered` with the reason in `last_error`. They are never
deleted automatically. `IOutboxAdmin.RequeueAsync` and `RequeueDeadLetteredAsync` bring them back with a fresh attempt budget.
Proven by `RetryScheduleTests.Exhausted_message_is_dead_lettered_and_manual_requeue_redelivers_it_from_attempt_one`.

### 7. Two workers, one message

`SELECT ... WHERE sent = false LIMIT 100`, then publish. Two instances read the same rows and both publish. Adding a global
distributed lock fixes the duplicates but leaves only one active worker.

**DbRelay:** claims use `FOR UPDATE SKIP LOCKED` on PostgreSQL and `UPDLOCK, READPAST, ROWLOCK` on SQL Server, so every
instance works in parallel on different rows. Proven by the conformance test `Concurrent_claimers_never_receive_the_same_message`
and by `MultiWorkerTests.Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run` (4 hosts, 2,000 messages, 0
duplicates in a crash-free run).

### 8. The outbox write is not really in the business transaction

The outbox insert uses a different connection, or `TransactionScope` silently escalates to a distributed transaction, or the
insert happens after `Commit()`.

**DbRelay:** `EnqueueAsync(transaction, message)` uses exactly your transaction and its connection, and it validates the pairing:
a completed transaction, a foreign connection or a closed connection is rejected with a clear message. Proven by the conformance
`Enqueue_*` tests and by `TransactionAtomicityTests.Only_committed_business_transactions_are_ever_delivered`, where 200 random
commits and rollbacks produce a delivered set equal to the committed set.

### 9. Shutdown loses or strands work

On deploy, the process exits mid-batch. Rows stay "locked" until a manual fix, or in-flight publishes are abandoned silently.

**DbRelay:** on `StopAsync` it stops claiming, releases unstarted claims immediately (with their attempt count restored), lets
in-flight deliveries finish and record their outcomes, and returns them (with the attempt counted) if the host's shutdown timeout expires. Proven by
`GracefulShutdownTests.*`.

### 10. Secrets and payloads in logs

`_logger.LogError(ex, "Failed to publish {Message}", JsonSerializer.Serialize(row))`.

**DbRelay:** logs carry the message ID, type and attempt, never payloads or connection strings. Stored error text is the exception
type and message only, truncated to 2,000 characters. Proven by
`SecurityTests.Payloads_and_connection_strings_never_appear_in_logs_while_delivering_retrying_and_dead_lettering`. Your own
exception messages are still stored, so keep secrets out of them.

### 11. Unbounded table growth

Nobody deletes sent rows, and after a year the poll query scans millions of rows.

**DbRelay:** delivered rows are deleted in batches after `DeliveredRetention` (default 1 day), with row-skipping locks so that
several instances can clean up safely. Claims use a partial or filtered index that contains only claimable rows.

### 12. "Exactly once" in the design document

No outbox can deliver exactly once to an external system: a crash between "publish" and "mark sent" always allows a duplicate.

**DbRelay** says so plainly. It delivers at least once, and `OutboxMessage.Id` is a stable idempotency key for consumers.
See [reliability.md](reliability.md).

## What you give up

Being honest about the trade-offs:

- **One handler per application.** If your hand-rolled outbox has several publishers, route inside one handler on
  `MessageType` or `Destination`.
- **DbRelay's schema, not yours.** The table layout is fixed (documented in [postgres.md](postgres.md) and
  [sqlserver.md](sqlserver.md)). Custom columns are not supported; use headers instead.
- **No ordering guarantee.** If your current outbox is a single-threaded loop that happens to preserve order, and you depend on
  that, DbRelay is not yet a drop-in replacement. Per-key ordering is on the roadmap.

## Migration plan

1. **Add DbRelay alongside the old outbox.** Create its table: `EnsureCreatedAsync`, or the script through your migration tool.
   It lives in its own schema (`dbrelay` by default) and does not touch your old table.
2. **Port the publisher** into an `IMessageDeliveryHandler`. Return `Retry` for transient errors and `DeadLetter` for permanent
   ones, pass the cancellation token, and send `OutboxMessage.Id` downstream as the idempotency key.
3. **Make the receivers idempotent** on that ID, if they are not already.
4. **Switch the producers.** Replace your outbox `INSERT` with `outbox.EnqueueAsync(transaction, message)` inside the same
   transaction. New messages now go to DbRelay.
5. **Drain the old table.** Keep the old worker running, with no new rows arriving, until its backlog reaches zero. Then stop it.
   Alternatively, copy pending rows into DbRelay in one transaction per batch with `EnqueueRawAsync` (bytes unchanged) and set
   `EnqueueOptions.MessageId` to the old ID, so consumers deduplicate correctly. Then mark the rows done in the old table, in the
   same transaction.
6. **Add observability.** Enable the `DbRelay` ActivitySource and Meter, add the health check, and alert on
   `dbrelay.messages.oldest_due_age` and dead letters.
7. **Drop the old table and worker** once you are confident.
