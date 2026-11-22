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