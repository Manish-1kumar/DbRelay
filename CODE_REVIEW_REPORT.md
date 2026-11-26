# DbRelay Code Review Report

## Review 2026-11-21: independent production-readiness review

**Reviewer:** code-reviewer (independent; did not write this code)
**Scope:** `src/DbRelay.Abstractions`, `src/DbRelay`, `src/DbRelay.PostgreSql`, `src/DbRelay.SqlServer`, plus the strength of the tests under `tests/`.
**Method:** I read every source file in scope and the main test suites. Each concern that could be settled by running something was checked against real PostgreSQL 17.5 (localhost:55432) and SQL Server 2022 Express (`.\SQLEXPRESS`), using a scratch harness built outside the repository that references the Release DLLs. No source file was modified. Every scratch schema was dropped; I confirmed `rv_*` schemas = 0 on both servers afterwards.

**Evidence column:**
- **Verified** means reproduced or measured by running something.
- **Reasoned** means derived from reading the code; not executed.

### Summary

| Severity | Count | IDs |
|---|---|---|
| Critical | 0 | none |
| High | 1 | CR-01 |
| Medium | 5 | CR-02, CR-03, CR-04, CR-05, T-01 |
| Low | 21 | CR-06 to CR-21, T-02, T-03, T-05, T-07, T-10 |
| Info | 9 | CR-22 to CR-26, T-04, T-06, T-08, T-09 |

Build, tests and dependency audit (all run by me):
- **Build:** `dotnet build DbRelay.slnx -c Release` succeeded with 0 warnings and 0 errors, across net8.0, net9.0, net10.0 and net11.0.
- **Tests (net10.0):** all passed, none skipped (databases were available):
  - `DbRelay.Tests`: 70 passed.
  - `DbRelay.PostgreSql.Tests`: 74 passed.
  - `DbRelay.SqlServer.Tests`: 91 passed.
  - `DbRelay.IntegrationTests`: 17 passed.
- **Not tested:** other target frameworks were built but not tested in this review.
- **Dependency audit:** `dotnet list DbRelay.slnx package --vulnerable --include-transitive` reported no vulnerable packages for any project.

### What was checked and found sound

These areas were checked and need no action:

- **Caller resources.**
  - No code path commits, rolls back, closes or disposes a caller-owned connection or transaction.
  - `NpgsqlBatch` and `SqlCommand` objects created on caller connections are the only things disposed.
- **Pairing validation.**
  - The connection and transaction pairing is checked by reference (`Outbox.cs:161-170`).
  - `NpgsqlConnection` and `SqlConnection` are sealed, so the provider `is` checks cannot be bypassed by subclassing.
- **Lock-token guards.**
  - Every post-claim write is guarded by the lock token. PostgreSQL uses `id = $1 AND lock_token = $2`. SQL Server additionally checks `status = 1`.
  - Requeue only changes rows with `status = 3`, whose `lock_token` is NULL, so stale workers cannot touch them.
- **Claim locking.**
  - PostgreSQL uses a `MATERIALIZED` CTE with `FOR UPDATE SKIP LOCKED`. On a 2,000,000-row table it uses `ix_outbox_messages_claim` and runs in 6.5 ms (Verified).
  - SQL Server uses the two-phase `UPDLOCK, READPAST` claim with `FORCESEEK`. I found no deadlock cycle between claim, acknowledgement, cleanup and enqueue (Reasoned).
- **Retention delete.** The PostgreSQL retention delete uses `ix_outbox_messages_cleanup` and runs in 3 ms on 2,000,000 rows (Verified).
- **Clocks.**
  - Leases and availability use the database clock.
  - The dispatcher's local lease deadline uses the monotonic `TimeProvider.GetTimestamp()`, taken before the claim round trip. This is conservative for both providers, including SQL Server's millisecond truncation.
- **SQL safety and type activation.**
  - All values are parameters.
  - There is no CLR type activation from database strings anywhere.
- **Host safety.** No exception path out of `OutboxDispatcher.ExecuteAsync` or `OutboxMaintenanceService.ExecuteAsync` was found that could trigger `BackgroundServiceExceptionBehavior.StopHost`, other than CR-26.
- **Cancellation token sources and semaphore.**
  - Every `CancellationTokenSource` is disposed.
  - The semaphore is only disposed after all in-flight tasks complete.
- **Double acknowledgement.** Not possible: the first successful guarded update clears `lock_token`.
- **Logging.** Payloads and connection strings are not logged by DbRelay. The integration test for this does have a working capture (it is not a vacuous pass).
- **SQL Server `DATEADD` overflow.** Overflow is avoided by second/millisecond splitting with a 20,000-day clamp.

---

### Findings

#### CR-01 (High): one malformed stored row wedges its entire claim batch forever, bypassing MaxAttempts and dead-lettering

- **File:**
  - `src/DbRelay/Storage/OutboxHeaders.cs:37`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:242,266-268`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:161-163,434`
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:172-180`
- **Failure scenario:**
  1. Row mapping (`ReadDetails`) runs while the claim result set is being read, **after** the claim has committed (PostgreSQL autocommit UPDATE; on SQL Server the reader is drained on dispose, which lets the batch reach COMMIT).
  2. If any row cannot be mapped, `ClaimAsync` throws. For example, headers that are valid `jsonb` but not a string-to-string object, such as `{"retries": 1}`. Such rows can come from a non-DbRelay producer writing SQL directly, a manual fix, a migration, or a future version's format.
  3. The dispatcher treats the throw as a poll failure. Every row in that batch has already been set to Processing with `attempt_count + 1`, but none is ever dispatched.
  4. When the lease expires, the same rows (which share the same `available_at`) are claimed together again, and the claim fails again.
  5. The crash-loop guard (`AttemptCount > MaxAttempts`, `OutboxDispatcher.cs:241`) never runs, because `ProcessAsync` is never reached. Attempts grow without bound, nothing is dead-lettered, and up to `BatchSize - 1` healthy messages are stuck with it permanently.
  6. The health check only flaps, because successful polls of other batches reset `ConsecutiveFailures`.
- **Evidence:** Verified on PostgreSQL. Harness setup: 5 healthy messages plus 1 row with `headers = '{"retries": 1}'`, `MaxAttempts = 3`, lease 2 s, observed for 12 s. Outcome:
  - All 6 rows were still `status = 1` with `attempt_count = 6`.
  - 0 of 5 healthy messages were delivered.
  - A message enqueued later, and claimed in a different batch, was delivered.
  - The SQL Server path is the same code shape (Reasoned).
- **Recommendation:**
  - Map rows defensively: catch per-row mapping failures and return them as "unreadable" records, or parse headers lazily in the dispatcher.
  - Dead-letter unreadable rows with the lock token and a fixed reason, so the rest of the batch proceeds.
  - Never let one row fail the whole claim.
  - Add a conformance test (see T-01).
- **Status:** Open

#### CR-02 (Medium): `SqlIdentifier` accepts a trailing newline; on both providers the DDL script then moves comment text into executable SQL

- **File:**
  - `src/DbRelay/Storage/SqlIdentifier.cs:36`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:39-45`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxSql.cs:139`
- **Failure scenario:**
  - The pattern `^[A-Za-z_][A-Za-z0-9_]{0,62}$` uses `$`, which in .NET also matches before a final `\n`. So `"outbox_messages\n"` passes validation.
  - Both providers substitute identifiers into script placeholders that also appear inside `--` comment lines. The newline ends the comment, and the rest of the library's comment line becomes SQL.
  - Runtime statements are not exploitable, because the value stays inside quotes or brackets and only a single trailing LF is admitted. However, the documented injection barrier ("rules out SQL injection through configuration") is defective.
  - `SqlIdentifier` is public SPI that third-party providers are told to rely on, and they may embed identifiers in less forgiving positions.
- **Evidence:** Verified.
  - `SqlIdentifier.IsValid("outbox_messages" + "\n")` returns `True`. The CR variant returns `False`.
  - Registration accepted the name on both providers.
  - `EnsureCreatedAsync` failed with PostgreSQL `42601: syntax error at or near "outbox"` and SQL Server `Incorrect syntax near 'the'`. Both errors are comment text being executed.
- **Recommendation:**
  - Use `\z` instead of `$` (or `RegexOptions.None` plus an explicit length and character loop).
  - Add trailing-LF and other control-character cases to the identifier tests.
- **Status:** Open

#### CR-03 (Medium): PostgreSQL statistics query is a full table scan, run every minute on every instance and on every threshold health probe

- **File:**
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:108-118`
  - `src/DbRelay/Maintenance/OutboxMaintenanceService.cs:61-65`
  - `src/DbRelay/HealthChecks/DbRelayHealthCheck.cs:87-89`
- **Failure scenario:**
  - `count(*) FILTER (...) FROM table` cannot use the partial indexes, so it reads the whole heap, including every retained Delivered row.
  - The maintenance service runs on **every** instance, including `DisableDispatcher()` web nodes, with a default `StatisticsInterval` of 1 minute.
  - Health probes with `MaxOldestDueAge` or `MaxDeadLettered` set run the same query on each probe, typically every 10 to 30 s.
  - At one day of retention and 100 msg/s, the table holds about 8.6 M rows, so N instances times one full scan per minute, plus probes.
- **Evidence:** Verified. On a 2,000,000-row table (99.9% delivered), `EXPLAIN (ANALYZE, BUFFERS)` showed a `Parallel Seq Scan` over 105,264 buffers taking 1,954 ms. The SQL Server statistics query uses the filtered indexes (its plan test covers this), so this is PostgreSQL-specific.
- **Recommendation:**
  - Count Pending and Processing from the claim index, DeadLettered from `ix_dead`, and Delivered with `ix_cleanup` (index-only scans, or `pg_class.reltuples` estimates for Delivered).
  - Alternatively, compute statistics on one elected instance only.
  - Add a large-table plan assertion, as SQL Server has.
- **Status:** Open

#### CR-04 (Medium): retention cleanup has a hard throughput ceiling, so the outbox grows without bound above about 333 msg/s per instance

- **File:** `src/DbRelay/Maintenance/OutboxMaintenanceService.cs:16,90-99,58`
- **Failure scenario:**
  - Each run deletes at most `MaxCleanupBatchesPerRun (100) × CleanupBatchSize (1000)` rows, then waits the full `CleanupInterval` (5 min) even when it stopped because it hit the cap. That is at most 100,000 rows per 5 minutes per instance.
  - If sustained deliveries exceed that rate, Delivered rows accumulate indefinitely.
  - This compounds CR-03, and grows the table, TOAST and index sizes. Nothing reports the backlog.
- **Evidence:** Reasoned.
- **Recommendation:**
  - When a run ends because it hit the cap (the last batch was full), schedule the next run immediately or after a short delay rather than a full interval.
  - Log or meter "cleanup behind".
- **Status:** Open

#### CR-05 (Medium): a handler that ignores its cancellation token permanently consumes a concurrency slot; repeated reclaims can stall an instance below MaxAttempts

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:234-261,267-280`
- **Failure scenario:**
  1. `DeliveryTimeout` is enforced only cooperatively. A handler that blocks synchronously, or awaits something that ignores the token, never returns. Its slot is released only in `ProcessAsync`'s `finally`.
  2. After the lease expires, the same instance (which still has free slots) reclaims the message and hangs another slot.
  3. After `MaxConcurrency` reclaims (8 by default), every slot is hung. `ClaimBatchAsync` blocks on `slots.WaitAsync` forever, so no more claims happen.
  4. The message's `attempt_count` stays at 8, below `MaxAttempts` (10), so it is never dead-lettered, and the whole instance delivers nothing until restart.
  5. Shutdown also hangs past the grace period.
- **Evidence:** Reasoned.
- **Recommendation:**
  - After `DeliveryTimeout` plus a small grace, stop awaiting the handler: release the slot, record a timeout retry with the lock token, and let the orphaned task finish in the background. Its late outcome is rejected by the token guard.
  - Count and log abandoned handlers.
- **Status:** Open

#### CR-06 (Low): shutdown-timeout release gives back an attempt whose handler already ran

- **File:**
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:282-287`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:86-88`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxSql.cs:105-106`
- **Failure scenario:**
  - When the host's shutdown timeout cancels an in-flight delivery, the message is released with `attempt_count - 1`. The handler did run, and may have performed its side effect.
  - With a `DeliveryTimeout` longer than the host shutdown timeout, and frequent forced restarts (rolling deploys, autoscaling), a message whose handler always runs long is never charged an attempt and never reaches dead-letter. This weakens invariant 10.
  - The tests enshrine this behaviour (see T-04).
- **Evidence:** Reasoned.
- **Recommendation:**
  - Use a release variant that keeps the attempt when the handler was invoked. Only give attempts back for messages that were never started.
  - Alternatively, document the exception explicitly.
- **Status:** Open

#### CR-07 (Low): lease safety margin is smaller than the outcome-persist budget

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:33-35,73,199-200,390-416`
- **Failure scenario:**
  - A delivery starts only if `remainingLease >= DeliveryTimeout + min(5 s, lease/10)`. Recording the outcome may then take up to 3 × 15 s (`PersistTimeout`) plus backoff, and then the fallback, about 90 s.
  - Under database slowness, outcomes routinely land after the lease has expired. If another worker has reclaimed the message, the message is delivered twice, and the first worker logs "lease lost".
  - Correctness is preserved by the token guard, but duplicate deliveries are more frequent than necessary.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Include the persist budget in the margin.
  - Alternatively, cap total persist time to the remaining lease, or extend the lease (a guarded `available_at` bump) before persisting when the remaining lease is short.
- **Status:** Open

#### CR-08 (Low): shutdown releases unstarted messages one round trip at a time, each with a 15 s timeout, ignoring the abort token

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:193-196,419-443`
- **Failure scenario:**
  - On stop, up to `BatchSize - 1` unstarted messages are released sequentially.
  - If the database is slow or unreachable, a 100-message batch can take about 25 minutes. The host gives up after its timeout plus 5 s.
  - The remainder stays in Processing until the lease expires, and the attempt is still counted.
  - `ReleaseAsync` does not observe `_abort`, and after disposal it runs against a disposed data source (owned case).
- **Evidence:** Reasoned.
- **Recommendation:**
  - Release a whole batch in one guarded statement: `WHERE id = ANY($1) AND lock_token = $2`, or a TVP or JSON list on SQL Server.
  - Bound the total release time, and stop on `_abort`.
- **Status:** Open

#### CR-09 (Low): a PostgreSQL claim cancelled at shutdown can commit without being dispatched

- **File:**
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:168-171`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:69-80`
- **Failure scenario:**
  - `ClaimAsync` passes the stopping token to `ExecuteReaderAsync` and `ReadAsync`. If cancellation lands after the autocommit UPDATE completed (while rows are being read), the rows are claimed with an attempt counted, and then discarded.
  - Each shutdown can orphan up to one batch, which is delayed by one lease.
  - SQL Server is safer here: attention plus `XACT_ABORT ON` rolls back the open claim transaction.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Do not cancel the claim itself with the stopping token (use a short independent timeout).
  - Alternatively, release whatever was read when cancellation is observed.
- **Status:** Open

#### CR-10 (Low): a retry after an ambiguous commit is reported as "lease lost", and success telemetry is skipped

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:392-416,310-314`
- **Failure scenario:**
  - If `MarkDeliveredAsync` commits but the client times out (15 s CTS) or the connection drops, the retry matches 0 rows (`lock_token` was cleared).
  - The dispatcher then logs warning 1007 ("reclaimed by another worker ... may be delivered again"), increments `dbrelay.lease.lost`, and does not record `dbrelay.messages.delivered`.
  - Operators are misled during database incidents.
- **Evidence:** Reasoned.
- **Recommendation:** On a zero-row result after a failed attempt, re-read the row (`GetAsync`) to distinguish "already recorded by me" (status matches the intended outcome, and `locked_by` is this worker) from "lease lost".
- **Status:** Open

#### CR-11 (Low): the `transaction: null` contract is documented incorrectly and differs by provider

- **File:**
  - `src/DbRelay.Abstractions/IOutbox.cs:30-33,52,69`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:49`
- **Failure scenario:**
  - The doc says passing `null` makes the insert "auto-commit on its own; it is then **not** atomic".
  - On PostgreSQL, Npgsql commands always run in the connection's current transaction. So with an active `NpgsqlTransaction` and `null`, the outbox row joins that transaction, and is rolled back with it.
  - On SQL Server, the same call throws `InvalidOperationException` (pending local transaction). The same application code therefore behaves three different ways depending on the provider.
- **Evidence:** Verified on PostgreSQL. After `BeginTransaction`, `EnqueueAsync(c, null, msg)`, then `Rollback`, the row does not exist.
- **Recommendation:**
  - Detect an active transaction when `null` is passed, and either reject it uniformly or adopt it uniformly.
  - Fix the XML docs.
- **Status:** Open

#### CR-12 (Low): a custom serializer's `ContentType` is not validated; SQL Server silently truncates it

- **File:**
  - `src/DbRelay/Outbox.cs:186`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:127,480-489`
- **Failure scenario:**
  - `RawOutboxMessage.ContentType` is length-checked, but `IMessageSerializer.ContentType` is not.
  - SQL Server parameters are sized (`NVarChar(128)`), and SqlClient silently truncates longer values. PostgreSQL stores the full value.
  - Stored data silently diverges between providers. A null `ContentType` is also not rejected early.
  - The same truncation risk applies to any future unvalidated field.
- **Evidence:** Verified. A 216-character content type was stored as 216 characters on PostgreSQL and 128 characters on SQL Server, with no error.
- **Recommendation:**
  - Validate the serializer's `ContentType` (non-empty, at most `MaxContentTypeLength`, no NUL) once at startup or per enqueue.
  - Consider throwing in `Add` when a value exceeds its parameter size.
- **Status:** Open

#### CR-13 (Low): `PurgeDeliveredAsync` has no upper bound; PostgreSQL throws while SQL Server clamps

- **File:**
  - `src/DbRelay/OutboxAdmin.cs:53`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:94`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:459-467`
- **Failure scenario:** `PurgeDeliveredAsync(TimeSpan.MaxValue)` (a natural "purge nothing recent" call) fails on PostgreSQL with `22008: timestamp out of range`, but returns 0 on SQL Server.
- **Evidence:** Verified. PostgreSQL threw `PostgresException 22008`; SQL Server returned `0`. 3,000 years worked on PostgreSQL.
- **Recommendation:** Clamp `olderThan` (for example, to 3650 days, as `MaintenanceOptions` does) in `OutboxAdmin`, or reject it with `ArgumentOutOfRangeException`.
- **Status:** Open

#### CR-14 (Low): default message type names of generic types embed assembly versions

- **File:** `src/DbRelay/Serialization/MessageTypeRegistry.cs:48`
- **Failure scenario:**
  - `Type.FullName` of a generic type includes assembly-qualified type arguments, for example `...[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, Culture=neutral, PublicKeyToken=...]]`.
  - The stored `message_type` therefore changes when the runtime major version, or the version of the type argument's assembly, changes.
  - Consumers routing by `MessageType` silently stop matching after an upgrade, and the name approaches the 256-character limit quickly.
  - The docs only warn about renames and moves.
- **Evidence:** Verified (observed in stored rows during the CR-01 run).
- **Recommendation:** For generic types, require an explicit mapping or attribute (throw a clear error), or build a version-free name.
- **Status:** Open

#### CR-15 (Low): SQL Server begins its own transaction on a caller-owned connection, and `EnqueueManyAsync` is not "single round trip"

- **File:**
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:82-93,96-103`
  - `src/DbRelay.Abstractions/IOutbox.cs:49`
- **Failure scenario:**
  - With `transaction: null` and more than 200 records, the provider calls `BeginTransactionAsync` and `CommitAsync` on the caller's connection.
  - With a caller transaction and more than 200 records, it issues several statements. A failure in chunk N leaves chunks 1 to N-1 inside the caller's transaction (SQL Server does not abort the transaction on a statement error unless `XACT_ABORT` is set). A caller that catches and commits therefore commits a partial batch. PostgreSQL's batch error aborts the whole transaction instead.
  - The interface doc promises "a single round trip".
- **Evidence:** Verified that the tested cases are benign. With an ambient `TransactionScope` (150 and 250 messages) or a T-SQL `BEGIN TRAN` (`@@TRANCOUNT` back to 1), the rows rolled back with the caller (0 of 250 survived). Partial-commit semantics: Reasoned.
- **Recommendation:**
  - Do not begin transactions on caller connections. Use a single statement with a TVP or JSON (`OPENJSON`) input, which is atomic and one round trip.
  - Alternatively, document that callers must roll back on any exception, and fix the XML doc.
- **Status:** Open

#### CR-16 (Low): `BatchSize` much larger than `MaxConcurrency` causes claim and release churn

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:146-149,199-206,122`
- **Failure scenario:**
  - The loop claims a full `BatchSize` (100) as soon as **one** slot is free. Unstarted messages burn their lease while waiting. Once the remaining lease is too short, they are released (two writes per message, wasted).
  - Because the batch was full, the loop reclaims immediately. With slow handlers, the defaults (100 vs 8) produce a steady stream of claim, release and reclaim writes.
  - Released rows also lose their queue position (`available_at = now`).
- **Evidence:** Reasoned.
- **Recommendation:** Claim `min(BatchSize, freeSlots + small prefetch)`, or validate `BatchSize` against `MaxConcurrency × (LeaseDuration / expected delivery time)`.
- **Status:** Open

#### CR-17 (Low): PostgreSQL `Delay` and `created_at` are measured from the caller's transaction start, not the insert

- **File:**
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:47-52`
  - `src/DbRelay.Abstractions/EnqueueOptions.cs:32-36`
  - `src/DbRelay.PostgreSql/Scripts/001_create_outbox.sql:28`
- **Failure scenario:**
  - `now()` is the transaction start time. In a caller transaction that has been open for 5 minutes, `Delay = 1 min` is already in the past at commit.
  - The doc says "no earlier than this long after the insert". SQL Server uses statement time (`SYSUTCDATETIME()`), so the providers differ.
- **Evidence:** Reasoned (from the SQL; the code comment acknowledges it).
- **Recommendation:** Use `clock_timestamp()` for `Delay` arithmetic and `created_at`, or correct the documentation.
- **Status:** Open

#### CR-18 (Low): no aggregate size limits for enqueue batches or claim batches

- **File:**
  - `src/DbRelay/Outbox.cs:77-86`
  - `src/DbRelay/Configuration/Options.cs:7,14`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:240`
- **Failure scenario:**
  - `EnqueueManyAsync` accepts 1000 × `MaxPayloadBytes`: 1 GiB per call at the default, and 256 GiB at the allowed maximum. All of it is buffered and sent in one request.
  - A claim loads `BatchSize × payload` into memory at once (100 MiB at defaults, 256 GiB at maximums).
  - One oversized call can exhaust memory on the producer or on every dispatcher.
- **Evidence:** Reasoned.
- **Recommendation:** Add a total-bytes cap per enqueue call, and cap claim batches by bytes (for example, stop the claim once a cumulative `octet_length(payload)` threshold is reached).
- **Status:** Open

#### CR-19 (Low): the health check exposes database exception details and runs unguarded statistics

- **File:** `src/DbRelay/HealthChecks/DbRelayHealthCheck.cs:57,87-89`
- **Failure scenario:**
  - The raw provider exception is attached to the `HealthCheckResult`. Common response writers (for example HealthChecks.UI) emit `exception.Message`, which includes the database host, port or server name, onto often-public health endpoints.
  - `GetStatisticsAsync` is not caught, so a slow statistics scan (see CR-03) turns a "degraded backlog" probe into `Unhealthy`, which can restart pods under load.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Attach only the exception type, or attach nothing by default.
  - Catch statistics failures and report Degraded with a fixed message.
- **Status:** Open

#### CR-20 (Low): `SystemTextJsonMessageSerializer` freezes the caller's `JsonSerializerOptions`

- **File:** `src/DbRelay/Serialization/SystemTextJsonMessageSerializer.cs:20-21`
- **Failure scenario:**
  - `UseJsonSerializerOptions(appOptions)` passes the application's shared options object. `MakeReadOnly()` then makes it immutable.
  - Later configuration of that same instance elsewhere in the application (for example ASP.NET Core JSON options, or adding a converter at startup) throws `InvalidOperationException`, far from DbRelay.
- **Evidence:** Reasoned.
- **Recommendation:** Copy the options (`new JsonSerializerOptions(options)`) before freezing.
- **Status:** Open

#### CR-21 (Low): PostgreSQL admin `QueryAsync` sorts the whole table

- **File:**
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:154-156`
  - `src/DbRelay.PostgreSql/Scripts/001_create_outbox.sql:40-47`
- **Failure scenario:**
  - `ORDER BY created_at, id OFFSET/LIMIT` has no supporting index, so every admin page scans and sorts all rows, including millions of Delivered rows.
  - Deep `Skip` values make it worse (unbounded `int`).
- **Evidence:** Reasoned.
- **Recommendation:** Add an index on `(created_at, id)`, or keyset pagination, or document admin queries as non-production-path. Cap `Skip`.
- **Status:** Open

#### CR-22 (Info): extension overloads allow silent mistakes and miss common shapes

- **File:**
  - `src/DbRelay.Abstractions/OutboxExtensions.cs:20-28`
  - `src/DbRelay.Abstractions/IOutbox.cs:41-46,93-96`
- **Failure scenario:**
  - Because `TMessage` is unconstrained, `outbox.EnqueueAsync(tx, cancellationToken)` (message forgotten) compiles and enqueues a `CancellationToken` as the message.
  - `EnqueueAsync(tx, msg, ct)` correctly binds the 3-parameter extension, not the 5-parameter interface method.
  - `EnqueueAsync(conn, tx, msg, ct)` and `EnqueueStandaloneAsync(msg, ct)` do not compile without named arguments.
- **Evidence:** Verified the bindings with a recording `IOutbox`. The calls bound to `EnqueueAsync<AnonymousType>` and `EnqueueAsync<CancellationToken>`.
- **Recommendation:**
  - Add a Roslyn analyzer, or `where TMessage : notnull`, plus a runtime rejection of `CancellationToken`, `DbTransaction` or `DbConnection` messages.
  - Add `(…, CancellationToken)` overloads.
- **Status:** Open

#### CR-23 (Info): wrapped connections are rejected

- **File:**
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:27-40`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:29-41`
- **Failure scenario:**
  - Profiling or tracing wrappers (MiniProfiler `ProfiledDbConnection` and similar) are rejected. This fails closed, which is correct.
  - However, the error does not explain how to unwrap. A wrapper whose `Transaction.Connection` returns the inner connection gets the misleading "belongs to a different connection" error.
- **Evidence:** Reasoned.
- **Recommendation:** Document the limitation, or accept an unwrap hook.
- **Status:** Open

#### CR-24 (Info): public SPI and service interfaces are hard to evolve

- **File:**
  - `src/DbRelay/Storage/IOutboxStore.cs:18`
  - `src/DbRelay.Abstractions/IOutbox.cs:23`
  - `src/DbRelay.Abstractions/Administration.cs:22`
- **Failure scenario:** Adding any member later, for example a batch release (needed for CR-08) or row-level error reporting (needed for CR-01), breaks every third-party provider and every test double.
- **Evidence:** Reasoned.
- **Recommendation:** Decide before 1.0: an abstract base class for `IOutboxStore`, or default interface methods.
- **Status:** Open

#### CR-25 (Info): doubled punctuation in log text

- **File:** `src/DbRelay/Diagnostics/Log.cs:25` combined with `OutboxDispatcher.cs:291`
- **Failure scenario:** The log reads "...within 00:00:00.2000000.. Retrying in ...".
- **Evidence:** Verified (seen in `DbRelay.Tests` output).
- **Recommendation:** Drop the trailing period from the TimeoutException message, or from the template.
- **Status:** Open

#### CR-26 (Info): a wall-clock jump can stop the host

- **File:** `src/DbRelay/Maintenance/OutboxMaintenanceService.cs:58,64,70-75`
- **Failure scenario:**
  - The schedule uses wall-clock `GetUtcNow()`.
  - If the system clock moves back by more than about 49 days, `next - now` exceeds `Task.Delay`'s limit, and `ArgumentOutOfRangeException` escapes `ExecuteAsync`. That triggers `StopHost` on .NET 6+.
  - This is the only uncaught path into the host found.
- **Evidence:** Reasoned.
- **Recommendation:** Schedule with `GetTimestamp()` or `PeriodicTimer`, and clamp the wait.
- **Status:** Open

---

### Test-suite findings

#### T-01 (Medium): no test covers rows DbRelay did not write

- **File:** `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs` (no such case)
- **Failure scenario:** The suite only exercises rows written through the API, so CR-01 (one malformed row stalls a batch forever) passes every existing test.
- **Evidence:** Verified (CR-01 reproduced while the full suite is green).
- **Recommendation:** Add a conformance test that inserts rows with bad headers, an unknown status, or a NULL in a nullable-by-schema column via raw SQL. Assert that they end dead-lettered and that their batch-mates are delivered.
- **Status:** Open

#### T-02 (Low): identifier tests miss control characters

- **File:**
  - `tests/DbRelay.Tests/CoreTests.cs:141-150`
  - `tests/DbRelay.SqlServer.Tests/SqlServerProviderTests.cs:38-46`
  - `tests/DbRelay.PostgreSql.Tests/PostgreSqlProviderTests.cs:31-47`
- **Failure scenario:** There is no trailing `\n` or other control-character case, so CR-02 is undetected.
- **Evidence:** Verified.
- **Recommendation:** Add `"name\n"`, `"name\r\n"`, `"\nname"` and `"na\tme"` cases.
- **Status:** Open

#### T-03 (Low): PostgreSQL plan tests are weak

- **File:** `tests/DbRelay.PostgreSql.Tests/PostgreSqlProviderTests.cs:296-345`
- **Failure scenario:**
  - The test forces `SET LOCAL enable_seqscan = off` on a 5-row table. That proves the index is usable, not that the planner chooses it.
  - The statistics query is not plan-tested at all; the SQL Server suite does test its statistics plan, so this is an asymmetry. CR-03 is therefore undetected.
- **Evidence:** Verified. At 2M rows the planner does pick the claim and cleanup indexes, but the statistics query does a sequential scan.
- **Recommendation:** Seed a realistic table (as SQL Server does), assert plans without disabling seqscan, and include `StatisticsSql`.
- **Status:** Open

#### T-04 (Info): shutdown tests lock in the attempt give-back

- **File:**
  - `tests/DbRelay.Tests/DispatcherTests.cs:428`
  - `tests/DbRelay.IntegrationTests/GracefulShutdownTests.cs:163`
- **Failure scenario:** The tests assert `AttemptCount == 0` after a handler was invoked and then cancelled. This enshrines CR-06 and will need updating if it is fixed.
- **Evidence:** Reasoned.
- **Recommendation:** Revisit together with CR-06.
- **Status:** Open

#### T-05 (Low): the `null`-transaction test misses the active-transaction case

- **File:** `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:286-293`
- **Failure scenario:** `Enqueue_without_transaction_autocommits_on_caller_connection` only uses a connection with no active transaction. The provider divergence in CR-11 (PostgreSQL joins the transaction; SQL Server throws) is untested.
- **Evidence:** Verified (CR-11).
- **Recommendation:** Add the "active transaction, `null` passed" case to the conformance suite with one defined expected behaviour.
- **Status:** Open

#### T-06 (Info): non-ASCII test literals are double-encoded (mojibake)

- **File:**
  - `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:354,368,578,582`
  - `tests/DbRelay.Tests/CoreTests.cs:149`
- **Failure scenario:**
  - The files contain double-encoded UTF-8: bytes `C3 83 C2 BC` ("Ã¼") instead of "ü", and "âœ“" instead of "✓".
  - The tests still use non-ASCII text, so they are not vacuous. But they do not test the intended characters (for example, no astral-plane or combining characters).
- **Evidence:** Verified (byte dump).
- **Recommendation:** Re-save as UTF-8 with the intended literals, and add a surrogate-pair case.
- **Status:** Open

#### T-07 (Low): the statistics assertion is too weak

- **File:** `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:715-717`
- **Failure scenario:** After expiring a lease, the test only asserts `OldestDueAge` is not null. It would pass if the expired Processing row were ignored, because other due rows still exist.
- **Evidence:** Reasoned.
- **Recommendation:** Assert that the value is at least the expired row's age, or isolate the case.
- **Status:** Open

#### T-08 (Info): a test name contradicts the at-least-once contract

- **File:** `tests/DbRelay.IntegrationTests/MultiWorkerTests.cs:14`
- **Failure scenario:** `Four_hosts_deliver_2000_messages_exactly_once_each_without_crashes`. The assertion (no duplicates in a fault-free run) is fine, but the wording contradicts invariant 6 and tends to leak into documentation.
- **Evidence:** Reasoned.
- **Recommendation:** Rename to "...without duplicates when no failures occur".
- **Status:** Open

#### T-09 (Info): dispatcher unit tests use the application clock

- **File:** `tests/DbRelay.Tests/Fakes/InMemoryOutboxStore.cs:56,100-113`
- **Failure scenario:** The fake computes leases with the application `TimeProvider`, so the unit tests cannot detect database-versus-application clock mistakes. Only the integration tests can.
- **Evidence:** Reasoned.
- **Recommendation:** Keep the integration coverage mandatory in CI, and give the fake a separate "database clock" that is skewed from the application clock.
- **Status:** Open

#### T-10 (Low): the poison-row path (CR-01) is untested on SQL Server

- **File:** `tests/DbRelay.SqlServer.Tests` (no such case)
- **Failure scenario:** As T-01, for the SQL Server reader-drain-then-COMMIT path.
- **Evidence:** Reasoned.
- **Recommendation:** Same test through the shared conformance suite.
- **Status:** Open

---

### Commands run in this review

All commands were run after `. env.ps1` (from the scratchpad), from `D:\DbRelay` unless stated otherwise.

| Command | Outcome |
|---|---|
| `dotnet --version` | 11.0.100 |
| `dotnet build DbRelay.slnx -c Release` | Build succeeded, 0 warnings, 0 errors (11m51s) |
| `dotnet test --project tests/DbRelay.Tests/... -c Release -f net10.0 --no-build` | 70/70 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.PostgreSql.Tests/... -c Release -f net10.0 --no-build` | 74/74 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.SqlServer.Tests/... -c Release -f net10.0 --no-build` | 91/91 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.IntegrationTests/... -c Release -f net10.0 --no-build` | 17/17 passed, 0 skipped |
| `dotnet list DbRelay.slnx package --vulnerable --include-transitive` | No vulnerable packages in any project |
| Scratch harness (outside the repo, referencing the Release DLLs), scenario `poison` | CR-01 reproduced: 6 rows stuck in Processing with attempts=6 (MaxAttempts=3); 0/5 healthy messages delivered |
| Harness `ident` | CR-02: `IsValid("…\n") = True`; `EnsureCreated` produced syntax errors on both providers |
| Harness `pg-null-tx` | CR-11: row rolled back with the caller's transaction |
| Harness `purge-max` | CR-13: PostgreSQL `22008 timestamp out of range`; SQL Server returned 0 |
| Harness `content-type` | CR-12: PostgreSQL stored 216 characters; SQL Server stored 128 |
| Harness `ss-own-tx` | CR-15: TransactionScope and T-SQL transaction cases rolled back correctly (0/250 rows survived) |
| Harness `overload` | CR-22: bindings confirmed |
| `psql` with 2,000,000 seeded rows: `EXPLAIN (ANALYZE, BUFFERS)` of the statistics, claim and cleanup SQL | Statistics: Parallel Seq Scan, 1,954 ms. Claim: 6.5 ms via `ix_outbox_messages_claim`. Cleanup: 3.1 ms via `ix_outbox_messages_cleanup`. Schema dropped |
| `psql`/`sqlcmd` cleanup check for `rv_*` schemas | 0 on both servers |

Not run: `dotnet format --verify-no-changes`, `dotnet pack`, and tests on net8.0, net9.0 and net11.0.

---

## Resolution log (principal architect, 2026-11-21)

The reviewer's findings above are left unedited. This section records what was done about each one. "Fixed" means code changed and a test that
fails without the change was added or updated; every fix was re-verified by running the suites (see `TEST_REPORT.md`, addendum).

| ID | Sev | Resolution | Change / test |
|---|---|---|---|
| CR-01 | High | **Fixed** | `OutboxHeaders.Deserialize` never throws: non-string values become raw JSON text, and non-object or invalid JSON yields a reserved `dbrelay-headers-error` header. This was the only realistic per-row mapping failure (every other column is `NOT NULL`, fixed-type and check-constrained). Conformance test `Claim_tolerates_rows_with_headers_DbRelay_did_not_write` writes the bad rows directly with SQL on both providers (closes T-01 and T-10), plus unit theories. |
| CR-02 | Medium | **Fixed** | `SqlIdentifier` uses `\A...\z`. New cases for `\n`, `\r\n`, `\t` and `\0` (closes T-02). |
| CR-03 | Medium | **Fixed (mitigated)** | The PostgreSQL statistics query now uses one subquery per status, each matching a partial index. On 2M rows, measured with EXPLAIN ANALYZE: parallel seq scan, 552 ms warm (1,954 ms cold in the review) → about 230–295 ms. All counts come from index scans with 0 heap fetches, and the remaining cost is the exact Delivered count (an index-only scan). Statistics are now refreshed only on instances that run the dispatcher. Residual: the Delivered count stays O(retained delivered rows); see K12. |
| CR-04 | Medium | **Fixed** | When cleanup hits its per-run cap, the next run starts after 1 s, and event 1102 "cleanup is behind" is logged. |
| CR-05 | Medium | **Fixed** | The handler runs via `Task.Run`. After `DeliveryTimeout` plus grace (min(5 s, timeout/2), which stays inside the lease) the dispatcher stops waiting, records a timeout retry under its lock token, logs event 1013, and increments the metric `dbrelay.delivery.abandoned`. Unit test `Handlers_that_ignore_cancellation_are_abandoned_and_do_not_hold_slots`; integration test revised to `Handler_that_ignores_its_timeout_is_abandoned_and_its_late_result_never_overwrites_the_redelivery`. |
| T-01 | Medium | **Fixed** | See CR-01. |
| CR-06 | Low | **Fixed** | An in-flight delivery cancelled by the shutdown timeout is returned as a zero-delay retry, so the attempt stays counted, with reason "Delivery was interrupted by host shutdown.". Only unstarted claims give the attempt back. Tests: `Handlers_that_always_outlast_shutdown_still_reach_dead_letter`, and the updated shutdown tests (closes T-04). |
| CR-07 | Low | Accepted (documented) | Correctness is preserved by the token guard. The worst case is an extra duplicate during database slowness, which at-least-once allows. Revisit with lease extension in 0.2. |
| CR-08 | Low | Deferred to 0.2 | A batch release needs a new SPI member. That is bundled with the SPI-evolution decision (CR-24). |
| CR-09 | Low | **Fixed** | The claim is no longer cancelled by the stopping token (independent 15 s timeout). A claim that completes during shutdown is released unstarted. |
| CR-10 | Low | **Fixed** | After a failed attempt, a zero-row result re-reads the row. If it already holds this worker's outcome (status matches and `locked_by` is this worker), it counts as recorded, not as a lost lease. Unit test `An_acknowledgement_that_committed_but_timed_out_is_not_reported_as_a_lost_lease`. |
| CR-11 | Low | **Fixed (documentation)** | The `IOutbox` XML docs now state the provider-specific behaviour of `transaction: null` while a transaction is in progress. Making it uniform needs provider introspection and is deferred to 0.2. |
| CR-12 | Low | **Fixed** | The serializer `ContentType` is validated (1 to 128 characters, no NUL) when `Outbox` is constructed. |
| CR-13 | Low | **Fixed** | `PurgeDeliveredAsync` requires `olderThan` ≤ 3650 days, the same bound on every provider. |
| CR-14 | Low | **Fixed** | Generic type names are formatted as `Ns.Envelope<Ns.Arg>`, without assembly versions. Unit test added. |
| CR-15 | Low | Accepted (documented, K9) | Verified harmless under a caller rollback. |
| CR-16 | Low | Accepted (documented) | Guidance: keep `BatchSize` within a small multiple of `MaxConcurrency × (lease / typical delivery time)`; see `docs/configuration.md`. |
| CR-17 | Low | Accepted (documented) | PostgreSQL `now()` is the transaction start time, so `created_at` and `Delay` count from the start of the caller's transaction. |
| CR-18 | Low | Deferred to 0.2 | Aggregate batch byte limits. The per-message limit and the 1000-message cap bound the worst case at 1 GiB per call; a lower aggregate cap is planned. |
| CR-19 | Low | **Fixed** | A statistics failure in the health check reports Degraded (not Unhealthy), with a generic description. |
| CR-20 | Low | **Fixed** | The serializer copies the caller's `JsonSerializerOptions` before freezing them. Unit test added. |
| CR-21 | Low | Deferred | The admin `QueryAsync` sort. Operator-only and paged; a `created_at` index is planned for 0.2. |
| CR-22 | Info | **Fixed (partially)** | `EnqueueAsync(transaction, ct)` with the message forgotten now throws an actionable `ArgumentException`. |
| CR-23 | Info | Accepted (K10) | |
| CR-24 | Info | **Open, decision needed before 1.0** | Public interfaces can't gain members without breaking implementers. Options: default interface members, or an abstract `OutboxStoreBase`. Tracked in the roadmap. |
| CR-25 | Info | **Fixed** | Log 1005 template no longer doubles punctuation. |
| CR-26 | Info | Accepted | Requires a wall-clock jump of more than 49 days. |
| T-03, T-07 | Low | Partly addressed | The statistics plan was measured manually (CR-03). An automated large-table plan assertion for PostgreSQL is still open. |
| T-05 | Low | Open | Covered by the documentation change in CR-11. A test for the active-transaction case is still to be added. |
| T-06 | Info | **Fixed** | Mojibake literals (introduced by the architect's PowerShell 5.1 edits) replaced with `\u` escapes, including an astral emoji. |
| T-08 | Info | **Fixed** | Renamed to `Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run`. |
| T-09 | Info | Accepted | |

**Open Critical/High after resolution: none.** Open Medium: none.

---

## Follow-up audit 2026-11-22 (milestone 13): verification of the resolution log

**Reviewer:** code-reviewer (independent; did not write the fixes)
**Scope:** every entry in the resolution log above, plus new defects introduced by the fixes. Most attention went to `src/DbRelay/Dispatching/OutboxDispatcher.cs`.
**Method:**
- Re-read the changed code.
- Re-ran all four test projects myself on net10.0.
- Ran a new scratch harness against PostgreSQL 17.5 (localhost:55432) and SQL Server Express (`.\SQLEXPRESS`). The harness lives in the scratchpad (`fu\harness`), outside the repository, and references the freshly built Release DLLs.
- Every scratch schema was dropped. Afterwards I confirmed 0 `fu_*` schemas on both servers.
- No source or test file was modified. The resolution log was treated as claims to check.

**Evidence:** Verified = reproduced or measured by running something. Reasoned = derived from reading the code.

### Test results (run by me)

| Project (net10.0, Release) | Result |
|---|---|
| `DbRelay.Tests` | 87/87 passed, 0 skipped |
| `DbRelay.PostgreSql.Tests` | 75/75 passed, 0 skipped |
| `DbRelay.SqlServer.Tests` | 93/93 passed, 0 skipped |
| `DbRelay.IntegrationTests` | 17/17 passed, 0 skipped |

The suites are green **while FU-01 (a High-severity CR-01 residual) reproduces**. Green suites therefore do not show that CR-01 is closed.

### Verdicts on prior findings

| ID | Verdict | Notes |
|---|---|---|
| CR-01 | **Partially fixed** | Non-string values, non-objects, invalid JSON and depth above 64 are now tolerated (Verified). However, `Deserialize` still throws `InvalidOperationException` for lone-surrogate escapes and `ArgumentException` for raw lone surrogates. SQL Server stores headers as unconstrained `nvarchar(max)`, so such a row still wedges its whole claim batch and bypasses MaxAttempts. Reproduced on SQL Server; see **FU-01**. PostgreSQL `jsonb` rejects these inputs at insert, so PostgreSQL is not affected (Verified). |
| CR-02 | Verified fixed | `IsValid` returns False for `"x\n"`, `"x\r\n"`, `"\nx"`, `"x\tx"` and `"x\0x"`, and True for `"outbox"`. |
| CR-03 | Verified fixed (mitigated) | Seeded 1,000,000 rows. Counts match a `GROUP BY` exactly, and the oldest-due age matches within 1 s. Plan: Pending and Processing use an index scan of `ix_..._claim`; Delivered uses a parallel index-only scan of `ix_..._cleanup` (43 heap fetches); DeadLettered uses an index-only scan of `ix_..._dead`; oldest-due uses an index-only scan with LIMIT 1. Total 188 ms, of which the Delivered count is about 186 ms. Residual: Delivered stays O(retained rows), and Pending/Processing are O(backlog) with heap visits, because `status` is not in the claim index. Statistics now run only where the dispatcher is registered. |
| CR-04 | Verified fixed (code read) | When a run hits the cap, the next run starts after 1 s and event 1102 is logged. Event 1102 is a Warning on every catch-up run, so it is noisy under sustained load (cosmetic). |
| CR-05 | **Partially fixed** | Slots are no longer held by hung handlers (unit test, plus my harness). The fix introduces **FU-02** (abandoned handlers accumulate without bound, Verified), **FU-03** (the "grace stays inside the lease" claim is false, Verified) and **FU-09** (CTS and abort-path details, Info). |
| CR-06 | Verified fixed | An interrupted in-flight delivery uses `ScheduleRetryAsync(..., TimeSpan.Zero, ...)`, so the attempt stays counted. `ReleaseAsync` (which gives the attempt back) is called only from `DispatchBatchAsync`, for messages at index `i..` that were never started. Attempts stay bounded across restarts through the crash-loop guard. Both the unit test and the integration test assert this. Minor wording issue: **FU-10**. |
| CR-07 | Accepted-OK | FU-03 widens this window. |
| CR-08 | Not fixed (deferred; acceptable) | Interacts with FU-04: with the database hung, unstarted releases still run sequentially after `StopAsync` has returned. |
| CR-09 | **Partially fixed** | The PostgreSQL mid-read orphaning at shutdown is addressed. The claim now ignores both the stopping token and `_abort`, so shutdown is no longer prompt when the database hangs (**FU-04**, Verified). The fixed 15 s timeout also overrides the provider's `CommandTimeout` (**FU-06**). |
| CR-10 | **Partially fixed** | True positives are handled. The re-read can report false success, and the fallback path still misreports "lease lost": **FU-05**. |
| CR-11 | Verified fixed (documentation) | `IOutbox.cs:32-34` now documents the provider difference. T-05 is still open. |
| CR-12 | Verified fixed (code read) | `Outbox.cs:48-54` validates length 1 to 128 and no NUL, at construction. |
| CR-13 | Verified fixed | `PurgeDeliveredAsync(TimeSpan.MaxValue)` throws `ArgumentOutOfRangeException` before reaching any provider. |
| CR-14 | **Partially fixed** | Generic names are now version-free, but nested types inside generic types collide, and arrays of generic types still embed assembly versions (**FU-07**, Verified). |
| CR-15 | Accepted-OK | |
| CR-16 | Accepted-OK | |
| CR-17 | Accepted-OK | |
| CR-18 | Not fixed (deferred; acceptable) | |
| CR-19 | **Partially fixed** | Statistics failures now report Degraded. The raw provider exception is still attached to the result at `DbRelayHealthCheck.cs:57` and `:99`, which was the first half of the finding (**FU-08**). |
| CR-20 | Verified fixed | The caller's `JsonSerializerOptions` are still mutable after constructing the serializer. |
| CR-21 | Not fixed (deferred; acceptable) | |
| CR-22 | Verified fixed (stated partial scope) | `EnqueueAsync(tx, ct)` throws `ArgumentException`. `DbTransaction` or `DbConnection` passed as the message, and the missing overloads, are not addressed, as the resolution log says. |
| CR-23 | Accepted-OK | |
| CR-24 | Not fixed (open decision before 1.0) | |
| CR-25 | Verified fixed (code read) | The template ends with `Reason: {Reason}`. |
| CR-26 | Accepted-OK | |
| T-01 | **Partially fixed** | The conformance test exists for both providers but omits invalid UTF-16 input, which is exactly the remaining failure mode (**FU-T2**). |
| T-02 | Verified fixed | |
| T-03 | Not fixed (acknowledged) | |
| T-04 | Verified fixed | The tests now assert that the attempt is kept, plus the reason text. |
| T-05 | Not fixed (acknowledged) | |
| T-06 | Verified fixed | No mojibake byte sequences remain under `tests/`. |
| T-07 | Not fixed (acknowledged) | |
| T-08 | Verified fixed | |
| T-09 | Accepted-OK | |
| T-10 | **Partially fixed** | As T-01; the failing input is SQL Server-specific. |

**Verdict counts (36 prior items):**

| Verdict | Count | IDs |
|---|---|---|
| Verified fixed | 14 | CR-02, CR-03, CR-04, CR-06, CR-11, CR-12, CR-13, CR-20, CR-22, CR-25, T-02, T-04, T-06, T-08 |
| Partially fixed | 8 | CR-01, CR-05, CR-09, CR-10, CR-14, CR-19, T-01, T-10 |
| Not fixed (deferred, acknowledged or open decision) | 7 | CR-08, CR-18, CR-21, CR-24, T-03, T-05, T-07 |
| Accepted-OK | 7 | CR-07, CR-15, CR-16, CR-17, CR-23, CR-26, T-09 |

### New findings

| Severity | Count | IDs |
|---|---|---|
| Critical | 0 | none |
| High | 1 | FU-01 |
| Medium | 1 | FU-02 |
| Low | 9 | FU-03 to FU-08, FU-T1 to FU-T3 |
| Info | 2 | FU-09, FU-10 |

#### FU-01 (High): CR-01 residual. Invalid UTF-16 in stored headers still fails the whole claim on SQL Server, wedging the batch and bypassing MaxAttempts

- **File:**
  - `src/DbRelay/Storage/OutboxHeaders.cs:47,56-58,63`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:163,434`
  - `src/DbRelay.SqlServer/Scripts/001_create_outbox.sql:27`
- **Failure scenario:**
  - Only `JsonException` is caught (`:63`). `JsonDocument.Parse` accepts `\uD800`-style escapes, but `JsonElement.GetString()` and `JsonProperty.Name` (`:56-58`) then throw `InvalidOperationException` ("Cannot read incomplete UTF-16 JSON text..."). A raw lone surrogate in the .NET string makes `JsonDocument.Parse(string)` (`:47`) throw `ArgumentException`.
  - SQL Server's `headers` column is `nvarchar(max)` with no JSON constraint, so a foreign producer can store such text. A realistic example is a JavaScript producer: well-formed `JSON.stringify` writes a string cut in the middle of an emoji as `"\ud83d"`.
  - The row then fails `ReadDetails` while the claim result is read. This is exactly the CR-01 wedge: the whole batch stays in Processing, `attempt_count` grows every lease, nothing is dead-lettered, and the healthy batch-mates are never delivered.
  - DbRelay's own writer is safe: `Serialize` replaces lone surrogates with U+FFFD (Verified).
  - PostgreSQL `jsonb` rejects `\ud800` (22P02) and `\u0000` (22P05), so PostgreSQL is not affected (Verified).
- **Evidence:** Verified.
  - `OutboxHeaders.Deserialize` throws for 5 inputs: a lone high or low surrogate escape in a value, a lone surrogate escape in a key, and a raw lone surrogate inside or outside a string.
  - It is safe for depth 200 (returns the error header), duplicate keys (last one wins), `1e400`, `\u0000`, trailing garbage, comments, BOM, non-JSON text and a 10 MB value.
  - **SQL Server harness:** 5 healthy messages plus 1 row with `headers = N'{"a":"\ud800"}'`, MaxAttempts 3, lease 2 s, observed for 12 s. Result: all 6 rows ended at `status = 1` and `attempt_count = 6`. 0 of 5 healthy messages were delivered. Event 1002 was logged 6 times with `InvalidOperationException: Cannot read incomplete UTF-16 JSON text as string with missing low surrogate.`
- **Recommendation:**
  - Make the "never throws" contract literal: catch `Exception` (at least `JsonException`, `InvalidOperationException` and `ArgumentException`) around the whole parse, and return the error header.
  - Defence in depth: wrap the per-row `ReadDetails` call in both providers so that any mapping exception yields an "unreadable" record, which the dispatcher dead-letters under its lock token. A future column or type change must not reopen CR-01.
  - Add the five inputs above to the unit theory and to the conformance suite (FU-T2).
- **Status:** Open

#### FU-02 (Medium): abandoned handlers accumulate without bound, each holding a thread-pool thread and its DI scope

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:285-307,389-413`
- **Failure scenario:**
  - Before CR-05, a hung handler pinned a slot, so at most `MaxConcurrency` handlers could hang.
  - Now each abandonment frees the slot, and the message is retried after a short backoff. Every retry starts a new handler, which hangs again (for example, a synchronous call into a dead dependency, or a lock that is never released).
  - Each orphan keeps a blocked thread-pool thread, its scope and its scoped services (DbConnections, HttpClients) until it returns, which may be never.
  - The number of orphans is bounded only by messages × MaxAttempts, so with a continuous stream it is unbounded.
  - Thread-pool growth (hill-climbing) and starvation then degrade the whole process, including the dispatcher's own timers (abandonment detection, persist timeouts) and any co-hosted ASP.NET requests.
  - Nothing exposes the live orphan count; `dbrelay.delivery.abandoned` counts events, it is not a gauge. The health check stays Healthy.
- **Evidence:** Verified. Harness on PostgreSQL: a handler that blocks on a `ManualResetEventSlim` and ignores its token, `MaxConcurrency = 2`, `BatchSize = 2`, 10 messages, lease 2 s, timeout 1 s, MaxAttempts 1000. Running handlers and thread-pool threads over time:

  | Time | Handlers still running | Thread-pool threads |
  |---|---|---|
  | Before start | 0 | 3 |
  | 5 s | 7 | 8 |
  | 15 s | 16 | 17 |
  | 30 s | 33 | 34 |

  At 30 s, 31 abandonments had been logged (event 1013), for 2 configured slots.
- **Recommendation:**
  - Track abandoned tasks, and cap them (for example at `MaxConcurrency`). While the cap is reached, stop claiming, and report Degraded in the health check with the orphan count.
  - Expose a `dbrelay.delivery.abandoned.active` gauge.
  - Consider dead-lettering immediately when the same message is abandoned twice; another attempt is very unlikely to behave differently.
  - Document that abandonment does not stop the handler.
- **Status:** Open

#### FU-03 (Low): "DeliveryTimeout + grace stays inside the lease" is false for messages started late in a batch; abandonment then races a reclaim

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:74,76-78,209-210,290`
- **Failure scenario:**
  - The start gate only requires `remainingLease >= DeliveryTimeout + margin`, where `margin = min(5 s, lease/10)`.
  - Abandonment happens at `start + DeliveryTimeout + grace`, where `grace = min(5 s, DeliveryTimeout/2)`.
  - Whenever `grace > margin`, a message started near the gate is abandoned **after** its lease has expired. That holds for any lease under 50 s with `DeliveryTimeout > lease/5`; for example lease 20 s with timeout 10 s gives margin 2 s and grace 5 s.
  - With the defaults (lease 2 min, timeout 30 s), grace equals margin, so abandonment lands exactly at lease expiry.
  - Another worker can then reclaim and deliver while the orphan is still running. The abandoning worker's guarded retry matches 0 rows, and it logs "lease lost".
  - The code comment's "<= 0.75 x lease" (`:76-77`) is measured from the claim, not from the delivery start.
  - Correctness holds (token guard), but the resolution log's statement is wrong, and duplicates occur more often than necessary.
- **Evidence:** Verified.
  - Two hosts, lease 2 s, timeout 1 s (margin 0.2 s, grace 0.5 s).
  - Host A has `MaxConcurrency = 1` and `BatchSize = 2`. Message a takes 0.6 s; message b ignores cancellation and started at 0.73 s.
  - Host B reclaimed b and delivered attempt 2 at 2.06 s, while host A's handler was still running.
  - Host A then logged 1013 (abandoned) followed by 1007 (lease lost for outcome 'retry').
- **Recommendation:**
  - Gate the start on `DeliveryTimeout + grace + margin`, or compute grace per message as `min(grace, remainingLease - DeliveryTimeout - margin)`.
  - Fix the comment.
  - CR-07 (persist budget) compounds this.
- **Status:** Open

#### FU-04 (Low): CR-09 fix makes shutdown non-prompt; with a hung database the dispatcher outlives `StopAsync` and the host's disposal

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:83-100,158-164`
- **Failure scenario:**
  - The claim is cancelled only by its own 15 s timeout. Neither `stoppingToken` nor `_abort` reaches it.
  - If the database hangs (lock, failover, network black hole) during shutdown:
    - with the default 30 s `ShutdownTimeout`, `StopAsync` now waits up to 15 s, where before the fix it returned immediately;
    - with a shorter timeout, `StopAsync` gives up after timeout + 5 s while `ExecuteAsync` keeps running. The host then disposes the service provider (including an owned `NpgsqlDataSource` and loggers) underneath the running claim, and under the CR-08 releases that follow.
  - The rationale applies only to PostgreSQL. On SQL Server, cancellation rolls back the claim (CR-09's own analysis), so there is no reason to ignore the stopping token there.
- **Evidence:** Verified.
  - Setup: PostgreSQL, an `ACCESS EXCLUSIVE` lock held on the outbox table so the claim blocks, `ShutdownTimeout = 2 s`.
  - `StopAsync` returned after 7.0 s.
  - "Dispatcher stopped" (1001) was logged at 14.1 s, about 7 s after `StopAsync` had returned.
  - No claim failure was logged; it was swallowed as a stopping-time cancellation.
- **Recommendation:**
  - Link the claim token to `_abort`. `_abort` fires only once the shutdown budget is exhausted, which is when abandoning the claim is the lesser evil.
  - For providers whose claim is transactional (SQL Server), let the stopping token cancel it.
  - Bound the post-stop work (see CR-08).
- **Status:** Open

#### FU-05 (Low): `WasRecordedByThisWorkerAsync` can report false success; the fallback path still reports a false "lease lost"

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:431-441,457-462,490-509`
- **Failure scenario:**
  1. **False success.** The check is `status == expected && locked_by == _workerId`.
     - `locked_by` identifies the worker, not the claim. If the first attempt failed **without** committing and the lease then expired, the message can be reclaimed by a worker with the same `WorkerId`. That includes this same dispatcher, which reclaims its own expired rows, and replicas sharing a configured `WorkerId`, which the docs do not forbid ("a diagnostic identifier").
     - If that reclaim records the same outcome (Delivered, or Pending after its own retry), the stale worker counts the outcome as its own. The lease-lost warning and metric are suppressed, and Delivered or Retried metrics are double-counted.
     - State is never corrupted, because no write happens on this path, so the risk is limited to telemetry.
  2. **Fallback path.** `PersistWithFallbackAsync` calls `PersistAsync` again with `previousAttemptFailed = false`. If the third primary attempt actually committed, the first fallback attempt matches 0 rows and logs 1007 "lease lost" without re-reading. This is the exact CR-10 symptom.
  3. **Cost.** `GetAsync` reads the whole row, including a payload of up to 256 MiB, during what is by definition a database incident.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Also compare `attempt_count == details.AttemptCount`. Any reclaim increments it, and a Release sets `locked_by = NULL`, so the pair is a near-exact claim identity.
  - Better: have the guarded statements leave a claim fingerprint, such as a `last_lock_token` column, and compare that.
  - Pass "previous attempt failed" into the fallback.
  - Use a narrow status query instead of `GetAsync`.
  - Add a negative unit test (FU-T3).
- **Status:** Open

#### FU-06 (Low): the fixed 15 s claim timeout overrides the provider `CommandTimeout`, so large or slow claims can never succeed

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:33,161`
- **Failure scenario:**
  - Previously the claim was bounded by the provider command timeout (PostgreSQL default 30 s, user-configurable). Now it has a hard 15 s whole-operation bound that the user cannot raise.
  - A claim that legitimately needs longer will time out on every poll. Examples: `BatchSize` × payload in the hundreds of MiB or more (validators allow 1000 × 256 MiB) over a slow link, or a claim slowed by I/O stalls.
  - Outcomes when that happens:
    - **SQL Server:** each claim rolls back, so the dispatcher makes no progress until the configuration changes.
    - **PostgreSQL:** each claim either rolls back, or (if the server already committed before cancellation) leaves the batch in Processing with an attempt counted and never dispatched. The second case is the CR-01-style loop.
  - The symptom is loud (event 1002, health Degraded), which is why the severity is Low.
- **Evidence:** Reasoned.
- **Recommendation:** Derive the claim timeout from the provider's configured `CommandTimeout` (or expose it as an option), and validate it against `LeaseDuration`. A byte cap on claims (CR-18) removes the root cause.
- **Status:** Open

#### FU-07 (Low): CR-14 residual. `FormatClrName` maps distinct types to the same message type, and arrays still embed versions

- **File:** `src/DbRelay/Serialization/MessageTypeRegistry.cs:64-77`
- **Failure scenario:**
  - The generic definition name is truncated at the first backtick, which drops the nested-type part.
  - So `Outer<int>.Inner` maps to `Outer<System.Int32>`, the same name as `Outer<int>`.
  - `Outer<int>.Inner2<string>` maps to `Outer<System.Int32,System.String>`, which looks like a two-parameter `Outer`.
  - Consumers routing by `MessageType` would misroute messages silently.
  - `List<int>[]` is not `IsGenericType`, so it still returns `FullName`: ``System.Collections.Generic.List`1[[System.Int32, System.Private.CoreLib, Version=10.0.0.0, ...]][]``.
- **Evidence:** Verified (reflection call to `FormatClrName`).
- **Recommendation:**
  - Format each nesting level, keeping `+Inner` and assigning generic arguments per level.
  - Handle arrays, pointers and by-ref element types recursively.
  - Alternatively, reject nested and array types without an explicit mapping.
- **Status:** Open

#### FU-08 (Low): CR-19 residual. Raw provider exceptions are still attached to health-check results

- **File:** `src/DbRelay/HealthChecks/DbRelayHealthCheck.cs:57,99`
- **Failure scenario:** The original finding had two parts, and only the second (unguarded statistics) was fixed. Both the Unhealthy and the new Degraded results still carry the `SqlException` or `NpgsqlException`. Common UI and JSON response writers print `exception.Message`, which includes the server name, host or port, onto often-public health endpoints.
- **Evidence:** Reasoned (code read).
- **Recommendation:** Omit the exception, or replace it with a sanitized wrapper that carries only the type name. Log the full exception at Debug instead.
- **Status:** Open

#### FU-09 (Info): abandonment lifetime details

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:277-278,288-301,314-319`
- **Notes:**
  - **Token lifetime.** The orphan keeps `linked.Token` after `timeout` and `linked` are disposed. This is safe in practice, because the `timeout` timer (DeliveryTimeout) fires before the deadline (DeliveryTimeout + grace) on the same `TimeProvider`, so the token is already cancelled. However, it relies on timer ordering: `DeliveryTimeout` under 2 ticks gives grace 0. A handler touching `token.WaitHandle` afterwards gets `ObjectDisposedException`. Cancel `linked` explicitly before abandoning.
  - **Zero grace at shutdown.** On `_abort`, the deadline is cancelled at the same instant as the handler token, so cooperative handlers get no time to unwind before the message is made claimable again (zero-delay retry). In a rolling deploy, another instance can start the redelivery while the original handler is still finishing in the dying process. Give cooperative handlers up to `_abandonGrace`, or the remaining release grace, before `ReturnInterruptedAsync`.
  - **Exceptions.** Faults are observed by `ObserveAbandoned`; cancelled tasks do not raise `UnobservedTaskException`. A 1014 log attempted after the host's logger providers were disposed may throw inside the discarded continuation. That is harmless, but it surfaces as an unobserved task exception.
  - **Scopes.** Scope disposal in `InvokeHandlerAsync` is correct for handlers that eventually return. An orphan that never returns keeps its scope forever (see FU-02).
- **Evidence:** Reasoned.
- **Status:** Open

#### FU-10 (Info): an interruption on the final attempt is dead-lettered with a misleading "crash loop" reason

- **File:** `src/DbRelay/Dispatching/OutboxDispatcher.cs:251-256,516-528`
- **Failure scenario:**
  - `ReturnInterruptedAsync` schedules a retry even when `AttemptCount >= MaxAttempts`. The next claim then dead-letters the message through the crash-loop guard, with "possible worker crash loop".
  - The bound holds, which is what `Handlers_that_always_outlast_shutdown_still_reach_dead_letter` asserts. The reason text misleads operators, and it costs one extra claim.
- **Evidence:** Reasoned.
- **Recommendation:** At the final attempt, dead-letter directly with "interrupted by host shutdown on the final attempt", or keep the retry but carry the real last error.
- **Status:** Open

### Test-suite follow-up findings

The revised `GracefulShutdownTests`, `DispatcherTests` (abandonment, CR-06 and CR-10 tests) and the conformance header test still assert meaningful outcomes: attempt kept or restored, reason text, slot freed, no late write, final owner's state. I found no assertion that was weakened just to pass. The gaps are below.

#### FU-T1 (Low): the dispatcher's lease-lost path is no longer exercised against a real database

- **File:** `tests/DbRelay.IntegrationTests/StaleWorkerTests.cs:78-165`
- **Assessment:**
  - The revised test is meaningful for CR-05: it asserts one abandonment (1013), no write attempt (no 1007), two deliveries and the final owner's state.
  - The real-database path "outcome write matches 0 rows, then (re-read), then 1007" is now covered only by the unit tests against the in-memory fake (`Lost_leases_are_detected...`, `An_acknowledgement_that_committed...`). The guard itself is covered by the store-level stale-token tests.
  - **This gap is acceptable for now**, because the composition is thin. However, FU-03 shows the path is reachable with validator-accepted configurations, and FU-05's re-read depends on provider `GetAsync` mapping of `LockedBy` and `Status`, which only a real-database test would check end to end.
- **Recommendation:** Add a deterministic integration test:
  1. Hold a handler open.
  2. From the test, expire the lease and reclaim the message with `IOutboxStore.ClaimAsync` (new token).
  3. Release the handler.
  4. Assert 1007, the new owner's row unchanged, and no false "recorded" outcome.
  Run it through the conformance suite so both providers are covered.
- **Status:** Open

#### FU-T2 (Low): header-tolerance tests miss invalid UTF-16 and non-JSON text on SQL Server

- **File:**
  - `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:494-520`
  - `tests/DbRelay.Tests/CoreTests.cs:255-274`
- **Failure scenario:** The suites pass with FU-01 present.
- **Recommendation:** Add `{"a":"\ud800"}`, `{"\udc00":"x"}` and a raw lone surrogate to the unit theory. Add a provider-conditional conformance case for SQL Server, where the column accepts arbitrary text, and assert that the healthy batch-mates are delivered.
- **Status:** Open

#### FU-T3 (Low): CR-10 has only a positive test

- **File:** `tests/DbRelay.Tests/DispatcherTests.cs:498-526`
- **Failure scenario:** Nothing asserts that the re-read does **not** mask a reclaim by the same `WorkerId` (FU-05).
- **Recommendation:** Add the negative case:
  1. The first update throws without committing.
  2. The same worker reclaims with a new token and records the same status.
  3. Assert that lease-lost is still reported.
- **Status:** Open

### Readiness verdict

**Not ready: one open High finding (FU-01, the CR-01 residual on SQL Server), no open Critical findings.** Open Medium: FU-02. The resolution log's "Open Critical/High after resolution: none" does not hold.

### Commands run in this audit

All commands were run after `. env.ps1` from the scratchpad.

| Command | Outcome |
|---|---|
| `dotnet --version` | 11.0.100 |
| `dotnet test --project tests/DbRelay.Tests/DbRelay.Tests.csproj -c Release -f net10.0` | 87/87 passed, 0 skipped (11.4 s); rebuilt the Release DLLs |
| `dotnet test --project tests/DbRelay.PostgreSql.Tests/DbRelay.PostgreSql.Tests.csproj -c Release -f net10.0` | 75/75 passed, 0 skipped (10.0 s) |
| `dotnet test --project tests/DbRelay.SqlServer.Tests/DbRelay.SqlServer.Tests.csproj -c Release -f net10.0` | 93/93 passed, 0 skipped (13.1 s) |
| `dotnet test --project tests/DbRelay.IntegrationTests/DbRelay.IntegrationTests.csproj -c Release -f net10.0` | 17/17 passed, 0 skipped (51.5 s) |
| `dotnet build fu/harness/Harness.csproj -c Release` (scratchpad; references the Release DLLs) | Succeeded, 0 warnings, 0 errors |
| Harness `hdr` | 5 inputs throw (`InvalidOperationException` or `ArgumentException`); 10 others tolerated |
| Harness `ss-surrogate` | FU-01 reproduced: 6/6 rows stuck at status 1 with attempts 6 (MaxAttempts 3); 0/5 healthy delivered; 6 × event 1002 |
| Harness `ss-own` | DbRelay's own writer emits U+FFFD; PostgreSQL and SQL Server each delivered 3/3 healthy messages plus the header message; 0 claim failures |
| Harness `pg-rows` | `jsonb` rejects `\ud800` (22P02) and `\u0000` (22P05); a row with `created_at` in year 10000 is claimed without error |
| Harness `stats` (1,000,000 rows) | Counts exact; partial indexes used; 188 ms execution |
| Harness `accum` | FU-02: 33 hung handlers and 34 thread-pool threads after 30 s, with MaxConcurrency 2 |
| Harness `grace` | FU-03: reclaim delivered at 2.06 s while host A's handler was still running; host A logged 1013 then 1007 |
| Harness `hang` | FU-04: `StopAsync` returned at 7.0 s (timeout 2 s); dispatcher stopped at 14.1 s |
| Harness `quick` | CR-02, CR-13, CR-20 and CR-22 confirmed; FU-07 collisions shown |
| Harness `cleanup-check` | 0 `fu_*` schemas on PostgreSQL and on SQL Server |

Not run: the full solution build across net8.0, net9.0 and net11.0; `dotnet format --verify-no-changes`; `dotnet pack`; tests on target frameworks other than net10.0.

---

## Follow-up resolution log (principal architect, 2026-11-22)

The follow-up audit above **correctly refuted** the first resolution log's "no open High". CR-01 was only partly fixed (FU-01).
Each item below was re-verified by running the suites (1,140 test executions across 4 TFMs, 0 failed, 0 skipped). Two fixes were
also **mutation-checked**: the fix was reverted temporarily, the new test confirmed failing, and the fix restored.

| ID | Sev | Resolution | Evidence |
|---|---|---|---|
| FU-01 | High | **Fixed, two layers.** (1) `OutboxHeaders.Deserialize` catches every exception, not just `JsonException`. (2) Defence in depth: both providers wrap per-row mapping. A row that can't be mapped becomes an `UnreadableRow` placeholder (reserved header `dbrelay-row-error`), which the dispatcher dead-letters under its lock token without calling the handler. Admin reads tolerate it too. | SQL Server test `Claim_tolerates_headers_that_are_invalid_json_or_invalid_utf16` (RCSI off and on) **fails with the old `catch (JsonException)` and passes with the fix**. Unit: 4 new invalid-UTF-16 header cases plus a runtime-built raw lone surrogate (a raw surrogate inside an attribute argument does not survive metadata encoding), placeholder tests, and dispatcher test `Unreadable_rows_are_dead_lettered_without_calling_the_handler_and_do_not_block_the_batch`. Finding: a raw lone surrogate written through SqlClient comes back normalised to valid text, so on SQL Server it is an ordinary header (claimed and readable). |
| FU-02 | Medium | **Fixed.** No new delivery starts, and no claim is made, while live abandoned handlers ≥ `max(2, 2 × MaxConcurrency)`. Headroom so one hung handler doesn't stall a `MaxConcurrency = 1` dispatcher (a first attempt with the limit = MaxConcurrency re-created CR-05, and the existing test caught it). Live orphans are bounded by limit + MaxConcurrency. New gauge `dbrelay.delivery.abandoned.active`; the health check reports Degraded while any orphan is alive. | Unit `Claiming_pauses_while_abandoned_handlers_fill_every_slot`: orphans bounded and stable, no claims at the limit, Degraded, recovery after release. |
| FU-03 | Low | **Fixed.** The start gate is now `DeliveryTimeout + grace + margin`, measured from delivery start. Validation (lease ≥ 2T) guarantees it holds at claim time (1.7T). | Reasoned; covered by the existing lease-time release test. |
| FU-04 | Low | **Fixed.** The claim is linked to the shutdown-timeout abort token, but still not to the stopping token (CR-09). | Integration `Shutdown_is_prompt_even_when_the_claim_is_blocked_by_the_database` (ACCESS EXCLUSIVE lock, 2 s shutdown timeout) **fails without the link (~19 s) and passes with it**. |
| FU-05 | Low | **Fixed.** The re-read requires the same status, `locked_by` and **attempt count**, so it identifies the claim and not only the worker. The fallback path carries the earlier-failure flag. The narrow status query was not done; the rare path keeps `GetAsync`. | Unit `Ambiguous_acknowledgement_after_a_reclaim_is_still_reported_as_a_lost_lease` (closes FU-T3) plus the existing positive CR-10 test. |
| FU-06 | Low | **Fixed.** The claim safety timeout is 5 minutes, so the provider `CommandTimeout` governs normal operation. | Reasoned. |
| FU-07 | Low | **Fixed.** Nesting levels are formatted separately (`Outer<System.Int32>+Inner2<System.String>`) and arrays recursively. | Unit `Nested_generic_and_array_type_names_are_distinct_and_version_free`. |
| FU-08 | Low | **Fixed.** Health results attach no exception, only `data["error"]` = exception type name. | Reasoned. |
| FU-09 | Info | **Fixed (partly).** The linked token is cancelled explicitly before abandonment; logging an abandoned handler's late failure tolerates a disposed logger. Accepted: at shutdown, cooperative handlers get no extra unwind time; an orphan that never returns keeps its scope (now bounded by FU-02). | |
| FU-10 | Info | **Fixed.** The dead-letter reason says "the last attempt was interrupted by host shutdown" when that is the cause. | Unit `Handlers_that_always_outlast_shutdown_still_reach_dead_letter` asserts the new reason. |
| FU-T1 | Low | **Fixed.** New integration test `Real_dispatcher_whose_lease_was_taken_over_cannot_overwrite_the_new_owner`: a real dispatcher mid-delivery, its lease expired with SQL, taken over by another worker. Event 1007 is logged and the new owner's row is untouched. | Passes on all TFMs. |
| FU-T2, FU-T3 | Low | **Fixed.** See FU-01 and FU-05. | |

**Open Critical/High: none.** No independent re-audit of these FU fixes has been done yet. The evidence is the architect's
test runs plus the two mutation checks.

---

## Batched acknowledgements review (2026-11-22)

**Reviewer:** code-reviewer (independent; did not write this code)

**Scope:**
- `src/DbRelay/Storage/IOutboxBatchStore.cs`
- `src/DbRelay/Dispatching/AcknowledgementBatcher.cs`
- `OutboxDispatcher` (creation and completion of the batcher, the batched delivered path, batched `ReleaseAsync`)
- the `DispatcherState` abandoned-handler counter
- the PostgreSQL and SQL Server batch statements and stores
- `DispatcherOptions.BatchAcknowledgements`
- the new tests

ADR 0003, code comments and the benchmark docs were treated as claims to check, not as evidence.

**Method:**
- Read the code.
- Re-ran the four test projects in the repository on net10.0.
- Ran a scratch harness against PostgreSQL 17.5 (localhost:55432) and SQL Server Express (`dbrelay_tests` and `dbrelay_tests_rcsi`). The harness drives the real stores, and the internal `AcknowledgementBatcher` through reflection.
- Ran mutation checks in a **copy** of the repository in the scratchpad (`ba\mut`), so no repository source file was ever modified. Every mutated file in the copy was restored, and its SHA-256 matched the repository file afterwards. `AcknowledgementBatcher.cs` hash: `615CECE1…2395`, unchanged in the repository before and after.
- Cleanup: every scratch schema (`ba_*`) was dropped, and 0 remain on all three databases. The `soak_*` schemas and the `DbRelay.Soak` processes were not touched.

**Evidence:**
- **Verified** means reproduced or measured by running something.
- **Reasoned** means derived from reading the code.

### Test results (run by me, net10.0, Release, in the repository)

| Project | Result |
|---|---|
| `DbRelay.Tests` | 101/101 passed, 0 skipped |
| `DbRelay.PostgreSql.Tests` | 79/79 passed, 0 skipped |
| `DbRelay.SqlServer.Tests` | 103/103 passed, 0 skipped (RCSI off and on) |
| `DbRelay.IntegrationTests` | 19/19 passed, 0 skipped |

### Mutation checks (in the scratchpad copy)

| # | Mutation | Result |
|---|---|---|
| M1 | Old loop condition `while (!pending.Completion.Task.IsCompleted && _leader.Wait(0))` | **The architect's claim holds.** `A_follower_that_enqueues_just_before_the_leader_releases_is_not_stranded` fails with `TimeoutException` at `DispatcherTests.cs:437`, and passes on the restored code. `Acknowledgement_batcher_never_strands_a_waiter_under_heavy_contention` **passed 3/3 runs** under the same mutation (BA-11). |
| M2 | Batcher re-read disabled | Killed by a single test, `An_acknowledgement_that_committed_but_timed_out_is_not_reported_as_a_lost_lease`, which takes the one-item fast path (BA-12). |
| M3 | SQL Server batch join without `m.[lock_token] = a.[token]` (both batch statements) | Killed: 4 conformance failures (RCSI off and on). |
| M4 | PostgreSQL `MarkDeliveredBatchSql` without the token predicate | Killed: 2 conformance failures. |
| M5 | PostgreSQL `ReleaseBatchSql` without the token predicate | **Survived** the full PostgreSQL suite (79/79) (BA-02). |
| M6 | SQL Server release batch without the token predicate | **Survived** the full SQL Server suite (103/103), and with M5 the integration suite (19/19) (BA-02). |

### Answers to the review questions

1. **Leader/follower protocol (up to 4 leaders).**
   - **No lost wake-up on the normal path** (Reasoned; M1 confirms the fix). A follower's failed `Wait(0)` means every slot was held after its enqueue. Each holder re-checks `IsEmpty` after releasing, and any thread that wins a slot drains until it sees the queue empty.
   - **An item is never flushed twice or by two leaders:** `ConcurrentQueue.TryDequeue` hands each item out once.
   - **`CompleteAsync` cannot deadlock or miss items in the dispatcher.** It runs after `Task.WhenAll` over every tracked delivery, and every batcher caller is a tracked delivery, so the queue is empty and every slot is free. It does not re-check the queue after releasing (BA-09).
   - **No race with `Dispose`:** `_batcher.Dispose()` runs after `WhenAll` and `CompleteAsync`. The dispatcher's own `Dispose` does not touch the batcher.
   - **Abandoned handlers and shutdown:** a delivery whose handler was abandoned, or that was interrupted at shutdown or abort, never reaches the batcher (`OutboxDispatcher.cs:346-376`), so a leader is always a successful delivery. The batcher ignores `_abort`; each flush is bounded by 3 × 15 s plus the re-reads.
   - **Exception paths are not covered.** If logging throws on the leader's error path, followers are stranded (BA-01, Verified).
2. **Guard semantics.**
   - **Every batched row is guarded by its own token** on both providers. Verified by M3 and M4, and by harness runs where a stale `ReleaseBatch` item left the new owner's row untouched on all three databases.
   - **SQL Server padding never outputs or updates a row twice** (Verified):
     - 3 distinct items in bucket 4 produced 3 OUTPUT rows;
     - 1 item repeated 64 times produced 1 row;
     - the plan has a `Distinct Sort` over the `VALUES` rows and a `Stream Aggregate` before the update.
   - **Mis-report:** results are keyed by message ID only, so a stale-token duplicate of a current item is reported as recorded (BA-03, Verified).
   - **`[status] = 1`:** correct and redundant, because a non-null token implies Processing. It is consistent per provider: SQL Server has it in the single-row and batch statements, PostgreSQL in neither.
3. **Failure handling.**
   - **No false "recorded" from the re-read path** (Reasoned). A false "lease lost" occurs only when the re-read itself fails, which is the same conservative behaviour as the single-row path.
   - **Other false "recorded" sources:** BA-03, and theoretically BA-08.
   - **Deadlocks:** no production cycle found (BA-07).
     - SQL Server orders lock acquisition by (id, token) through the plan's `Distinct Sort` (Verified plan). Claims use `READPAST`, and single-row statements lock one row (Reasoned).
     - A stale SQL Server item does wait on a locked row (`LCK_M_U`, both RC and RCSI, Verified).
     - PostgreSQL does not wait on stale items (Verified).
   - **SQL Server deadlock retry is safe:** the statement is autocommitted and fully rolled back, and it is guarded. The result set is shared across attempts (BA-08).
4. **Liveness and fairness.**
   - **A leader keeps its slot while acknowledgements keep arriving** (BA-05, Verified). There is no unbounded stall:
     - the queue holds at most `MaxConcurrency` items, because every caller holds a slot;
     - after stop, no new deliveries start, so leaders exit.
   - **Worst-case waits grow under database distress**, because re-reads are serial (BA-04, Verified).
5. **Performance.**
   - **SQL Server:**
     - at most 128 parameters per statement (64 × 2), far below the 2,100 limit;
     - 12 batch statement texts per table (6 buckets × 2 operations), so no plan-cache bloat;
     - `FORCESEEK` gives `ux_..._id` seeks plus key lookups (Verified plan), and padding costs no extra seeks because the `Distinct Sort` removes duplicates first.
   - **PostgreSQL:** one text per operation. With 200,000 rows, both custom and generic plans are a nested loop over `outbox_messages_pkey`: 0.15–0.7 ms for 2 items and 3.9–4.9 ms for 256 items (Verified).
   - **`MaxBatchSize = 500` is unreachable** (BA-09). With `MaxConcurrency` above 64, SQL Server runs up to 4 sequential chunks inside one 15 s timeout.
6. **Tests.**
   - The architect's mutation claim holds (M1).
   - The contention test does not detect the regression (BA-11).
   - The release guard is untested (BA-02).
   - Other gaps are listed in BA-12.

### Findings

| Severity | Count | IDs |
|---|---|---|
| Critical | 0 | none |
| High | 0 | none |
| Medium | 2 | BA-01, BA-02 |
| Low | 6 | BA-03, BA-04, BA-05, BA-06, BA-11, BA-12 |
| Info | 4 | BA-07, BA-08, BA-09, BA-10 |

#### BA-01 (Medium): the leader's error path is not exception-safe; a throwing logger strands followers and leaks their slots

- **File:** `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:72-91, 141-149, 176-191`
- **Failure scenario:**
  - `Log.OutcomePersistRetry` (`:179`), `Log.OutcomePersistFailed` (`:184`) and `Log.ProcessingFailed` (`:144`) run **before** the pending items are completed. `Microsoft.Extensions.Logging.Logger` rethrows provider failures as `AggregateException`.
  - If logging throws while the database is failing, two things go wrong:
    - (a) items already dequeued into the failed batch are never completed;
    - (b) the leader leaves `MarkDeliveredAsync` by exception without re-checking the queue, so followers that are still queued wait until some later acknowledgement arrives, possibly never.
  - Each stranded follower holds a delivery slot. `ExecuteAsync`'s `Task.WhenAll` then never completes, so shutdown always runs into the host timeout, and the batcher ignores `_abort`.
  - The unbatched path has no equivalent: a throwing logger there faults one delivery task, and its slot is released.
- **Evidence:** Verified. Harness `throwlog`: logger that throws, store that fails, `maxConcurrency` 1, 3 followers. Result: the leader threw `AggregateException`, **0/3 followers completed within 5 s**, and 3 were still queued.
- **Recommendation:**
  - Complete every dequeued item in a `finally`, failing anything not yet completed with `null`, before any logging.
  - Wrap the batcher's log calls so that they cannot throw.
  - On any exception, the leader must still release its slot and re-check the queue.
  - Add a unit test with a throwing logger.
- **Status:** Open

#### BA-02 (Medium): no test protects the lock-token guard of `ReleaseBatchAsync` on either provider

- **File:**
  - `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:589-612`
  - guards at `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:100-107` and `src/DbRelay.SqlServer/Internal/SqlServerOutboxSql.cs:117-119,295-302`
- **Failure scenario:**
  - The release test pairs its only mismatched token with a row that is also listed with its valid token, so an unguarded release produces the same result.
  - The guard is load-bearing. `DispatchBatchAsync` batch-releases unstarted messages on the "insufficient lease time remaining" path (`OutboxDispatcher.cs:251-259`), which is exactly when another worker may already have reclaimed them.
  - An unguarded regression would reset the new owner's claim to Pending, decrement its attempt count, and clear `locked_by`. That violates invariant 4, and no test would notice.
- **Evidence:** Verified.
  - M5 and M6 (token predicate removed from the release batch statement) survive the full PostgreSQL suite (79/79), the SQL Server suite (103/103) and the integration suite (19/19).
  - The current code is correct. Harness `release-guard` on all three databases: `ReleaseBatch([(X, stale), (Z, valid)])` released only Z, and X stayed Processing with attempt 2 and `locked_by = w2`.
- **Recommendation:** Add a conformance test:
  1. Claim X and Z with T1.
  2. Expire X and reclaim it with T2.
  3. Call `ReleaseBatchAsync([(X, T1), (Z, T1)])`. Use at least 2 items, so that SQL Server takes the batch statement and not the single-row path.
  4. Assert the result is {Z}, and that X still has status 1, its attempt count and `locked_by`.
- **Status:** Open

#### BA-03 (Low): batch results are keyed by message ID only, so a stale-token duplicate is reported as "recorded"

- **File:**
  - `src/DbRelay/Storage/IOutboxBatchStore.cs:28-29`
  - `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:195-200`
  - `PostgreSqlSettings.cs:97`
  - `SqlServerOutboxSql.cs:298`
- **Failure scenario:**
  - An acknowledgement for claim T1 is delayed past its lease (for example by BA-04, or a slow database). The same dispatcher reclaims the message with T2 and delivers it again.
  - (X, T1) and (X, T2) are then flushed in one batch. Only T2 matches, but the result `{X}` makes **both** waiters report `true`.
  - Consequences: the lease-lost warning (1007) and the `dbrelay.lease.lost` metric are suppressed, and "Delivered" is logged and counted twice. The database state is correct.
  - The return type is part of a public SPI. Fixing this after 1.0 would be a breaking change.
- **Evidence:** Verified.
  - Store level, on PostgreSQL, SQL Server RC and SQL Server RCSI: the result count is 1 and contains the ID.
  - Batcher level, on PostgreSQL: stale-token waiter `True`, current-token waiter `True`, and the row was updated once.
- **Recommendation:**
  - Return the matched (id, token) pairs. PostgreSQL: `RETURNING m.id, a.token`. SQL Server: `OUTPUT inserted.[id], deleted.[lock_token]`.
  - Alternatively, have the batcher send duplicate IDs in separate round trips.
  - Add a test.
- **Status:** Open

#### BA-04 (Low): after an ambiguous batch failure, re-reads run one at a time while every batch member holds its slot

- **File:**
  - `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:196-211`
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:240-285`
- **Failure scenario:**
  - Every item missing from a retried attempt is re-read with a sequential full-row `GetAsync`. That row includes the payload, up to 256 MiB.
  - Each re-read can take up to the 15 s timeout, and the leader and all followers in the batch wait for all of them. Worst case is about `MaxConcurrency × 15 s`: 2 min at the default of 8, about 64 min at 256. That comes on top of up to about 46 s of attempts.
  - Unbatched deliveries re-read in parallel, about 15 s each.
  - On SQL Server, a failure in chunk *k* discards the results of the already committed chunks 1 to *k*−1, which forces those re-reads.
  - Followers' acknowledgements can then land after their leases expire. Delivery stays at-least-once, but redeliveries and false lease-lost reports increase.
- **Evidence:** Verified. Probe: 8 followers, re-read 200 ms each. Result: 8 re-reads at peak concurrency 1; the first follower completed after 429 ms and all of them after 1,866 ms. Parallel re-reads would take about 400 ms.
- **Recommendation:**
  - Re-read the missing items with one narrow query (`id, status, attempt_count, locked_by` for the set of IDs), or in parallel with a bound.
  - Keep per-chunk results on SQL Server.
- **Status:** Open

#### BA-05 (Low): under sustained load a leader holds its delivery slot indefinitely, and up to 4 slots can be held this way, not "at most one"

- **File:**
  - `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:47,72-91`
  - `docs/decisions/0003-spi-evolution-and-batched-acknowledgements.md:23,31-32,52`
- **Failure scenario:**
  - A leader returns only when it observes an empty queue after releasing its slot. While acknowledgements keep arriving, its own delivery holds its slot, and its "Delivered" log and `RecordActivity` are delayed, although its own row was recorded long before.
  - With `_maxLeaders = clamp(MaxConcurrency / 4, 1, 4)`, up to 4 slots (25% of concurrency) can be held this way.
  - The ADR says "a single flusher" and "this costs at most one slot", and that slot holding is "as before". All three are now inaccurate.
  - There is no deadlock: the queue holds at most `MaxConcurrency` items, and it drains after stop (Reasoned).
- **Evidence:** Verified. Probe with a 20 ms flush for 3 s:
  - 4 slots (1 leader): the longest `MarkDeliveredAsync` took 1,396 ms, about 70 flush times;
  - 8 slots (2 leaders): 643 ms.
- **Recommendation:**
  - Bound leadership: after the leader's own item is done and K more rounds have run, hand off explicitly to a waiting follower. A simple early exit would strand followers.
  - Correct the ADR and the class documentation.
- **Status:** Open

#### BA-06 (Low): a failed batch logs only the first message ID; the other N−1 messages are never named

- **File:**
  - `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:144,179,184`
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:398-417`
- **Failure scenario:**
  - Events 1008, 1009 and 1011 carry `batch[0].Details.Id`, with the outcome `"delivered (batch of N)"`.
  - When the result is `null`, `DeliverAsync` logs nothing. The other messages that may be redelivered are therefore never logged.
  - The structured `Outcome` field now takes unbounded values, which breaks filters on `Outcome = "delivered"`.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Log a per-message 1009 in `DeliverAsync` when `recorded == null`.
  - Keep `Outcome = "delivered"` and add the batch size as a separate field.
- **Status:** Open

#### BA-07 (Info): SQL Server stale batch items wait on locked rows; no production deadlock cycle found

- **File:**
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxSql.cs:295-302`
  - `src/DbRelay.PostgreSql/Internal/PostgreSqlSettings.cs:92-107`
- **Observation:**
  - **SQL Server:** an item whose token no longer matches still requests a U lock on its row. While another session holds that row, the **whole** batch waits (`LCK_M_U`, under both RC and RCSI).
  - **PostgreSQL:** stale items do not wait (156 ms with the row locked).
  - **No cycle** was found between batches, claims (`READPAST`), single-row statements, cleanup or requeue:
    - the plan sorts the `VALUES` rows with a `Distinct Sort` on (id, token), then seeks `ux_..._id` in that order;
    - a blocking `Sort` on `sequence` precedes the update, so all batches acquire locks in one global order.
  - The harness produced a 1205 only by holding a lock across two statements in an explicit transaction, which production never does.
  - **Residual risk:** the ordering is an optimizer choice. A hash aggregate would lose it.
- **Evidence:**
  - Verified: the blocking behaviour, the plan operators and their sort keys.
  - Reasoned: the absence of a cycle.
- **Recommendation:**
  - Sort items by ID on the client before sending them (defence in depth; it also makes the PostgreSQL lock order deterministic).
  - Add a SQL Server stress test with concurrent batches, claims and stale items.
- **Status:** Open

#### BA-08 (Info): the SQL Server deadlock retry reuses one result set across attempts

- **File:**
  - `src/DbRelay.SqlServer/Internal/SqlServerOutboxStore.cs:240,257-282`
  - `src/DbRelay.SqlServer/Internal/SqlServerConnectionSource.cs:66-87`
- **Observation:**
  - `updated` is filled inside the lambda that `ExecuteAsync` retries on error 1205. SQL Server may return OUTPUT rows of a statement that then fails and rolls back. Those IDs would stay in the result, and if the retry did not update them (a lease expired and the row was reclaimed in the 5–50 ms gap), they would be reported as recorded.
  - With the current plan, 0 OUTPUT rows reached the client before 1205, because the blocking `Sort` precedes the update. The issue is therefore theoretical.
  - Otherwise the retry is safe: the statement is autocommitted, fully rolled back and guarded.
- **Evidence:**
  - Verified: 0 rows were read before 1205.
  - Reasoned: the rest.
- **Recommendation:** Collect results per attempt, and merge them only when the attempt succeeds.
- **Status:** Open

#### BA-09 (Info): stale comments, unreachable limits, and a `CompleteAsync` precondition

- **File:**
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:157`
  - `src/DbRelay/Dispatching/AcknowledgementBatcher.cs:18,97-113`
  - ADR 0003 `:23`
- **Notes:**
  - `OutboxDispatcher.cs:157` says "this just stops its flusher", but there is no flusher.
  - `MaxBatchSize = 500` is unreachable: every caller holds a delivery slot, so the queue holds at most `MaxConcurrency` (≤ 256) items.
  - `CompleteAsync` releases its slots without re-checking the queue. It is safe only because nothing can call `MarkDeliveredAsync` concurrently. Document that precondition, or assert it (`QueuedCount == 0`).
  - The test seam `BeforeLeaderReleaseForTesting` is checked in production on every leadership round. Its cost is negligible.
- **Evidence:** Reasoned.
- **Status:** Open

#### BA-10 (Info): the abandoned-handler counter is sound, but a throwing cancellation callback skips it

- **File:**
  - `src/DbRelay/Dispatching/DispatcherState.cs:54-58`
  - `src/DbRelay/Dispatching/OutboxDispatcher.cs:351-352,471-495`
- **Observation:**
  - The single atomic counter is correct. There is one increment per `ObserveAbandoned`, one decrement in its continuation (which runs inline if the handler already finished), and readers use `Volatile.Read`.
  - However, `await linked.CancelAsync()` runs **before** `ObserveAbandoned`. If a cancellation callback that the handler registered throws:
    - `CancelAsync` throws `AggregateException`;
    - the orphan is never counted, which bypasses the FU-02 cap, and its fault is never observed;
    - the message is scheduled for retry while the handler is still running.
- **Evidence:** Reasoned.
- **Recommendation:** Call `ObserveAbandoned` first, or catch exceptions from `CancelAsync`.
- **Status:** Open

#### BA-11 (Low, tests): the contention test does not detect the lost-wake-up regression

- **File:** `tests/DbRelay.Tests/DispatcherTests.cs:386-410`
- **Failure scenario:**
  - The test's comment presents it as the regression test for this race.
  - With the old loop condition (M1), it passed 3/3 runs on this machine. Only the deterministic hook test (`:413-438`) catches the regression.
- **Evidence:** Verified (M1).
- **Recommendation:**
  - Describe it as a smoke test, or make it effective, for example by enabling a randomized delay in `BeforeLeaderReleaseForTesting`.
  - Keep the deterministic test.
- **Status:** Open

#### BA-12 (Low, tests): important paths that are untested

- **File:**
  - `tests/DbRelay.Tests/DispatcherTests.cs`
  - `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs`
- **Gaps:**
  - (a) **Multi-item ambiguous commit followed by a re-read.** `CommitThenFailOnce` is used only in single-message tests, which take the one-item fast path. M2 is killed only by that test. The fake already models a partial multi-item commit.
  - (b) **SQL Server one-item tail chunk and partial chunk failure.** The tail chunk occurs at 65, 129 and 193 items. The full-batch test uses 1,000 items (15 × 64 + 40), which never produces a one-item tail chunk.
  - (c) **Duplicate IDs with different tokens** (BA-03).
  - (d) **Fallback of the dispatcher's batched `ReleaseAsync`** to per-message releases when `ReleaseBatchAsync` throws.
  - (e) **Exception safety of the batcher** (BA-01).
  - (f) **SQL Server lock behaviour under concurrent batches** (BA-07).
- **Evidence:** Verified for (a) by M2 and a grep; Reasoned for the rest.
- **Recommendation:** Add the tests listed; (a), (b) and (d) are cheap with the existing fakes and conformance helpers.
- **Status:** Open

### Commands run in this review

All commands were run after dot-sourcing `env.ps1` from the scratchpad.

| Command | Outcome |
|---|---|
| `dotnet test --project tests/DbRelay.Tests/DbRelay.Tests.csproj -c Release -f net10.0` | 101/101 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.PostgreSql.Tests/DbRelay.PostgreSql.Tests.csproj -c Release -f net10.0` | 79/79 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.SqlServer.Tests/DbRelay.SqlServer.Tests.csproj -c Release -f net10.0` | 103/103 passed, 0 skipped |
| `dotnet test --project tests/DbRelay.IntegrationTests/DbRelay.IntegrationTests.csproj -c Release -f net10.0` | 19/19 passed, 0 skipped (55.7 s) |
| `robocopy` of the repository (excluding bin, obj and .git) to `scratchpad\ba\mut` | Copy for mutation checks |
| Baseline: deterministic test in the copy (`--filter-method`) | 1/1 passed |
| M1: deterministic test | **Failed**: `TimeoutException` at `DispatcherTests.cs:437` |
| M1: contention test × 3 (`--no-build`) | Passed 3/3 |
| M2: full `DbRelay.Tests` | 100/101; only the CR-10 single-message test failed |
| M3: SQL Server `*Batch_*` | 6/10; 4 failed |
| M4: PostgreSQL `*Batch_*` | 4/6; 2 failed |
| M5: full PostgreSQL suite | 79/79 passed (mutation survived) |
| M6: full SQL Server suite | 103/103 passed (mutation survived) |
| M5 + M6: integration suite | 19/19 passed (mutations survived) |
| Restore of the copy, then hash comparison with the repository | 3/3 files identical |
| `dotnet build scratchpad\ba\harness` (references the Release DLLs) | Succeeded, 0 errors |
| Harness `ss-dup` (RC and RCSI) | Padding output 3 rows of 3; 64 copies output 1 row; same ID with two tokens gave result `{id}`; plan: `Distinct Sort` → `ux_id` seek → key lookup → `Sort(sequence)` → `Stream Aggregate` → update |
| Harness `ss-deadlock` (RC and RCSI) | A stale item blocks on `LCK_M_U`; a cycle needs a multi-statement transaction (1205 was raised with 0 OUTPUT rows read first) |
| Harness `pg-block` | A stale item does not wait (156 ms); same ID with two tokens gave result `{id}` |
| Harness `pg-explain` (200,000 rows) | Nested loop over the primary key in custom and generic plans; 256 items in 3.9–4.9 ms |
| Harness `batcher` | BA-03 confirmed at batcher level; re-reads serial (peak 1, 1,866 ms for 8); leader held 1,396 ms at 4 slots |
| Harness `throwlog` | BA-01: leader threw; 0/3 followers completed in 5 s |
| Harness `release-guard` | Stale release item untouched on all three databases |
| Harness `cleanup-check` | 0 `ba_*` schemas on PostgreSQL, `dbrelay_tests` and `dbrelay_tests_rcsi` |

**Not run:**
- the full solution build, which would rebuild `DbRelay.Soak` under the running soak;
- other target frameworks;
- `dotnet format`;
- `dotnet pack`.

### Verdict

**No open Critical or High findings for batched acknowledgements. Two open Medium findings: BA-01 (followers stranded when logging throws) and BA-02 (release token guard untested).**

---

## Batched acknowledgements: resolution log (principal architect, 2026-11-22)

All findings above were addressed or accepted. Re-verified on net10.0: unit 104/104, PostgreSQL 84/84, SQL Server 113/113,
0 skipped. Mutation-checked: the BA-02 test fails when the release token guard is removed, on PostgreSQL and on SQL Server
(RCSI off and on), where previously that mutation survived every suite.

| ID | Sev | Resolution | Evidence |
|---|---|---|---|
| BA-01 | Medium | **Fixed.** `DrainAsync` never throws: every dequeued item is completed in a `finally`, and all batcher logging goes through a non-throwing `SafeLog`. The leader therefore always releases and re-checks the queue. | Unit `A_throwing_logger_and_a_failing_store_never_strand_waiters` (every log call throws, every store call fails; all 12 waiters complete with null) |
| BA-02 | Medium | **Fixed (test).** The guard was already correct. | Conformance `Batch_release_never_touches_a_message_reclaimed_by_another_worker`; mutation-checked on both providers |
| BA-03 | Low | **Fixed.** Pre-release SPI change: `IOutboxBatchStore` methods return matched `OutboxAcknowledgement` (id, token) pairs (PostgreSQL `RETURNING m.id, a.token`; SQL Server `OUTPUT inserted.[id], deleted.[lock_token]`), and the batcher matches on pairs. | Conformance `Batch_results_distinguish_a_stale_and_a_current_token_for_the_same_message` |
| BA-04 | Low | **Fixed (partly).** Re-reads after an ambiguous failure run in parallel, bounded at 8. Accepted: on SQL Server, a mid-batch chunk failure still forces re-reads of committed chunks (correct, just slower). | Unit `A_multi_item_batch_that_committed_but_reported_failure_is_recovered_by_re_reading` |
| BA-05 | Low | **Accepted; documentation corrected.** ADR 0003 and class remarks now state up to `clamp(MaxConcurrency/4,1,4)` slots and the delayed leader log/activity. A bounded hand-off is deferred. | |
| BA-06 | Low | **Fixed.** A failed batch logs event 1016 (with batch size) plus event 1009 per message with `Outcome = "delivered"`. Retries log 1015. | |
| BA-07 | Info | Accepted. The plan orders locks by (id, token); no cycle found. Client-side sorting would not match SQL Server `uniqueidentifier` ordering. A stress test is a follow-up. | |
| BA-08 | Info | **Fixed.** SQL Server results are collected per attempt and merged only on success. | |
| BA-09 | Info | **Fixed.** Stale comments corrected; `MaxBatchSize` documented as a safety limit; the `CompleteAsync` no-concurrent-callers precondition is documented. | |
| BA-10 | Info | **Fixed.** `ObserveAbandoned` runs before `CancelAsync`, and exceptions from the handler's cancellation callbacks are caught. | |
| BA-11 | Low | **Fixed.** Renamed `..._under_heavy_contention_smoke`, with a comment pointing to the deterministic regression test. | |
| BA-12 | Low | **Fixed (most).** Added: a multi-item ambiguous commit (a real 2-item batch, asserted); SQL Server chunk tails at 65/129/100 items with a stale tail; duplicate ids with different tokens; the dispatcher's per-message fallback when batched release throws. Not added: a SQL Server lock-behaviour stress test (BA-07 follow-up). | Unit `Shutdown_falls_back_to_per_message_release_when_the_batched_release_fails`; conformance `Batch_acknowledgements_handle_chunk_boundaries` |

**Open Critical/High: none. Open Medium: none.**

## Resolution log: 0.2 engineering backlog (principal architect, 2026-11-22)

Each item was re-run on all four target frameworks. Mutation checks: the fix was reverted temporarily, the new test was confirmed failing, and the fix was restored.

| ID | Resolution | Evidence |
|---|---|---|
| T-03 | **Fixed.** A realistic-table plan test asserts the planner's own choices. | `Planner_chooses_the_partial_indexes_on_a_realistic_table` (100k rows; fails against the pre-CR-03 statistics query) |
| CR-21 | **Fixed without an index.** A `(created_at, id)` index measured +25-28% insert cost. Instead, PostgreSQL sorts only the narrow keys, as SQL Server already does, and `Skip` is capped at 10,000 (ADR 0004). | Conformance paging test; 1M-row measurements in ADR 0004 |
| CR-18 | **Fixed.** A 64 MiB payload budget per enqueue call and per claim (ADR 0004). | `Claim_stops_at_the_payload_budget_but_always_takes_the_first_message` (mutation-checked on both providers); unit test for the enqueue cap |
| CR-11 / T-05 | **Resolved as documented behaviour** (see the checklist). Both behaviours are tested, and SQL Server now raises a guiding error. | `Null_transaction_while_a_transaction_is_in_progress_*` (PostgreSQL and SQL Server) |
| BA-05 | **Fixed.** Leadership is bounded to 4 rounds and then handed off (release first, then promote). | Load test: before the fix, one acknowledgement waited 4.3 s of a 6.8 s burst; after it, every acknowledgement finished within 500 ms. Deterministic hand-off test (fails when the promotion is removed). Throughput A/B against the old batcher and this morning's commit: no regression. |
| BA-07 | **Stress test added.** It found 3 deadlock cycles, now fixed or documented (below). | `Concurrent_claims_batches_stale_acknowledgements_and_cleanup_never_deadlock`; deadlock graphs from `system_health` |
| K13 | **Fixed.** Test schemas are timestamped, and stale ones are swept. | Planted stale and fresh schemas, verified end to end |

**BA-07 deadlock findings (SQL Server, RCSI off):**

1. **Batch vs batch.** Each statement held a U lock on one key of `ux_..._id` and waited on the other.
   - Cause: overlapping stale and current acknowledgements, locked in opposite orders. The plan does not always sort the `VALUES` rows, so the review's "one global order" premise did not hold.
   - Fix: items are sent in `SqlGuid` order.
   - Before the fix, 5 of 48 stress runs failed with all three deadlock retries exhausted.
2. **Claim payload-budget read vs acknowledgement update.** The budget read waited for an S lock on a clustered row while holding a U lock on its claim-index key.
   - This regression came from the CR-18 change in this cycle.
   - Fix: `READPAST`, as in claim phase 2.
3. **Retention cleanup vs a stale batch acknowledgement.** These locks are taken in opposite orders.
   - This occurs only when `DeliveredRetention` is shorter than the time an acknowledgement can arrive after delivery. The test forced it with a retention of zero.
   - Documented in `docs/configuration.md`. The statement is retried. A `READPAST` fix was rejected: it could make a current acknowledgement report a false lost lease.

**Results:** 0 failures in 96 stress runs with all fixes. Mutation checks were **inconclusive**. Reproduction depends on machine load: the unfixed code failed 5 of 48 runs at one time and 0 of 48 later. The deadlock graphs are the evidence for cycles 1 and 2.

**Also found:** two 1205 reports from 09:08 UTC came from the BA review harness's explicit-transaction scenario, not from a production path. A targeted repro of duplicate items gave 0 deadlocks in 12,000 parameterized statements.

---

## Oracle provider review (ADR 0005), 2026-11-26

Independent review of branch `oracle-provider` (`git diff main...oracle-provider`, at `525d83c`; the last commit changed only
documentation, not code or tests). Scope:
- `src/DbRelay.Oracle/**`;
- `tests/DbRelay.Oracle.Tests/**`;
- the hooks added to `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs` and `tests/Shared/TestSchemaNames.cs`.

The PostgreSQL and SQL Server providers were the reference.

**Evidence limits.** I could not run Oracle. There is no Docker and no Oracle instance on this machine, so none of the
55 database tests ran here and I make no claim that they pass. The CI result the caller cites (run 45872607007: 82/82 on
23ai Free and 21c XE, 0 skipped) is the only database evidence, and I did not re-verify it. A finding marked "Reasoned"
comes from reading the code and from documented Oracle or ODP.NET behaviour. Where I was unsure what Oracle or ODP.NET
does, the finding says so, together with the test that would settle it.

### Test results (run by me)

| Command | Result |
|---|---|
| `dotnet build tests/DbRelay.Oracle.Tests/DbRelay.Oracle.Tests.csproj -c Release` | Build succeeded, 0 warnings, 0 errors (net8.0, net9.0, net10.0, net11.0) |
| `dotnet test --project tests/DbRelay.Oracle.Tests -c Release -f net10.0 --no-build` (without `DBRELAY_TEST_ORACLE`) | total 82: **27 succeeded** (registration, naming, script and SQL-shape tests), **55 skipped** ("No Oracle database available"), 0 failed |

### What was checked and found sound

- **Invariant 1 (caller-owned resources).** `InsertAsync` never opens, closes or disposes the caller's connection, and
  never commits, rolls back or disposes a transaction the caller passed.
  - The only transaction DbRelay commits on a caller connection is one it began itself (OR-04).
  - ODP.NET autocommits every command when there is no `OracleTransaction`, so such a connection has no pending caller work
    that DbRelay's commit could include.
- **Invariant 2 (caller's connection and transaction).**
  - Core `Outbox.ValidateCallerResources` checks the pairing (`transaction.Connection` is the supplied connection) and
    rejects a completed transaction.
  - The provider checks the types.
  - Commands do not set `OracleCommand.Transaction`, which is correct for ODP.NET: it runs every command in the
    connection's transaction.
- **Invariant 3 (no transaction during handler calls).** Every dispatcher-path operation is one autocommitted statement or
  anonymous block. The claim's REF CURSOR is read after the block has committed, and it is not a `FOR UPDATE` cursor.
- **Invariant 4 (lock-token guards).**
  - `MarkDelivered`, `ScheduleRetry`, `MarkDeadLettered` and `Release` are guarded by `WHERE ID = :id AND LOCK_TOKEN = :lock_token`.
  - The batch variants guard each FORALL element with its own token, and `SQL%BULK_ROWCOUNT(i)` reports each element.
  - Every terminal transition clears `LOCK_TOKEN`, so a token match implies status 1. That makes the missing
    `AND STATUS = 1` (which SQL Server has) harmless.
- **Invariant 5 (database clock).** Every time is `SYS_EXTRACT_UTC(SYSTIMESTAMP)`. Intervals are bound as milliseconds and
  converted with `NUMTODSINTERVAL(ms / 1000, 'SECOND')`, which is exact in `NUMBER` arithmetic. The application clock is
  used only for the caller-supplied `DeliverAt`, which is a value, not a comparison.
- **Lease equality.** `v_until` (`TIMESTAMP(6)`) is stored into a `TIMESTAMP(6)` column, so the result cursor's equality
  `CASE ... END = v_until` cannot lose precision.
- **Invariants 7 to 10.**
  - No type is activated from database strings.
  - All values are bind parameters.
  - Identifiers pass `SqlIdentifier` (`[A-Za-z_][A-Za-z0-9_]{0,62}`), are upper-cased and double-quoted. Index names in
    hints are unquoted upper-case `[A-Z0-9_]` names that start with `IX_`.
  - Nothing is logged by the provider, and error text is truncated in core (`ErrorText`) and again to `MaxErrorLength`.
  - Attempts are incremented in the claim block itself.
- **Claim block.**
  - Explicit single-row `FETCH` is correct: a cursor FOR loop would bulk-fetch 100 rows and, with `SKIP LOCKED`, lock rows
    it never claims.
  - The payload-budget check (`v_count > 0 AND v_bytes >= :max_payload_bytes`, with `v_bytes` the sum *before* the current
    row) matches the `IOutboxStore.ClaimAsync` contract and ADR 0004.
  - The row that trips the budget has been fetched, so it is locked, but it is not updated. Its lock ends when the block
    commits, as on PostgreSQL.
  - The claim-order string is 32 hex digits times at most 1,000 ids, which is 32,000 bytes, within `VARCHAR2(32767)`.
  - The output parameter is sized 32,767. The batch strings (32,000 characters in, 1,000 flags out) fit too.
- **Atomicity and retry.** An unhandled exception in a client-submitted anonymous block rolls back the whole block
  (statement-level atomicity of the top-level call). Retrying ORA-00060 is therefore safe for the claim, the guarded
  updates, the batch blocks, cleanup and requeue.
  - The >90-row `InsertStandaloneAsync` is several statements in DbRelay's own transaction. On ORA-00060 that transaction
    is rolled back by `DisposeAsync` before the retry, so it is safe too. The XML comment on `ExecuteAsync` saying "every
    operation is a single autocommitted statement" is slightly inaccurate (OR-10).
- **BA-07 ordering.** Unsigned lexicographic comparison of `Guid.ToByteArray()` is the order Oracle compares `RAW(16)` in,
  and FORALL runs its elements in index order. Claims, cleanup and bulk requeue use `SKIP LOCKED`, so they never wait.
- **`''` is NULL.**
  - `NVL(:pl, EMPTY_BLOB())` keeps the `NOT NULL` payload column valid for empty payloads.
  - An empty `Destination`, `CorrelationId` or `CausationId` reads back as null. This is documented (K17).
- **Schema script.** Each object is created by its own `EXECUTE IMMEDIATE`, and "already exists" codes are ignored.
  Re-running the whole block after ORA-04020, ORA-04021 or ORA-00054 only creates what is missing. The retry is bounded
  (5 attempts, with jitter).
- **Resources and async.**
  - Commands, readers, the REF CURSOR on the non-empty path, connections and DbRelay's own transaction are disposed.
  - `ConfigureAwait(false)` is used throughout.
  - The only synchronous call is `BeginTransaction()`. ODP.NET makes no round trip for it at the default isolation level.
- **Packaging.** `IsPackable` is false unless `-p:DbRelayPackOracle=true`, so a release tag cannot publish the package
  without approval.

### Findings

| Severity | Count | IDs |
|---|---|---|
| Critical | 0 | none |
| High | 0 | none |
| Medium | 3 | OR-01, OR-02, OR-03 |
| Low | 6 | OR-04, OR-05, OR-06, OR-07, OR-08, OR-09 |
| Info | 4 | OR-10, OR-11, OR-12, OR-13 |

#### OR-01 (Medium): the claim UPDATE does not re-check that the row is still due, so it relies on Oracle's fetch-time re-check under `SKIP LOCKED`

- **File:** `src/DbRelay.Oracle/Internal/OracleOutboxSql.cs:101-113` (`UPDATE ... WHERE ROWID = v_rid`); cursor at `:343-345`
- **Failure scenario:**
  - The cursor's rows come from the read-consistent snapshot taken at `OPEN`. With `SKIP LOCKED`, the row lock is taken
    later, at each `FETCH`.
  - Suppose another session changes a candidate row and commits between our `OPEN` and our `FETCH` of it. For example:
    - a concurrent claimer claims it (status 1, a new lease in the future);
    - or its previous owner acknowledges it after its lease expired (status 2).
  - The row is then no longer locked, so our `FETCH` locks it. The block then updates it by `ROWID` without re-checking
    `STATUS IN (0, 1) AND AVAILABLE_AT <= v_now`.
  - Whether Oracle re-evaluates the predicate against the current row version (a write-consistency restart) at fetch time
    under `SKIP LOCKED` is not something this code checks or a test pins.
    - If Oracle does not, a message under a valid lease is claimed twice (concurrent duplicate delivery, and the first
      owner's acknowledgement reports a lost lease).
    - Or a Delivered row is moved back to Processing with `ATTEMPT_COUNT + 1` and delivered again.
  - The reference providers both re-check:
    - PostgreSQL through EvalPlanQual on `FOR UPDATE`;
    - SQL Server explicitly (`WHERE m.[status] IN (0, 1) AND m.[available_at] <= @_now` in claim phase 2,
      `SqlServerOutboxSql.cs:113`).
- **Evidence:** Reasoned.
  - `Concurrent_claimers_never_receive_the_same_message` passed in CI. However, its race window is microseconds per row,
    and it never races a claim against an acknowledgement or a lease expiry, so it is weak evidence either way.
  - I could not run Oracle to test this.
- **Recommendation:**
  - Make the UPDATE self-guarding and count only rows it changed:
    `UPDATE ... WHERE ROWID = v_rid AND STATUS IN (0, 1) AND AVAILABLE_AT <= v_now;`
    `IF SQL%ROWCOUNT = 1 THEN v_count := v_count + 1; v_bytes := v_bytes + v_size; v_ids := v_ids || RAWTOHEX(v_id); END IF;`
  - The row is already locked, so the extra predicate costs nothing.
  - Add a deterministic provider test:
    1. Open the exact `ClaimCandidates` cursor in a test block.
    2. Commit a change to the first candidate from a second connection (claim it, or mark it delivered).
    3. Fetch, and assert that the row is not claimed again.
- **Status:** Open

#### OR-02 (Medium): the REF CURSOR is not disposed when nothing is claimed, which is the idle dispatcher's hot path

- **File:** `src/DbRelay.Oracle/Internal/OracleOutboxStore.cs:166-178`
- **Failure scenario:**
  - The block always runs `OPEN :claimed FOR ...`, so every claim returns a server-side cursor.
  - When `:claimed_ids` is NULL (nothing due), `ClaimAsync` returns at `:174` without disposing `cursor.Value`. The
    `using (var refCursor = ...)` is reached only when rows were claimed. A `ParseIds` exception also skips it.
  - An idle dispatcher polls continuously, so most claims take this path.
  - Disposing the command and returning the connection to the pool does not necessarily close an output REF CURSOR on the
    pooled session.
  - If the cursor stays open until the `OracleRefCursor` is finalized, idle polling accumulates open cursors per pooled
    session until ORA-01000 (`OPEN_CURSORS`, default 300). After that, every claim fails until the session is recycled.
  - The test suite cannot show this. It does a few dozen empty claims per pooled session, and there is no Oracle dispatcher
    integration test (OR-13).
- **Evidence:** Reasoned. The missing dispose is certain. Whether it leaks depends on ODP.NET internals and was not
  measured. Either way, CLAUDE.md requires disposing every reader-like resource.
- **Recommendation:**
  - Take the cursor into a `using` immediately after `ExecuteNonQueryAsync`, before parsing the ids or returning early:
    `using var refCursor = cursor.Value as OracleRefCursor;`
  - Add a provider test:
    1. Run 1,000 empty claims through a pool of size 1 (`Max Pool Size=1`).
    2. Assert that `V$OPEN_CURSOR` for that SID does not grow, or simply that no ORA-01000 occurs.
- **Status:** Open

#### OR-03 (Medium): LOB parameters are never disposed; possible temporary-LOB growth on pooled and caller sessions, and per-LOB round trips

- **File:**
  - `src/DbRelay.Oracle/Internal/OracleOutboxStore.cs:118-139` (insert: one `Blob` and one `Clob` parameter per row, up to 90 rows per statement);
  - `:218, :227, :621-622` (`LAST_ERROR` as `Clob`);
  - output parameters at `:166-167, :285-288, :515`
- **Failure scenario:**
  - When a `byte[]` is bound as `OracleDbType.Blob`, or a `string` as `OracleDbType.Clob`, ODP.NET may create a
    session-duration temporary LOB on the server for each parameter.
  - Neither `OracleParameter` (which implements `IDisposable`) nor its value is disposed here. Disposing `OracleCommand`
    does not dispose its parameters.
  - Temporary LOBs that are not freed live until the *session* ends:
    - in the dispatcher's pooled sessions, that is effectively forever;
    - on the transactional path, they are in the **caller's** session.
  - Under sustained enqueue, `V$TEMPORARY_LOBS` and TEMP tablespace use grow on sessions DbRelay does not own.
  - Separately, a 90-row `INSERT ALL` may cost up to 180 temporary-LOB round trips. That would undo much of the benefit
    of batching.
- **Evidence:** Reasoned, not measured. Whether ODP.NET 23.26 frees these LOBs on command dispose or sends small values
  inline is exactly what needs measuring.
- **Recommendation:**
  - Dispose every parameter after execution, for example `finally { foreach (OracleParameter p in command.Parameters) p.Dispose(); }`
    in one helper used by every command.
  - Add a provider test: run 50 `EnqueueManyAsync(90)` calls on one caller connection, then assert that
    `SELECT CACHE_LOBS + NOCACHE_LOBS FROM V$TEMPORARY_LOBS WHERE SID = SYS_CONTEXT('USERENV','SID')` has not grown.
  - Measure insert round trips (or the time for 90 x 1 KB rows) against binding payloads under 32 KB as `Raw` and headers
    under 32 KB as `Varchar2`, which Oracle converts on insert into a LOB column.
- **Status:** Open

#### OR-04 (Low): multi-statement insert without a transaction: DbRelay begins and commits on the caller's connection, and treats any `InvalidOperationException` as "a transaction exists"

- **File:** `src/DbRelay.Oracle/Internal/OracleOutboxStore.cs:81-111` (`BeginTransaction()` at `:97`, `catch (InvalidOperationException)` at `:99-104`)
- **Failure scenario:**
  - With `transaction: null` and more than 90 records, DbRelay calls `BeginTransaction()` and `CommitAsync()` on the
    caller-owned connection. This is the same pattern as SQL Server (CR-15, K9), and ADR 0005 now documents it.
  - I found no path where this commits *caller* work:
    - with no `OracleTransaction`, ODP.NET autocommits every command, so the session has no pending work;
    - if the caller has a local or distributed transaction, `BeginTransaction` throws and the statements join it.
  - Two residual risks remain:
    1. The catch assumes that every `InvalidOperationException` from `BeginTransaction` means "a transaction is in
       progress". If ODP.NET throws it for another reason, the chunks run as separately autocommitted statements. A failure
       in chunk N then leaves chunks 1 to N-1 committed, silently breaking the all-or-nothing contract of `EnqueueManyAsync`.
       For example, a connection enlisted in a `TransactionScope` that has already completed or aborted.
    2. With a caller transaction, a failure in chunk N leaves earlier chunks in that transaction (Oracle rolls back only the
       failed statement). A caller that catches the exception and commits anyway commits a partial batch, as in CR-15.
- **Evidence:** Reasoned. The 200-row joined case is tested (`Null_transaction_while_a_transaction_is_in_progress_joins_it`).
  The other `InvalidOperationException` sources are not.
- **Recommendation:**
  - Narrow the catch. After the catch, run one cheap check that a transaction really is active:
    `SELECT DBMS_TRANSACTION.LOCAL_TRANSACTION_ID FROM DUAL` must be non-null, or check `OracleConnection` enlistment state.
    If no transaction is active, rethrow.
  - Alternatively, avoid multi-statement inserts on caller connections. Insert more than 90 rows in one PL/SQL `FORALL`
    block over hex or array binds (the approach the batch acknowledgements already use for ids). The call is then atomic
    without DbRelay ever beginning a transaction on a caller's connection, and that also closes residual risk 2.
  - Document residual risk 2 on `EnqueueManyAsync`: roll back on any exception.
- **Status:** Open

#### OR-05 (Low): the schema script changes `DDL_LOCK_TIMEOUT` on pooled sessions and leaves it at 30 on failure; the 30 s wait equals the default command timeout

- **File:**
  - `src/DbRelay.Oracle/Scripts/001_create_outbox.sql:36, 86-87`
  - `src/DbRelay.Oracle/Internal/OracleOutboxSchemaManager.cs:15, 30-48`
  - `src/DbRelay.Oracle/OracleOutboxOptions.cs:19` (default `CommandTimeout` 30 s)
- **Failure scenario:**
  1. **Leak on failure.** The reset to 0 runs only on success. Any raised error, including the retried ORA-04020, ORA-04021
     and ORA-00054, returns the session to the ODP.NET pool with `DDL_LOCK_TIMEOUT = 30`. With
     `UseOracle(connectionString)`, the pool is shared with every application connection that uses the same string, so
     later application DDL on that session silently waits up to 30 s instead of failing fast.
  2. **Overwrite on success.** On success the reset writes 0, overwriting any value the DBA set, for example in a logon
     trigger.
  3. **Timeout race.** A DDL that waits the full 30 s for a lock races the 30 s command timeout. ODP.NET then raises
     ORA-01013 (user requested cancel), which `IsConcurrentDdlError` does not retry. `EnsureCreatedAsync` therefore fails
     in exactly the contention case the timeout was added for.
- **Evidence:** Reasoned (script control flow).
- **Recommendation:**
  - Reset in an exception handler as well: `EXCEPTION WHEN OTHERS THEN EXECUTE IMMEDIATE 'ALTER SESSION SET DDL_LOCK_TIMEOUT = 0'; RAISE;`
  - Better, avoid the session change entirely. Check `ALL_OBJECTS` or `ALL_INDEXES` for each object before issuing its
    DDL, so that a running system never takes DDL locks at start-up.
  - Use a `DDL_LOCK_TIMEOUT` clearly below `CommandTimeout` (for example 10 s), or derive it from the timeout.
- **Status:** Open

#### OR-06 (Low): `RAW(16)` accepts shorter values; a row with an ID that is not 16 bytes corrupts the claim result instead of becoming an unreadable placeholder

- **File:**
  - `src/DbRelay.Oracle/Scripts/001_create_outbox.sql:39`
  - `src/DbRelay.Oracle/Internal/OracleOutboxSql.cs:112` (`RAWTOHEX(v_id)` appended without a length check)
  - `src/DbRelay.Oracle/Internal/OracleOutboxStore.cs:584-594` (fixed 32-hex parse), `:535` (`new Guid(...)` inside the CR-01 fallback)
- **Failure scenario:**
  - Unlike `uuid` and `uniqueidentifier`, `RAW(16)` stores 1 to 16 bytes. A hand-written or migrated row with an 8-byte ID
    breaks the claim in two ways.
    1. It contributes 16 hex digits to `:claimed_ids`, so `ParseIds` misreads every later ID in that batch. Those rows are
       claimed (attempts already counted) but silently dropped from the result. They stay Processing until the lease
       expires, which can repeat.
    2. In `ReadDetails`, `new Guid(byte[8])` throws inside the catch block, so the whole claim fails. Its batch-mates are
       claimed but never delivered, and the poison row can never be dead-lettered because the dispatcher never sees it.
  - This defeats the CR-01/FU-01 protection that the other providers have.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Add `CONSTRAINT CK_<table>_ID CHECK (LENGTHB(ID) = 16)` to the table (new installs; note it in the upgrade docs).
  - Make the parse defensive:
    - In the block, claim only rows with `UTL_RAW.LENGTH(ID) = 16`, or emit a separator.
    - In `ReadDetails`, build the placeholder ID with a non-throwing conversion.
  - Add a compile-time or unit check that `OracleOutboxSql.HexPerId * OutboxLimits.MaxClaimBatchSize <= 32767`. Raising
    the batch limit would otherwise turn every claim into ORA-06502.
- **Status:** Open

#### OR-07 (Low): a shared conformance assertion was relaxed for every provider

- **File:** `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs:153`
- **Failure scenario:**
  - `Schema_create_script_uses_configured_schema` now compares with `OrdinalIgnoreCase` for PostgreSQL and SQL Server as
    well.
  - A regression that case-folds the schema name in their scripts would no longer be caught. On PostgreSQL that matters:
    quoted identifiers are case-sensitive.
  - CLAUDE.md: "Never weaken a test to make it pass."
- **Evidence:** Verified (diff).
- **Recommendation:**
  - Add a `protected virtual string ExpectedSchemaInScript(string schema) => schema;` hook. Oracle overrides it with
    `schema.ToUpperInvariant()`.
  - Keep `StringComparison.Ordinal`.
- **Status:** Open

#### OR-08 (Low): non-AL32UTF8 databases silently corrupt non-ASCII text columns

- **File:** `src/DbRelay.Oracle/Scripts/001_create_outbox.sql:40-57` (`VARCHAR2(n CHAR)` and `CLOB` for `MESSAGE_TYPE`, `DESTINATION`, `HEADERS`, `CORRELATION_ID`, `CAUSATION_ID`, `LOCKED_BY` and `LAST_ERROR`)
- **Failure scenario:**
  - Legacy Oracle databases often use a single-byte character set such as `WE8MSWIN1252`. There, characters outside that
    set are replaced with `?` on insert, without any error.
  - A message type or header value containing such characters reads back changed. For example, routing by `MessageType`
    or a non-Latin correlation ID silently stops matching.
  - Payloads are a `BLOB` and are safe.
- **Evidence:** Reasoned (Oracle character-set conversion behaviour). AL32UTF8 is the default since 12.2, so most new
  databases are unaffected.
- **Recommendation:**
  - Document that the database character set must be AL32UTF8, or use `NVARCHAR2` and `NCLOB`.
  - Optionally, have `EnsureCreatedAsync` read `NLS_DATABASE_PARAMETERS` and fail or warn if the character set is not a
    Unicode set.
- **Status:** Open

#### OR-09 (Low): cancellation may surface as `OracleException` ORA-01013 rather than `OperationCanceledException`

- **File:**
  - `src/DbRelay.Oracle/Internal/OracleConnectionSource.cs:75-96`, and every `Execute*Async(ct)` call in `OracleOutboxStore.cs`;
  - consumer at `src/DbRelay/Dispatching/OutboxDispatcher.cs:213-224`
- **Failure scenario:**
  - When the host stops during a claim or an acknowledgement, ODP.NET may cancel the server call and throw
    `OracleException` (ORA-01013 "user requested cancel of current operation") instead of `OperationCanceledException`.
  - The dispatcher's `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)` then does not match.
    Normal shutdowns log `ClaimFailed` errors and count poll failures.
- **Evidence:** Unverified. I could not run ODP.NET against a server. No test cancels an in-flight Oracle command.
- **Recommendation:**
  - In `OracleConnectionSource.ExecuteAsync`, translate `OracleException { Number: 1013 }` to
    `OperationCanceledException(cancellationToken)` when `cancellationToken.IsCancellationRequested`.
  - Add a provider test that cancels a long statement, for example a block running `DBMS_SESSION.SLEEP(5)`.
- **Status:** Open

#### OR-10 (Info): documentation drift in ADR 0005 and the XML docs

- **File:**
  - `docs/decisions/0005-oracle-and-mysql-providers.md:56, 81`
  - `src/DbRelay.Oracle/Internal/OracleOutboxSql.cs:213, 345`
  - `src/DbRelay.Oracle/Internal/OracleConnectionSource.cs:71-73`
- **Failure scenario:** The documents disagree with the code:
  - The ADR's column table says `TIMESTAMP(3)`; the script and `docs/oracle.md` say `TIMESTAMP(6)`.
  - The ADR's claim says `ORDER BY <claim expression>, ID`, but the cursor orders by the claim expression only. Rows with
    equal `AVAILABLE_AT`, for example a whole `INSERT ALL` batch with the same database `now`, are claimed in an
    unspecified order. That is harmless for correctness, but not what the ADR states.
  - The `CreateBlock` XML doc says comments are removed. Only the header comments are; the `--` comments inside the block
    remain (harmless).
  - The `ExecuteAsync` comment says every operation is a single autocommitted statement. The >90-row standalone insert is
    a multi-statement transaction. The retry is still safe (see "found sound").
- **Recommendation:**
  - Correct the ADR to `TIMESTAMP(6)`, and either add `ID` as the tie-breaker (an index-ordered scan would need it as a key
    column) or drop it from the ADR.
  - Fix the two comments.
- **Status:** Open

#### OR-11 (Info): REF CURSOR inheritance of `InitialLOBFetchSize = -1` is assumed, not tested

- **File:** `src/DbRelay.Oracle/Internal/OracleConnectionSource.cs:65`; `src/DbRelay.Oracle/Internal/OracleOutboxStore.cs:180`
- **Failure scenario:**
  - The claim's rows are read through `OracleRefCursor.GetDataReader()`.
  - If that reader does not inherit the command's `InitialLOBFetchSize = -1`, results stay correct: `GetValue` still returns
    the full `byte[]`. However, every claimed row costs up to three extra round trips (payload, headers, last error) on the
    hottest read path. This would show only as throughput.
- **Evidence:** Unverified (ODP.NET behaviour).
- **Recommendation:** In a provider test, assert `reader.InitialLOBFetchSize == -1` on the REF CURSOR's reader (the property
  is readable). If it is not inherited, set `refCursor.FetchSize` and read the LOBs through `GetOracleBlob` in bulk, or
  return the payload inline.
- **Status:** Open

#### OR-12 (Info): the claim index has no ID tie-breaker, so the claim relies on an index-ordered scan for `ORDER BY`

- **File:** `src/DbRelay.Oracle/Scripts/001_create_outbox.sql:63`; `src/DbRelay.Oracle/Internal/OracleOutboxSql.cs:343-345`
- **Failure scenario:**
  - The no-sort plan holds only while the hint is honoured. If the index is dropped, invisible or unusable, the claim falls
    back to a full scan plus `SORT ORDER BY` of every due row on each poll.
  - The documentation already says "keep the indexes". The plan test pins the hinted plan only on a fresh 20,000-row table,
    not after `ALTER INDEX ... UNUSABLE` or with stale statistics.
- **Recommendation:** None required. Optionally, have the health check report the claim index's status from
  `ALL_INDEXES.STATUS`, so that an unusable index is visible.
- **Status:** Open

#### OR-13 (Info): test gaps specific to Oracle

- **File:** `tests/DbRelay.Oracle.Tests/*`
- **Gaps:**
  - No test inserts large LOBs through `INSERT ALL`, such as payloads over 32 KB, headers over 4,000 bytes, or a
    2,000-character multibyte `LAST_ERROR` (6,000 bytes in AL32UTF8). These are the cases where ODP.NET LOB binding and
    ORA-24816 or ORA-01461 surprises occur. The ADR's original "array binding of BLOB values" item has no direct equivalent.
  - No Oracle counterpart of SQL Server's BA-07 stress test (claims, stale batch acknowledgements and cleanup at once).
    `DeadlockRetries` is exposed but never asserted for Oracle.
  - No dispatcher end-to-end (integration) test on Oracle. Such a test would have exercised the idle-poll path of OR-02 at
    volume.
  - No test for OR-01's open-to-fetch race, for OR-09's cancellation, or for the failure path of OR-05.
- **Recommendation:**
  - Add the large-LOB `EnqueueMany(90)` and `EnqueueMany(1000)` round-trip test.
  - Port the BA-07 stress test.
  - Run the integration suite against Oracle in the 23ai job.
- **Status:** Open

### Commands run in this review

| Command | Purpose | Result |
|---|---|---|
| `git diff --stat main...oracle-provider`, `git show --stat 525d83c` | Scope, and the documentation commit that landed during the review | 20 files in code and tests; 525d83c is documentation only |
| `dotnet build tests/DbRelay.Oracle.Tests/DbRelay.Oracle.Tests.csproj -c Release` | Build the provider and tests on all target frameworks | Succeeded, 0 warnings, 0 errors |
| `dotnet test --project tests/DbRelay.Oracle.Tests -c Release -f net10.0 --no-build` (no `DBRELAY_TEST_ORACLE`) | Non-database tests | 27 passed, 55 skipped, 0 failed |

Oracle database tests: **not run here (skipped: no Oracle available)**.

### Verdict

**No Critical or High findings. Three open Medium findings:**
- OR-01: the claim UPDATE has no re-check;
- OR-02: the REF CURSOR leaks on empty claims;
- OR-03: LOB parameters are not disposed.

Each has a small, local fix. OR-02 and OR-03 should be fixed before the package is released. OR-01 should be fixed, or
disproved by the deterministic test it describes, before release.

## Oracle provider review: resolution log (principal architect, 2026-11-27)

| ID | Severity | Resolution |
|---|---|---|
| OR-01 | Medium | **Fixed and tested.** The claim's UPDATE re-checks `STATUS IN (0, 1) AND AVAILABLE_AT <= v_now` on the locked row, and only rows with `SQL%ROWCOUNT = 1` are counted, returned and budgeted. Test seam `ClaimTestPause` holds the claim cursor open between choosing and locking; `A_row_delivered_while_the_claim_cursor_is_open_is_not_claimed_again` acknowledges the row in that gap and checks it is not claimed again (passes on 23ai and 21c XE). |
| OR-02 | Medium | **Fixed, then removed.** The claim no longer uses a REF CURSOR at all (see OR-11). |
| OR-03 | Medium | **Fixed.** Command parameters, including BLOB and NCLOB values, are disposed after the insert, the claim and every guarded or bulk statement. Large LOBs are covered by `Large_payloads_round_trip_through_insert_and_claim` (90 x 128 KiB and 8 MiB). No `V$TEMPORARY_LOBS` assertion: the 5h20m Oracle soak watches the long-lived worker instead. |
| OR-04 | Low | **Fixed.** Up to 1,000 rows are one anonymous PL/SQL block of `INSERT ALL` statements, rolled back as one statement on an error. DbRelay never begins a transaction on the caller's connection, and a failure leaves nothing in the caller's transaction (`The_largest_enqueue_batch_is_one_atomic_statement_and_rolls_back_with_the_caller`). |
| OR-05 | Low | **Fixed.** The script no longer changes session settings (`DDL_LOCK_TIMEOUT` removed). The ORA-00054/04020/04021 retry covers concurrent DDL. |
| OR-06 | Low | **Fixed.** Check constraint `CK_<table>_ID` (`LENGTH(RAWTOHEX(ID)) = 32`). Unit test: the claim order of a full batch fits `VARCHAR2(32767)`. Database test: a short ID is rejected with ORA-02290. |
| OR-07 | Low | **Fixed.** The shared assertion is ordinal again; Oracle overrides the new `SchemaNameInScript` hook. |
| OR-08 | Low | **Fixed.** Text columns are `NVARCHAR2` and `NCLOB` and parameters bind as `NVarchar2` and `NClob`, which are Unicode whatever the database character set. A test pins the column types. |
| OR-09 | Low | **Fixed and tested.** An `OracleException` raised while the caller's token is cancelled is rethrown as `OperationCanceledException`, on DbRelay's connections and on the caller's insert path. `Cancelling_a_statement_that_waits_for_a_lock_throws_OperationCanceledException`; the dispatcher end-to-end test also asserts no error is logged across a shutdown under load. |
| OR-10 | Info | **Fixed.** The ADR says `TIMESTAMP(6)` and no ID tie-break; both XML comments are corrected. |
| OR-11 | Info | **Fixed.** Claimed rows are read by primary key on a DbRelay command (`InitialLOBFetchSize = -1`), in bucketed IN lists, instead of through a REF CURSOR. |
| OR-12 | Info | **Fixed.** The health check fails while one of the three indexes is missing or unusable (`Health_check_fails_while_an_index_is_missing_or_unusable`). |
| OR-13 | Info | **Fixed.** New tests: large LOBs, a BA-07-style deadlock stress test (0 ORA-00060 retries), and the dispatcher end to end across a shutdown. Plus an Oracle soak (`.github/workflows/soak.yml`). |

## MySQL provider review (ADR 0006), 2026-11-27

Independent review of branch `mysql-provider` against `oracle-provider` (`git diff oracle-provider...mysql-provider`). I reviewed
at `77f9a55`. During the review the branch moved to `7293232`. The two new commits (`ad8ca95` and `7293232`) touch only the soak
harness and its workflow (`tests/DbRelay.Soak/**`, `.github/workflows/soak-mysql.yml`), not the provider or its tests. Scope:
- `src/DbRelay.MySql/**`;
- `tests/DbRelay.MySql.Tests/**`;
- `tests/Shared/TestSchemaNames.cs` (the MySQL sweep);
- `docs/decisions/0006-mysql-provider.md`.

PostgreSQL, SQL Server and Oracle (including the OR-01..OR-13 fixes) were the reference.

**Evidence limits.** There is no MySQL or MariaDB server on this machine, so none of the 62 database tests ran here, and I make
no claim that they pass. The CI result the caller cites (run 45886270423: 88/88 on MySQL 8.4 on 4 TFMs, MySQL 8.0.46, MariaDB
10.11.19 and 11.4.13) is the only database evidence, and I did not re-verify it. "Reasoned" means the finding comes from reading
the code and from documented InnoDB or MySqlConnector behaviour. "Verified" means I checked it here. For MySqlConnector, I read
the string literals in the 2.6.2 `MySqlConnector.dll` from the NuGet cache.

### Test results (run by me)

| Command | Result |
|---|---|
| `dotnet build tests/DbRelay.MySql.Tests/DbRelay.MySql.Tests.csproj -c Release` | Build succeeded, 0 warnings, 0 errors (net8.0, net9.0, net10.0, net11.0) |
| `dotnet test --project tests/DbRelay.MySql.Tests -c Release -f net10.0 --no-build` (without `DBRELAY_TEST_MYSQL`) | total 88: **26 succeeded** (registration, naming, script and statement-shape tests), **62 skipped** ("No MySQL or MariaDB server available"), 0 failed |
| `dotnet format src/DbRelay.MySql/DbRelay.MySql.csproj --verify-no-changes`; the same for `tests/DbRelay.MySql.Tests` | exit 0 for both |

### What was checked and found sound

- **Invariants 1 and 2 (caller resources).**
  - `InsertAsync` runs one `INSERT` on exactly the caller's `MySqlConnection` and `MySqlTransaction`, and never opens, closes,
    commits, rolls back or disposes either. DbRelay never begins a transaction on a caller connection.
  - Core `Outbox.ValidateCallerResources` checks the pairing, rejects completed transactions and closed connections. The
    provider checks the types.
  - With `IgnoreCommandTransaction=true`, MySqlConnector runs the command in the connection's active transaction. Because of
    the pairing check, that is the caller's transaction, so the invariant still holds.
- **Invariant 3.**
  - Every transaction is opened and committed inside one store call, on a DbRelay-owned connection.
  - The claim's three statements and the batch acknowledgement's two statements contain no handler call.
- **Invariant 4 (single-row transitions).**
  - `MarkDelivered`, `ScheduleRetry`, `MarkDeadLettered` and `Release` are `WHERE id = UNHEX(@id) AND lock_token = UNHEX(@lock_token)`.
  - Every terminal transition clears `lock_token`, so a token match implies status 1.
  - The `rows == 1` check is correct under both affected-rows modes: with `UseAffectedRows=false` (the default) MySQL reports
    found rows, and every matching update changes `status` or `available_at` anyway.
  - The batch path is discussed in MY-06.
- **Claim correctness (risk 1).**
  - `FOR UPDATE` is a current (locking) read. In InnoDB, a row returned by step 1 is the latest committed version, and it stays
    X-locked until the commit.
  - Step 3's `status IN (0, 1) AND available_at <= now` re-check therefore cannot fail for a chosen row. It is defence in depth:
    if step 1 ever returned a row it had not locked, step 3 would lock and re-evaluate it.
  - Step 4 reads by `lock_token = @fresh token`, so only rows this claim changed are returned. The attempt is counted in
    step 3 (invariant 10).
  - SKIP LOCKED never waits, so claimers cannot deadlock with each other or with acknowledgements.
  - Budget rows that were read but not taken stay locked only until this short transaction commits, as on PostgreSQL and
    Oracle.
  - The budget test (`chosen.Count > 0 && bytes >= budget`, with `bytes` summed before the current row) matches the
    `IOutboxStore.ClaimAsync` contract.
- **Isolation (risk 2).** Producers insert under the caller's isolation. Their insert-intention locks conflict only with gap
  locks. DbRelay's claim, batch, cleanup and bulk-requeue transactions are READ COMMITTED, which takes no gap locks here (the
  only unique index is the PK, and duplicate-key checks happen only on producers' own inserts). Delivered and dead rows sit at
  the NULL end of `ix_claim`, and new rows have NULL `cleanup_at` and `dead_at`. Under REPEATABLE READ, the cleanup range scan
  would have gap-locked exactly where producers insert, so the RC choice is the right one.
  - The autocommitted single-row updates (PK equality) take record locks only, while the row exists. If the row is missing,
    REPEATABLE READ takes a gap lock for the duration of that one autocommitted statement, which is negligible.
- **GUIDs and ordering (risk 3).**
  - `id` is `BINARY(16)` written as `UNHEX` of the "N" string. It is independent of `GuidFormat`, which is tested for three
    formats.
  - Core's `Uuid7` builds the GUID big-endian, so the "N" string, and therefore the stored bytes, are time-ordered. InnoDB's
    clustered inserts are mostly appends.
  - Ordinal order of the lowercase "N" string equals `memcmp` order of the stored bytes, so the BA-07 lock order is the PK order.
  - `BINARY(16)` always holds 16 bytes, because shorter values are zero-padded. The OR-06 class of problem (short IDs corrupting
    a parse) cannot occur.
  - Padding IN lists by repeating the last ID is harmless.
- **Batched acknowledgements (risk 4).**
  - The rows are locked with `FOR UPDATE` in PK order, and the tokens are compared in C# while those locks are held in the same
    READ COMMITTED transaction. The `UPDATE` then touches only rows that are still locked, so no other session can change a
    token in between: the update is race-free.
  - Duplicate pairs are collapsed by `ToHashSet`. A stale and a current token for the same ID give at most one match, and a NULL
    token never matches.
- **Clock and types (invariant 5).**
  - All comparisons use `UTC_TIMESTAMP(6)`, which is constant within a statement.
  - `DATETIME(6)` is not converted by the session `time_zone`.
  - Intervals are bound as whole microseconds and clamped to 20,000 days.
  - `DeliverAt` is a value, bound with `Kind = Unspecified`, so MySqlConnector's `DateTimeKind` option cannot reject it. Reads
    re-tag values as UTC without converting them.
- **Invariants 6 to 9.**
  - No type is activated from database strings.
  - Every value is a parameter.
  - Identifiers pass `SqlIdentifier` (no backtick can appear) and are backtick-quoted.
  - The provider logs nothing, and `last_error` is truncated to `MaxErrorLength`.
- **Retry (risk 6).**
  - 1213 rolls back the whole transaction. 1205 rolls back only the statement (with `innodb_rollback_on_timeout=OFF`), but
    `RunAsync` disposes, and so rolls back, DbRelay's transaction and connection before retrying.
  - Every retried operation is a single autocommitted statement or a whole DbRelay transaction, so each is safe to run again.
  - The caller's insert is never retried.
  - The retry is bounded (3 attempts, 5 to 50 ms times the attempt number of jitter). It is skipped once cancellation is
    requested, and a `MySqlException` raised under cancellation is mapped to `OperationCanceledException` (tested).
- **Resources and async.**
  - Every command, reader, transaction and owned connection is disposed with `await using (... .ConfigureAwait(false))`.
  - There is no sync-over-async.
  - `MySqlParameter` holds no unmanaged resources (unlike OR-03).
- **Schema (risk 7).**
  - `CREATE TABLE IF NOT EXISTS` with inline indexes and `INSERT IGNORE` is idempotent, and it is safe concurrently under the
    metadata lock (tested with 4 x 3 concurrent runs).
  - Long names are bounded to 64 characters with a deterministic hash (unit-tested).
  - MySQL's per-database CHECK-name rule is satisfied for distinct tables, but see MY-09.
- **Packaging.** `IsPackable` is false unless `-p:DbRelayPackMySql=true`.

### Findings

| Severity | Count | IDs |
|---|---|---|
| Critical | 0 | none |
| High | 0 | none |
| Medium | 3 | MY-01, MY-02, MY-03 |
| Low | 9 | MY-04, MY-05, MY-06, MY-07, MY-08, MY-09, MY-10, MY-11, MY-12 |
| Info | 4 | MY-13, MY-14, MY-15, MY-16 |

#### MY-01 (Medium): the claim's candidate query reads every candidate's full payload from InnoDB just to compute its length

- **File:**
  - `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:73-74` (`SELECT HEX(id), OCTET_LENGTH(payload) ... LIMIT @batch_size FOR UPDATE SKIP LOCKED`);
  - `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:147-157`
- **Failure scenario:**
  - `payload` is a `LONGBLOB`. With the DYNAMIC row format, any payload larger than a few hundred bytes to about 8 KB is stored
    off-page.
  - When a column is in the read set, as it is for `OCTET_LENGTH(payload)`, InnoDB copies the whole externally stored value
    into the row buffer (`btr_rec_copy_externally_stored_field`, on both MySQL and MariaDB). There is no length-only path.
  - Step 1 therefore reads up to `BatchSize` full payloads. Step 4 then reads the chosen ones again.
    - When the budget does not bind, every payload is read twice on each claim.
    - When it binds, for example `BatchSize = 100` with 100 due 16 MiB messages and a 64 MiB budget, step 1 reads about 1.6 GiB
      from the buffer pool or disk to claim 4 messages, on every poll while the backlog lasts.
  - The other providers do not have this cost:
    - PostgreSQL's `octet_length` on a TOASTed value reads the TOAST header;
    - SQL Server's `DATALENGTH` and Oracle's LOB length read the LOB root or locator.
  - Correctness is unaffected. This is read amplification on the hottest path, and it defeats much of what ADR 0004's budget is
    for.
- **Evidence:** Reasoned (InnoDB source behaviour); not measured.
- **Recommendation:**
  - Before the v1 schema ships, add `payload_size INT UNSIGNED NOT NULL` and write it in the INSERT as `OCTET_LENGTH(@pl{n})`.
    A `STORED` generated column `AS (OCTET_LENGTH(payload))` works on both engines too.
  - Read `payload_size` in step 1 instead of `OCTET_LENGTH(payload)`.
  - Measure before and after by comparing the `Innodb_buffer_pool_read_requests` delta of one claim over 100 x 8 MiB rows with
    `MaxPayloadBytes = 1`.
- **Status:** Open

#### MY-02 (Medium): an enqueue that exceeds `max_allowed_packet` makes the server close the caller's connection; core accepts calls larger than the server defaults; the provider guide the ADR requires does not exist

- **File:**
  - `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:92-120`;
  - `docs/decisions/0006-mysql-provider.md:99-100`;
  - core limit at `src/DbRelay/Outbox.cs:99-106` (a call may total `max(64 MiB, MaxPayloadBytes)`, and `MaxPayloadBytes` can be up
    to 256 MiB)
- **Failure scenario:**
  - The 1,000-row INSERT is sent as one statement. MySqlConnector substitutes parameters on the client, so the packet holds the
    payloads (escaped as `_binary'...'`, up to 2x for bytes such as 0x00), the headers and about 270 KB of statement text.
  - The server's `max_allowed_packet` defaults to 64 MiB on MySQL 8 and 16 MiB on MariaDB. On a larger packet, the server
    raises ER_NET_PACKET_TOO_LARGE and **closes the connection** (MySQL manual, "Packet Too Large"). MySqlConnector 2.6.2
    reports "Error submitting {0}MB packet; ensure 'max_allowed_packet' is greater than {1}MB." (verified in the DLL).
  - On the transactional path, that connection is the caller's, so the caller's business transaction is aborted and its
    connection is dead.
    - Atomicity holds, because everything rolls back.
    - But no other provider turns an oversized enqueue into a lost connection.
    - On MariaDB with defaults, about 17 MiB of payload in one `EnqueueManyAsync` triggers it, well within what core accepts.
  - The ADR's mitigation ("The provider guide must say so") has not been done: there is no `docs/mysql.md`.
- **Evidence:** Reasoned (documented server behaviour, plus the MySqlConnector message). No test covers it.
- **Recommendation:**
  - Read `@@max_allowed_packet` once on a DbRelay connection and cache it.
  - In `InsertCoreAsync`, estimate the packet size (2 x payload bytes, plus 2 x header characters, plus the statement text
    length) and throw an `ArgumentException` naming the limit *before* sending. The caller's connection and transaction then
    stay usable.
  - Write `docs/mysql.md` covering `max_allowed_packet` and the other items in MY-05, MY-07, MY-11 and MY-15.
  - Add a provider test: `SET GLOBAL max_allowed_packet` to a small value in the container, enqueue in a caller transaction,
    then assert the clear exception and that the caller's connection still runs `SELECT 1`.
- **Status:** Open

#### MY-03 (Medium): the INSERT text is cached for every row count from 1 to 1,000, which retains up to about 259 MiB per registration

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:41, 168-173, 252-266`
- **Failure scenario:**
  - `GetInsert(rows)` caches one string per distinct row count.
  - I replicated `BuildInsert` (default names): 1,000 rows is 271,872 characters. All 1,000 sizes total 135.6 M characters,
    about **259 MiB** of UTF-16 strings, mostly on the large object heap and never released. Sizes 1 to 100 alone are 2.5 MiB.
  - Applications whose `EnqueueManyAsync` sizes vary, such as bulk jobs whose last chunk varies or per-order line items, collect
    a new size each time they see one.
  - The Oracle provider explicitly stopped caching texts above 90 rows for this reason (`OracleOutboxSql.cs:285-288`: "a block
    for 1,000 rows is about 300 KB of text"). SQL Server caches only up to its per-statement row cap.
- **Evidence:** Verified (length computation); retention follows from the `ConcurrentDictionary`.
- **Recommendation:**
  - Cache only up to a small row count (for example 64), and build larger texts per call. A 272K-character `StringBuilder` is
    small next to sending the rows.
  - Optionally shorten each row: the `due` expression is emitted twice per row. Binding one precomputed `@dl` or `@da` per row,
    or using a derived table, halves the text.
  - Add a unit test that bounds the cache size after `GetInsert(1..1000)`.
- **Status:** Open

#### MY-04 (Low): MySqlConnector sets the isolation level for the *session*; with `ConnectionReset=false`, pooled sessions shared with the application stay at READ COMMITTED

- **File:** `src/DbRelay.MySql/Internal/MySqlConnectionSource.cs:98` (`BeginTransactionAsync(IsolationLevel.ReadCommitted, ...)`)
- **Failure scenario:**
  - MySqlConnector 2.6.2 implements an explicit isolation level as `set session transaction isolation level <level>` followed
    by `start transaction` (verified from the string literals in the DLL). The session keeps READ COMMITTED after DbRelay's
    commit.
  - With the default `ConnectionReset=true`, the pool resets the session, so there is no effect.
  - With `ConnectionReset=false`, which some performance guides recommend, and `UseMySql(connectionString)` sharing the pool
    with the application's own connections, later application work on that pooled session runs at READ COMMITTED instead of the
    server default (REPEATABLE READ). That applies to `BeginTransaction()` without a level (MySqlConnector then sends no SET)
    and to autocommitted statements. The application's semantics change silently: non-repeatable reads, and different locking.
- **Evidence:** Verified (MySqlConnector string literal). The effect with `ConnectionReset=false` is reasoned.
- **Recommendation:**
  - Run `SET TRANSACTION ISOLATION LEVEL READ COMMITTED` (next-transaction scope, no `SESSION`), then
    `BeginTransactionAsync()` with no level. First confirm that MySqlConnector sends no SET for `Unspecified`.
  - Alternatively, give DbRelay its own pool by adding a distinguishing option to the connection string.
  - Add a test on a `ConnectionReset=false;Maximum Pool Size=1` connection string: after a claim, `SELECT @@session.transaction_isolation`
    on the next pooled connection equals the global value.
- **Status:** Open

#### MY-05 (Low): with `binlog_format=STATEMENT`, every DbRelay write in READ COMMITTED fails with error 1665, while the health check stays green

- **File:** `src/DbRelay.MySql/Internal/MySqlConnectionSource.cs:79-81, 98`; `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:114` (health check)
- **Failure scenario:**
  - InnoDB refuses statement-based binary logging under READ COMMITTED, on both MySQL and MariaDB: "impossible to write to
    binary log since BINLOG_FORMAT = STATEMENT ... InnoDB is limited to row-logging when transaction isolation level is READ
    COMMITTED".
  - On a server with `log_bin=ON` and `binlog_format=STATEMENT`, which is legacy and deprecated in 8.0.34 but still found, these
    all fail on every call: the claim UPDATE, batch acknowledgements, cleanup and bulk requeue.
  - Producers' inserts, at the caller's isolation, succeed. Messages therefore pile up undelivered.
  - `SELECT 1 FROM t LIMIT 1` still passes, so the health check reports healthy. ROW and MIXED (the defaults) are unaffected.
- **Evidence:** Reasoned (documented server rule).
- **Recommendation:**
  - In `CheckHealthAsync` (and optionally `EnsureCreatedAsync`), fail with an explanatory message when
    `@@log_bin = 1 AND @@session.binlog_format = 'STATEMENT'`.
  - Document the requirement in `docs/mysql.md`.
- **Status:** Open

#### MY-06 (Low): the batched acknowledgement UPDATE has no lock-token predicate in SQL, and its affected-row count is not checked

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:207-216`; `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:297-310`
- **Failure scenario:**
  - The guard is correct today: the token is compared in C# while the `FOR UPDATE` locks are held in the same transaction.
  - Invariant 4, however, is written as "every state change after a claim is guarded by `WHERE id = @id AND lock_token = @token`",
    and this is the only post-claim statement in any provider whose SQL has no token predicate. SQL Server joins on
    `(id, lock_token)`.
  - The guard depends on two things staying true, and neither is pinned by a test:
    1. both statements stay in one transaction;
    2. step 1 stays a locking read.
  - A refactor, for example moving step 1 to `ExecuteAsync`, dropping `FOR UPDATE` to reduce waits, or a future autocommit path,
    would silently turn the batch into an unguarded update that overwrites a newer claim.
  - The `ExecuteNonQueryAsync` result is discarded, so such a regression would not be detected at run time.
- **Evidence:** Verified (code).
- **Recommendation:**
  - Use `WHERE (id, lock_token) IN ((UNHEX(@i0), UNHEX(@t0)), ...)`. Row-constructor IN is range-optimized on the PK in MySQL
    8.0 and MariaDB.
  - Check that the returned count equals `matchedIds.Count`. Every matched row changes, so the count is the same under either
    affected-rows mode. On a mismatch, throw so that the transaction rolls back.
- **Status:** Open

#### MY-07 (Low): with `Schema = null`, the caller's insert goes to the caller connection's current database, which may not be the one the dispatcher polls

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:248` (unqualified names); `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:52-74`; `docs/decisions/0006-mysql-provider.md:33`
- **Failure scenario:**
  - In MySQL, a schema is a database, and applications often pick it per connection: `Database=` per tenant, `USE`, or
    `ChangeDatabase`.
  - Suppose `UseMySql("...;Database=app")` is registered and a caller enqueues on a connection whose current database is
    `tenant_42`.
    - If `tenant_42.outbox_messages` exists (for example, every tenant database was created from the same script), the message
      commits there and this host's dispatcher never sees it. That is a silent stall.
    - Otherwise the insert fails with 1146.
  - Oracle has the same rule, but switching the current schema is far less common there than switching databases on MySQL.
- **Evidence:** Reasoned.
- **Recommendation:**
  - When `Schema` is null, record the database of DbRelay's own connections: from `MySqlConnectionStringBuilder.Database`, or
    from `connection.Database` after the first open.
  - In `InsertAsync`, compare it with the caller's `connection.Database` (no round trip; MySqlConnector tracks `USE`), and throw
    an explanatory `InvalidOperationException` on a mismatch.
  - Alternatively, qualify every statement with the resolved database.
  - Document the rule in `docs/mysql.md`.
- **Status:** Open

#### MY-08 (Low): `CREATE TABLE IF NOT EXISTS` accepts an existing table of any shape, and the health check cannot tell

- **File:** `src/DbRelay.MySql/Scripts/001_create_outbox.sql:21-52`; `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:114`
- **Failure scenario:**
  - If `outbox_messages` already exists with another definition (an earlier hand-written script, a table from another tool, or
    an index a DBA dropped), `EnsureCreatedAsync` succeeds and `CheckHealthAsync` passes.
  - Then either the first claim fails with "Unknown column 'claim_at'", or, without `ix_claim`, every claim becomes a full-table
    `FOR UPDATE SKIP LOCKED` scan (see MY-12).
  - Oracle fixed this class of problem in OR-12: its health check fails while an index is missing or unusable.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Make the health check `SELECT claim_at, cleanup_at, dead_at, lock_token FROM t LIMIT 0`.
  - Add a check that `information_schema.STATISTICS` has the three configured index names for the table, as Oracle does.
- **Status:** Open

#### MY-09 (Low): on MySQL, an explicitly named CHECK constraint keeps its name across `RENAME TABLE`, so re-creating the outbox after renaming the old table aside fails

- **File:** `src/DbRelay.MySql/Scripts/001_create_outbox.sql:51`; `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:54`
- **Failure scenario:**
  - MySQL CHECK constraint names are unique per database. `RENAME TABLE` renames only generated names of the form
    `<table>_chk_N`; user-named constraints keep their names.
  - A common operation is `RENAME TABLE outbox_messages TO outbox_messages_2026` followed by `EnsureCreatedAsync` to start a
    fresh table. That fails with ER_CHECK_CONSTRAINT_DUP_NAME (3822) for `ck_outbox_messages_status`.
  - MariaDB scopes CHECK names per table, so it is unaffected.
- **Evidence:** Reasoned (MySQL documentation on CHECK constraint naming).
- **Recommendation:**
  - Use an unnamed CHECK (`CHECK (status BETWEEN 0 AND 3)`), so that MySQL generates `<table>_chk_1` and renames it with the
    table.
  - Alternatively, document the rename procedure (drop or rename the constraint first).
- **Status:** Open

#### MY-10 (Low): every `InvalidOperationException` on a no-transaction insert is reported as "the connection has a transaction in progress"

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:61-69`
- **Failure scenario:**
  - MySqlConnector raises `InvalidOperationException` for several other caller mistakes.
    - The most common is "This MySqlConnection is already in use", for example while an unbuffered Dapper reader is still open
      on the same connection (MySQL has no MARS). The string is verified in the 2.6.2 DLL.
  - With `transaction: null`, DbRelay rewrites any of these into "no transaction was passed while the connection has a
    transaction in progress. Pass that transaction..." That sends the developer the wrong way. The original is only the inner
    exception.
  - This is the same over-broad catch as OR-04's first residual risk. Here it only produces a wrong message.
- **Evidence:** Verified (code, plus the MySqlConnector string).
- **Recommendation:**
  - Wrap only the transaction-mismatch error: MySqlConnector's text says the transaction "is not the connection's active
    transaction; see https://mysqlconnector.net/trans".
  - Alternatively, make the wrapper neutral: "MySqlConnector rejected the outbox insert; if the connection has a transaction in
    progress, pass it ...".
  - Add a test with an open reader on the connection.
- **Status:** Open

#### MY-11 (Low): the supported range is wider than the tested one; MariaDB 11.8 LTS turns on `innodb_snapshot_isolation` by default

- **File:**
  - `docs/decisions/0006-mysql-provider.md` (Scope: "MySQL 8.0 and later", "MariaDB 10.11 and later");
  - `.github/workflows/ci.yml` (matrix: 8.0, 8.4, 10.11, 11.4);
  - `src/DbRelay.MySql/Internal/MySqlConnectionSource.cs:123-124`
- **Failure scenario:**
  - From MariaDB 11.6.2, and so in 11.8 LTS, `innodb_snapshot_isolation=ON` by default. Under REPEATABLE READ, locking a record
    changed after the transaction's read view was created raises ER_CHECKREAD (1020, "Record has changed since last read").
  - DbRelay's own transactions are READ COMMITTED, so they are unaffected.
  - The autocommitted single-row transitions and `Requeue`, however, run at the session default (normally REPEATABLE READ),
    and 1020 is not in `IsRetriable`.
    - Whether an autocommitted UPDATE without a consistent read opens a read view, and so can hit 1020 when it waits behind a
      batch or cleanup transaction, is unverified.
    - If it can, guarded acknowledgements fail spuriously under contention on the current MariaDB LTS.
  - MySQL 9.x LTS is likewise inside "8.0 and later" but untested.
- **Evidence:** Unverified (engine behaviour). The test gap itself is verified.
- **Recommendation:**
  - Add MariaDB 11.8 (and, when available, MySQL 9.x LTS) to `mysql-compat`, including the BA-07 stress test.
  - Either add 1020 to the retried errors for DbRelay-owned statements, or run the guarded single-row updates in RC
    transactions too.
  - Until then, state the tested versions in the ADR and the guide.
- **Status:** Open

#### MY-12 (Low): the claim depends on the optimizer choosing `ix_claim`; a filesort plan would lock every due row for the whole claim

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxSql.cs:73-74`; `tests/DbRelay.MySql.Tests/MySqlProviderTests.cs:376-420`
- **Failure scenario:**
  - With an index-ordered range scan, `LIMIT n` stops after n locked rows.
  - If the optimizer instead picks a table scan or `ix_claim` range plus filesort, InnoDB locks every row it reads before the
    sort, and the `LIMIT` is applied afterwards. Causes include:
    - small or freshly bulk-loaded tables with stale statistics;
    - MariaDB 11's new cost model;
    - a missing index (MY-08).
  - Under READ COMMITTED, rows cut by `LIMIT` keep their locks: only rows failing the WHERE are unlocked. Every due row then
    stays locked until the commit, which includes step 4's payload transfer. Concurrent claimers find nothing, so throughput
    collapses to one effective claimer.
  - The plan test pins the plan for one 20,000-row, freshly analysed dataset only.
  - The Oracle provider hints its claim index.
- **Evidence:** Reasoned.
- **Recommendation:**
  - Add `FORCE INDEX (ix_claim)`, which both engines support, to `ClaimCandidates`.
  - Likewise consider `FORCE INDEX (ix_cleanup)` and `(ix_dead)` for the `ORDER BY ... LIMIT` maintenance statements.
  - Extend the plan test with a 50-row table that has not been analysed.
- **Status:** Open

#### MY-13 (Info): documentation drift and gaps

- **File:**
  - `docs/decisions/0006-mysql-provider.md:84, 86, 95, 99-100`;
  - `src/DbRelay.MySql/Internal/MySqlConnectionSource.cs:74-78`;
  - `tests/DbRelay.MySql.Tests/MySqlFixture.cs:30`;
  - `CONTRIBUTING.md`, `IMPLEMENTATION_TRACKER.md`, `PROJECT_STATUS.md`
- **Details:**
  - The ADR says 1213 and 1205 are retried for transactions "which the server has rolled back". For 1205 inside a DbRelay
    transaction, InnoDB rolls back only the statement; DbRelay's dispose rolls back the rest. The code is right and the
    sentence is not.
  - "Within the 65,535-parameter limit" applies only to server-side prepared statements. DbRelay never calls `Prepare`, so the
    real limit is `max_allowed_packet` (MY-02).
  - "`transaction: null` ... MySqlConnector rejects the command" does not hold with `IgnoreCommandTransaction=true`. In that
    case the insert silently joins the active transaction. That outcome is safe, but it should be documented.
  - There is no `docs/mysql.md`, although the ADR requires one.
  - The fixture's skip message says "see CONTRIBUTING.md", but `CONTRIBUTING.md` does not mention `DBRELAY_TEST_MYSQL`.
  - `IMPLEMENTATION_TRACKER.md` and `PROJECT_STATUS.md` have no MySQL entries (CLAUDE.md: track work in the tracker).
- **Recommendation:** Fix the three ADR sentences. Add the guide, the CONTRIBUTING entry and the tracker rows.
- **Status:** Open

#### MY-14 (Info): the public SPI insert methods accept more than 1,000 records and then commit them in separately autocommitted chunks

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:76-120`
- **Failure scenario:**
  - Core never passes more than `MaxEnqueueBatchSize` records. `IOutboxStore` is public, however, and the store does not check.
  - A direct caller with 1,500 records and `transaction: null` gets two autocommitted statements. If the second fails, the first
    stays committed.
  - On `InsertStandaloneAsync`, a 1213 in the second chunk retries the whole operation, which re-inserts chunk 1 and fails with
    1062.
  - The XML comment on `ExecuteAsync` ("every operation passed here is one statement") is then false.
- **Recommendation:** Throw `ArgumentOutOfRangeException` when `records.Count > OutboxLimits.MaxEnqueueBatchSize`, and drop
  the loop.
- **Status:** Open

#### MY-15 (Info): server-side statement logs contain message payloads

- **File:** `src/DbRelay.MySql/Internal/MySqlOutboxStore.cs:97-117` (MySqlConnector substitutes parameters on the client)
- **Details:**
  - The server receives the INSERT with payloads, headers and correlation IDs inlined as literals.
  - With the general log, the slow query log (a 1,000-row insert can exceed `long_query_time`), audit plugins, or
    `performance_schema` statement history (truncated to `performance_schema_max_sql_text_length`, 1,024 by default), payload
    bytes land in server logs.
  - DbRelay itself logs nothing (invariant 9 holds). This is a deployment note, because operators of other providers expect
    bind values to stay out of statement text.
- **Recommendation:** Document it in `docs/mysql.md`. Payload confidentiality needs the server's logs to be scoped accordingly.
- **Status:** Open

#### MY-16 (Info): test gaps specific to MySQL and MariaDB

- **File:** `tests/DbRelay.MySql.Tests/*`
- **Gaps:**
  - There is no EXPLAIN pin for the PK IN-list and PK-equality statements:
    - claim step 3 and step 4;
    - lock-for-batch and the batch update;
    - the guarded single-row updates;
    - `SelectById`.

    If an `UNHEX` coercion ever stopped them using `PRIMARY`, a REPEATABLE READ guarded update would scan and lock the whole
    table.
  - No test shows the ADR's central isolation claim. For example: hold a claim or cleanup transaction open through a test seam
    (as Oracle's `ClaimTestPause`), and insert from a REPEATABLE READ producer with `innodb_lock_wait_timeout=1`. The insert
    must not wait.
  - There is no `max_allowed_packet` test (MY-02), and nothing pins the session isolation (MY-04).
  - The connection-option matrix covers only `GuidFormat`. It should also cover `UseAffectedRows=true`,
    `ConnectionReset=false`, `IgnoreCommandTransaction=true` and `DateTimeKind=Utc`/`Local`.
  - Cancellation is tested on DbRelay's own connection only, not on the caller's insert path. The Oracle fix (OR-09) covers
    both.
  - `Concurrent_claims_batches_stale_acknowledgements_and_cleanup_never_deadlock` asserts `DeadlockRetries == 0` (`:604`).
    - A batch locks rows in PK order while cleanup locks them in `cleanup_at` order. A stale batch holding two or more
      delivered rows that cleanup is deleting can therefore form a legitimate, retried, harmless cycle.
    - That makes the assertion a potential source of flakiness rather than a correctness check.
    - Consider asserting "no errors", and logging the retry count or bounding it.
- **Recommendation:** Add the plan pins and the producer-not-blocked test first. Both are cheap and guard the ADR's core claims.
- **Status:** Open

### Commands run in this review

| Command | Purpose | Result |
|---|---|---|
| `git diff --stat oracle-provider...mysql-provider`; `git log --oneline oracle-provider..mysql-provider`; `git diff --stat 77f9a55..HEAD` | Scope, and the commits that landed during the review | 19 files at `77f9a55`; later commits touch the soak harness only |
| `dotnet build tests/DbRelay.MySql.Tests/DbRelay.MySql.Tests.csproj -c Release` | Build the provider and tests on all target frameworks | Succeeded, 0 warnings, 0 errors |
| `dotnet test --project tests/DbRelay.MySql.Tests -c Release -f net10.0 --no-build` (no `DBRELAY_TEST_MYSQL`) | Non-database tests | 26 passed, 62 skipped, 0 failed (88 total) |
| `dotnet format <project> --verify-no-changes` (provider and tests) | Formatting | Exit 0 for both |
| Python string scan of `~/.nuget/packages/mysqlconnector/2.6.2/lib/net8.0/MySqlConnector.dll` | Confirm MySqlConnector's isolation SQL and error texts (MY-02, MY-04, MY-10) | `set session transaction isolation level`; "Error submitting {0}MB packet; ensure 'max_allowed_packet' ..."; "This MySqlConnection is already in use"; "... not the connection's active transaction; see https://mysqlconnector.net/trans" |
| Python replica of `MySqlOutboxSql.BuildInsert` | Size of the insert-text cache (MY-03) | 271,872 characters for 1,000 rows; 135.6 M characters (about 259 MiB of UTF-16) for all sizes from 1 to 1,000 |

MySQL and MariaDB database tests: **not run here (skipped: no server available)**.

### Verdict

**No Critical or High findings.** The invariants hold:
- caller resources are untouched;
- the insert uses exactly the caller's pair;
- no transaction spans a handler;
- the single-row transitions are token-guarded, and the batch path is token-guarded through locks;
- only the database clock is used;
- attempts are counted at claim time.

The claim and batch designs are race-free on InnoDB, and the READ COMMITTED choice is correct for producer concurrency.

**Three Medium findings to fix before the package is released:**
- MY-01: payload lengths read through InnoDB LOB fetches. This is a schema change, so it is cheapest before v1 ships.
- MY-02: an oversized enqueue kills the caller's connection, and the guide is missing.
- MY-03: up to about 259 MiB of cached insert texts.

MY-06 and MY-12 are small hardening changes worth making at the same time.

## MySQL provider review: resolution log (principal architect, 2026-11-27)

| ID | Severity | Resolution |
|---|---|---|
| MY-01 | Medium | **Fixed.** `payload_size BIGINT AS (OCTET_LENGTH(payload)) STORED`; the claim reads it instead of the payloads. Test: `Payload_size_is_stored_at_insert`. |
| MY-02 | Medium | **Fixed.** A call estimated at 1 MiB or more is checked against the server's `max_allowed_packet` (read on the connection, cached per server for 5 minutes) and rejected before sending. The size counts bytes as sent: each payload byte plus one per byte MySQL escaping may double, and UTF-8 text likewise. A first version counted every byte twice and rejected a legitimate 10.5 MB call on MariaDB (CI round 2). Test: `A_call_larger_than_max_allowed_packet_is_rejected_and_the_callers_transaction_survives`. The guide (`docs/mysql.md`) documents it. |
| MY-03 | Medium | **Fixed.** Insert texts are cached only up to 64 rows. |
| MY-04 | Low | **Fixed.** DbRelay's transactions are begun with `SET TRANSACTION ISOLATION LEVEL READ COMMITTED; START TRANSACTION`, without MySqlConnector's `BeginTransaction`, which sets the session isolation level (confirmed in the 2.6.2 binary). They end with `COMMIT`, or `ROLLBACK` on any failure. Test: `DbRelay_transactions_leave_a_pooled_sessions_isolation_level_unchanged` (one pooled connection, `ConnectionReset=false`: still REPEATABLE-READ afterwards, and no open transaction). |
| MY-05 | Low | **Fixed.** `@@log_bin` and `@@binlog_format` are read once per connection source; with a statement-based log, the session default is kept. Not tested in CI, because the containers use row-based logging. |
| MY-06 | Low | **Fixed.** The batch UPDATE is guarded by `(id, lock_token) IN (...)`, and its row count must equal the locked matches, or the transaction rolls back. Test: `Claims_and_acknowledgements_work_with_either_affected_rows_setting`. |
| MY-07 | Low | **Fixed.** With no `Schema`, an insert on a connection whose current database differs from DbRelay's is rejected with guidance. Test: `An_unqualified_outbox_rejects_a_connection_to_another_database`. |
| MY-08 | Low | **Fixed.** `EnsureCreatedAsync` and the health check verify the three indexes and all 23 columns. Tests: `EnsureCreated_and_the_health_check_reject_a_table_of_the_wrong_shape` and `The_health_check_fails_while_an_index_is_missing`. |
| MY-09 | Low | **Fixed.** The CHECK constraint is unnamed. Its generated name `<table>_chk_1` limits MySQL table names to 58 characters, which registration now enforces (found in CI round 2). Test: `The_table_can_be_created_again_after_the_old_one_is_renamed_aside`. |
| MY-10 | Low | **Fixed.** The guidance is given only for MySqlConnector's missing-transaction error; other `InvalidOperationException`s pass through unchanged. |
| MY-11 | Low | **Fixed.** Error 1020 is retried; CI adds MariaDB 11.8 and MySQL 9. |
| MY-12 | Low | **Fixed.** `FORCE INDEX (ix_claim)` on the claim. A missing index fails loudly, and the health check reports it (MY-08). |
| MY-13 | Info | **Fixed.** `docs/mysql.md`, ADR 0006 ("Review follow-ups"), CONTRIBUTING and the tracker. |
| MY-14 | Info | **Fixed.** At most 1,000 records per insert at the storage interface. Test: `The_storage_interface_rejects_more_than_the_enqueue_limit_in_one_call`. The Oracle provider gets the same limit. |
| MY-15 | Info | **Documented.** MySqlConnector sends values in the statement text, so a server's general or slow query log can contain payloads (`docs/mysql.md`). DbRelay's own logging never includes them (invariant 9). |
| MY-16 | Info | **Fixed.** New tests: producers inserting while a claim holds its locks; the session isolation level afterwards; oversized calls; the other database; a wrongly shaped table; a missing index; re-creation after a rename; `UseAffectedRows` on and off; primary-key plans for every by-ID statement. The `DeadlockRetries == 0` assertion stays (never weakened); it passed on every engine in CI. |
