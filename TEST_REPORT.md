# DbRelay Test Report

Author: QA and Reliability Engineer · Date: 2026-11-20 · Scope: PostgreSQL provider, engine, end-to-end reliability

## 1. Summary

| Project | TFMs | Tests per TFM | Passed | Failed | Skipped | Verdict |
|---|---|---|---|---|---|---|
| `tests/DbRelay.Tests` (unit, in-memory store) | net8.0, net9.0, net10.0, net11.0 | 68 | 272 | 0 | 0 | PASS |
| `tests/DbRelay.PostgreSql.Tests` (provider + conformance, real PostgreSQL) | net8.0, net9.0, net10.0, net11.0 | 74 | 296 | 0 | 0 | PASS |
| `tests/DbRelay.IntegrationTests` (new: real hosts, real child processes, real PostgreSQL) | net8.0, net9.0, net10.0, net11.0 | 17 | 56 | 12 | 0 | **FAIL: 3 tests per TFM expose BUG-1** |
| `tests/DbRelay.SqlServer.Tests` | - | - | - | - | - | **PENDING** (still being written by the SQL Server engineer; not run) |

Every integration failure is the same `src/` bug (BUG-1, section 6). It reproduced on every run and every TFM, so it is deterministic and not a flake. The other 14 integration tests passed on every final run: 3 runs on net10.0 and 1 run on all four TFMs. One test-tolerance flake (F-1, section 7) showed up in an earlier all-TFM run, before the final code freeze. It is reported below together with the change that fixed it.

No test was skipped. A real PostgreSQL was available through `DBRELAY_TEST_POSTGRES`, so no test result is a disguised skip.

## 2. Environment

| Item | Value |
|---|---|
| OS | Windows 10 Home 10.0.19045, x64 |
| CPU / RAM | Intel Core i5-5350U @ 1.80 GHz (2 cores, 4 logical), 7.9 GB. This is a slow machine; the timings below reflect it. |
| .NET SDK | 11.0.100 (user-local, `%USERPROFILE%\.dotnet`), MSBuild 18.12.2 |
| Runtimes | Microsoft.NETCore.App 8.0.34, 9.0.23, 10.0.15, 11.0.0 |
| Test framework | xUnit v3 4.0.3 on Microsoft.Testing.Platform (`global.json` test runner = MTP) |
| PostgreSQL | PostgreSQL 17.5 on x86_64-windows (msvc-19.44), private cluster at `localhost:55432`, user `postgres`, trust auth, `max_connections = 200` |
| Database selection | `DBRELAY_TEST_POSTGRES=Host=localhost;Port=55432;Username=postgres;Database=postgres;Include Error Detail=true` |
| Docker | **Not running** (`//./pipe/dockerDesktopLinuxEngine` not found). The Testcontainers path was not exercised. |
| SQL Server | Not used (tests pending) |

Note: the private PostgreSQL cluster was restarted once between sessions (on 2026-11-20). All **final** runs listed below happened after that restart.

## 3. Commands run

Every command was preceded by the environment script (it sets PATH and `DBRELAY_TEST_POSTGRES`, then `cd d:\DbRelay`). All of them were run from `d:\DbRelay`.

```powershell
# build check (warnings are errors)
dotnet build tests/DbRelay.IntegrationTests/DbRelay.IntegrationTests.csproj -c Release

# integration tests, net10.0, three consecutive runs (final code)
dotnet test --project tests/DbRelay.IntegrationTests/DbRelay.IntegrationTests.csproj -c Release -f net10.0   # x3

# integration tests, all TFMs (the SDK runs the four TFM test processes in parallel against the same database)
dotnet test --project tests/DbRelay.IntegrationTests/DbRelay.IntegrationTests.csproj -c Release

# existing suites, all TFMs
dotnet test --project tests/DbRelay.Tests/DbRelay.Tests.csproj -c Release
dotnet test --project tests/DbRelay.PostgreSql.Tests/DbRelay.PostgreSql.Tests.csproj -c Release
```

Final results (exit code 2 means "at least one test failed"):

| Run | Exit | Elapsed | Total | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| Integration net10.0, run 1 | 2 | 1:35.4 | 17 | 14 | 3 (BUG-1) | 0 |
| Integration net10.0, run 2 | 2 | 1:20.5 | 17 | 14 | 3 (BUG-1) | 0 |
| Integration net10.0, run 3 | 2 | 1:22.0 | 17 | 14 | 3 (BUG-1) | 0 |
| Integration all TFMs | 2 | 2:10.5 | 68 | 56 | 12 (BUG-1, 3 per TFM) | 0 |
| Unit all TFMs | 0 | 0:34.8 | 272 | 272 | 0 | 0 |
| PostgreSQL provider all TFMs | 0 | 0:50.3 | 296 | 296 | 0 | 0 |

Integration results per TFM (final all-TFM run):

| TFM | Passed | Failed | Skipped | Duration |
|---|---|---|---|---|
| net8.0 | 14 | 3 | 0 | 1m 42.2s |
| net9.0 | 14 | 3 | 0 | 1m 42.5s |
| net10.0 | 14 | 3 | 0 | 1m 26.9s |
| net11.0 | 14 | 3 | 0 | 1m 39.3s |

Unit results per TFM: 68 passed, 0 failed, 0 skipped on each of net8.0, net9.0, net10.0 and net11.0 (17.4 to 18.0 s each).
PostgreSQL provider results per TFM: 74 passed, 0 failed, 0 skipped on each TFM (29.5 to 32.6 s each).

Earlier runs, before the code freeze (reported for completeness):
- 2 development runs on net10.0: 17 tests, 14 passed, 3 failed (BUG-1).
- 3 runs on net10.0 with the previous retry-gap assertion: 14 passed, 3 failed (BUG-1) each time.
- 1 all-TFM run with the previous retry-gap assertion: 68 total, 55 passed, **13 failed**. 12 of those were BUG-1; the 13th was flake F-1 on net11.0.

**Important:** the two new projects are **not** in `DbRelay.slnx`, because that file was out of scope for this task. A plain `dotnet test -c Release` at the repo root therefore does **not** run the integration tests. Whoever owns the solution needs to add `tests/DbRelay.IntegrationTests` and `tests/DbRelay.CrashWorker`.

## 4. What was built

- `tests/DbRelay.IntegrationTests` is an xUnit v3 Exe. It references only `xunit.v3`, Dapper, Npgsql, Microsoft.Extensions.Hosting and Testcontainers.PostgreSql.
  - `PostgreSqlFixture` was copied from the provider tests. It uses `DBRELAY_TEST_POSTGRES` first and falls back to Testcontainers. If neither works, every test is skipped via `Assert.SkipWhen`.
  - Each test gets its own schema (`it_<16 hex>`). The schema is created with `IOutboxSchemaManager.EnsureCreatedAsync()` and has an `orders` business table. It is dropped on dispose; after the runs, `0` `it_%` schemas were left behind.
  - Tests inside one assembly run one at a time (`[assembly: Parallelization(Mode = ParallelMode.None)]`). The four TFM processes still run in parallel against the same database, so the suite still sees cross-process contention.
  - Hosts are real `HostApplicationBuilder` hosts running the real `OutboxDispatcher` through `AddDbRelay` + `UsePostgreSql`.
- `tests/DbRelay.CrashWorker` is a console Exe and not a test project. It takes the connection string, schema, marker file, mode (`hang` | `kill`), max attempts, lease and timeout as arguments, and runs a DbRelay host. Its handler appends `DELIVERED <id> <attempt>` to the marker file and flushes it to disk. In `hang` mode it then blocks forever and ignores cancellation. In `kill` mode it terminates its own process (`Process.Kill()`). Tests start it with `dotnet <dll>`, using the same TFM build.
- Metrics are written to the test output and to `bin/Release/<tfm>/integration-metrics.log`.

## 5. Scenario-to-test traceability and results

| # | Scenario (brief) | Test(s) | Result (all final runs) |
|---|---|---|---|
| 1 | 4 hosts x 2,000 messages: every message Delivered and delivered at least once, 0 duplicates, wall time | `MultiWorkerTests.Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run` | PASS 7/7 |
| 2 | Kill a child worker after "delivery": row stays Processing with attempt 1, is redelivered after lease expiry with Attempt 2, ends Delivered, and the child really died | `CrashRecoveryTests.Killed_worker_process_message_is_redelivered_after_lease_expiry` | PASS 7/7 |
| 3 | Crash loop (`MaxAttempts = 2`, worker kills itself) ends in DeadLettered with the crash-loop reason; handler ran exactly MaxAttempts times; final claim did not invoke the handler | `CrashRecoveryTests.Message_that_kills_its_worker_every_time_ends_dead_lettered_after_MaxAttempts` | PASS 7/7 |
| 4 | A stale lock token cannot MarkDelivered, ScheduleRetry, DeadLetter or Release. Checked both while the new owner holds the lease and after it delivered. | `StaleWorkerTests.Stale_lock_token_cannot_overwrite_while_the_new_owner_delivers_or_after_it_delivered` | PASS 7/7 |
| 4b | Two real dispatchers: a slow one outlives its lease. The fast one redelivers (attempt 2). The slow one's "Delivered" is rejected (lease lost, event 1007) and the row keeps the fast host's data. | `StaleWorkerTests.Slow_dispatcher_that_outlives_its_lease_cannot_overwrite_the_faster_dispatchers_outcome` | PASS 7/7 |
| 5 | `pg_terminate_backend` against the dispatcher's own connections (`Application Name=dbrelay-it-<guid>`): host keeps running, health goes non-Healthy and then recovers, every message is Delivered | `DatabaseInterruptionTests.Dispatcher_survives_terminated_connections_and_delivers_everything` | PASS 7/7 |
| 6 | 200 Dapper business transactions with random endings (commit, rollback, dispose without commit, business failure after enqueue): delivered set == committed set | `TransactionAtomicityTests.Only_committed_business_transactions_are_ever_delivered` | PASS 7/7 |
| 7 | Graceful StopAsync: in-flight deliveries finish as Delivered; the rest are released to Pending with attempts restored (0); nothing stays Processing. Also invariant 3: no open transaction or lock during handlers. | `GracefulShutdownTests.StopAsync_finishes_in_flight_deliveries_and_releases_the_rest_with_attempts_restored` | PASS 7/7 |
| 7b | Shutdown timeout: in-flight handlers are cancelled, every claim is released (Pending, attempt 0, no last_error) | `GracefulShutdownTests.Shutdown_timeout_cancels_in_flight_deliveries_and_releases_every_claim` | PASS 7/7 |
| 8 | Fails twice then succeeds (InitialDelay 200 ms, MaxDelay 1 s): attempts = 3, last_error recorded, delays inside the jitter bounds | `RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds` | PASS 7/7 final runs; see F-1 for the earlier flake |
| 8b | Exhaustion leads to dead-letter; manual requeue (`IOutboxAdmin.RequeueAsync`) redelivers from attempt 1; a second requeue returns false | `RetryScheduleTests.Exhausted_message_is_dead_lettered_and_manual_requeue_redelivers_it_from_attempt_one` | PASS 7/7 |
| 9a | Payloads (JSON and raw bytes) and connection strings never appear in logs while delivering, retrying, dead-lettering and permanently failing | `SecurityTests.Payloads_and_connection_strings_never_appear_in_logs_while_delivering_retrying_and_dead_lettering` | PASS 7/7 |
| 9b | Error text is truncated to 2,000 characters in the database (exception, dead-letter reason, exhausted retry) | `SecurityTests.Long_error_text_is_truncated_to_2000_characters_in_the_database`, `SecurityTests.Long_retry_reason_is_truncated_while_the_message_waits_for_its_retry` | PASS 7/7 |
| 9c | Error text that PostgreSQL cannot store (U+0000, a surrogate pair split by truncation) must still record the outcome | `SecurityTests.Error_text_that_postgresql_cannot_store_still_records_the_outcome` x3 (`nul-in-exception`, `nul-in-dead-letter-reason`, `surrogate-split-by-truncation`) | **FAIL 7/7: BUG-1** |

"7/7" counts the final-code test executions: 3 net10.0 runs plus the 4 TFM processes of the all-TFM run. Scenarios 1 to 7 and 9a/9b also passed in every pre-freeze run.

### How each claim is proven

- **Scenario 1.** Hosts start first; then 4 producers commit 200 transactions of 10 orders each, so claims compete with inserts. The test asserts:
  - ids enqueued == ids delivered;
  - deliveries == 2,000, so 0 duplicates;
  - every handler `Attempt == 1`;
  - every row is Delivered with `attempt_count = 1` and a null lock token;
  - every host delivered something;
  - no lease was lost (event 1007).
- **Scenario 2.**
  - **Before the kill:** the marker shows exactly `DELIVERED <id> 1`.
  - **The process really died:** after `Kill(entireProcessTree: true)` and `WaitForExit`, `HasExited` is true and a fresh `Process.GetProcessById(pid)` lookup reports it not running. The child's PostgreSQL sessions (unique application name) also disappear from `pg_stat_activity`.
  - **The claim survived the crash:** the row is `Processing`, `attempt_count = 1`, with a non-null token, and `locked_by` contains `:<child pid>:`.
  - **Redelivery:** an in-process host gets `Attempt == 2` exactly once. The row ends Delivered with `attempt_count = 2`. The second claim's database time (`last_attempt_at`) is at or after the first lease's `available_at`.
- **Scenario 3.**
  - Worker spawns were 3 in every run. Spawns 1 and 2 crashed with exit code -1.
  - Spawn 3 was still alive when the row became DeadLettered and was then killed by the test.
  - The marker holds exactly `DELIVERED <id> 1` and `DELIVERED <id> 2`.
  - `last_error = "Exceeded the maximum of 2 attempts without a recorded outcome (possible worker crash loop)."` and `attempt_count = 3`.
- **Scenario 5.** Three bursts of 2 s each, terminating the dispatcher's backends every 25 ms, with 1.5 s of calm in between. A producer on a separate (untargeted) data source enqueues one message about every 10 ms. Health is sampled every 50 ms. The test asserts:
  - backends were actually terminated, and the dispatcher logged failures (claim failures 1002 or persist retries 1008);
  - at least one non-Healthy sample inside an outage window;
  - the last sample is Healthy;
  - `ApplicationStopping` never fired;
  - deliveries continued after the last burst;
  - every row is Delivered.
- **Scenario 6.** Seed `20261119` gives 103 commits, 32 rollbacks, 32 disposes without commit and 33 business failures (unique violation after the enqueue). Transactions stay open for 0 to 25 ms while the dispatcher polls every 20 ms. The test asserts:
  - delivered set == committed set, with no intersection with aborted ids and no duplicates;
  - outbox rows == committed ids, and orders == committed orders;
  - after every transaction, the caller's connection is still `Open` and usable (`SELECT 1`), so DbRelay did not close it.
- **Scenario 7.** The whole batch is claimed (20 rows Processing, attempt 1), and 4 handlers are held open.
  - **Invariant 3:** while the handlers run, no session of this host has `xact_start` set and no relation, tuple or transactionid locks are held.
  - **During StopAsync:** the 16 unstarted rows go back to Pending with attempt 0 before StopAsync returns.
  - **After the handlers are released:** 4 rows are Delivered (attempt 1), 16 are Pending (attempt 0, null token and `locked_by`, immediately claimable), 0 are Processing, and no handler saw cancellation.
- **Scenario 8.** A test-only `AFTER UPDATE` trigger in the test schema records each row change stamped with the statement's `now()`. The test asserts:
  - the applied delay `available_at - now()` of each ScheduleRetry is inside `[d/2, d]` (to the millisecond);
  - the next claim never happened before that `available_at`;
  - the handler-observed gap is at least `d/2`;
  - `last_error` is `InvalidOperationException: transient failure #1`, then `#2`, as seen by the next attempt, and `#2` stays after success.
- **Scenario 9a.** A capturing `ILoggerProvider` at Trace level records the formatted message, structured state, scopes and exception `ToString()`. The test first proves the capture is not empty: Delivered (1004) >= 2, RetryScheduled (1005) >= 1, DeadLettered (1006) >= 3, and the message id is present. It then asserts the unique payload marker, the base64 of the raw payload, the unique application name and `Host=` never appear.

## 6. Bugs found in `src/` (reported, not fixed)

### BUG-1: Error text that PostgreSQL cannot store makes the outcome unrecordable, so a permanently failed message is redelivered once per lease until the crash-loop guard fires, and the real error is lost

Severity: **Medium** (reliability and operability; no data loss, but duplicate deliveries of messages the handler declared poisonous).

**Failing tests:** `SecurityTests.Error_text_that_postgresql_cannot_store_still_records_the_outcome` (3 cases). They fail on every TFM and every run. They are not marked Skip because they do not block the rest of the suite.

**Scenario:** `MaxAttempts = 3`, `LeaseDuration = 2 s`. The handler either returns `DeliveryResult.DeadLetter(reason)` or throws an exception that `IsPermanentFailure` classifies as permanent. The reason or exception message contains either:
- a NUL character, e.g. `"bad byte \0 in error from a binary protocol"`; or
- a string over 2,000 characters with a surrogate pair at UTF-16 index 1998/1999, e.g. `new string('x', 1998) + "😀" + ...`.

**Expected:**
- the handler is called once;
- the row becomes DeadLettered on attempt 1 (`attempt_count = 1`) with a stored, sanitized reason;
- no "Recording outcome failed" errors are logged.

**Actual** (identical on net8.0, net9.0, net10.0 and net11.0):
```
status=DeadLettered attemptCount=4 handlerCalls=3 persistRetries=6 persistFailed=3
lastError='Exceeded the maximum of 3 attempts without a recorded outcome (possible worker crash loop)'
persistException (NUL cases)       = Npgsql.PostgresException (0x80004005): 22021: invalid byte sequence for encoding "UTF8": 0x00
persistException (surrogate case)  = System.Text.EncoderFallbackException: Unable to translate Unicode character \uD83D at index 1998 to specified code page.

Assert.Equal() Failure: Values differ
Expected: 1
Actual:   3            (handler calls)
  at SecurityTests.Error_text_that_postgresql_cannot_store_still_records_the_outcome ... SecurityTests.cs:240
```

**Root cause:**
1. `src/DbRelay/Internal/ErrorText.cs:22`: `Truncate` cuts at UTF-16 index `MaxErrorLength - 1` (1999) without checking for surrogate pairs. A high surrogate at index 1998 survives unpaired, and Npgsql's strict UTF-8 encoder refuses it.
2. `src/DbRelay/Internal/ErrorText.cs:11-23` (and `FromException`) does not remove U+0000. `src/DbRelay.PostgreSql/Internal/PostgreSqlOutboxStore.cs:87` (`ScheduleRetryAsync`) and `:90` (`MarkDeadLetteredAsync`) pass the text unchanged as `text`, and PostgreSQL rejects NUL in `text` values (SQLSTATE 22021).
3. Amplifier: `src/DbRelay/Dispatching/OutboxDispatcher.cs:373-400` (`PersistAsync`) treats this deterministic data error like a transient database failure. It retries 3 times (200 ms, then 400 ms), gives up and leaves the row `Processing`. After the lease expires, the message is claimed again and the handler runs again. When `attempt_count > MaxAttempts`, the crash-loop guard at `OutboxDispatcher.cs:241-247` dead-letters it with a generic reason, and the real reason is never stored.

**Impact:**
- With the defaults (`LeaseDuration` 2 min, `MaxAttempts` 10), a message the handler rejected as poison is delivered about 10 times over about 20 minutes. It finally lands in dead-letter with a misleading "possible worker crash loop" reason.
- The same applies to the retry path (`OutboxDispatcher.cs:348`, ScheduleRetry): a transient failure whose message contains NUL gets its backoff replaced by the lease duration, and each failed persist logs an Error (1009).
- Realistic triggers: NUL bytes in exception messages from binary protocols and deserializers, and non-BMP characters (emoji, some CJK) in long error texts.
- SQL Server `nvarchar` accepts both NUL and lone surrogates, so it is probably not affected. This is unverified (SQL Server tests pending).

**Suggested fix** (for the engine owner; not applied):
- Sanitize in `ErrorText`: replace U+0000 and lone surrogates (for example with U+FFFD), and never split a surrogate pair when truncating.
- Optionally, let `PersistAsync` fall back to a fixed, safe reason when the store rejects the text, instead of abandoning the outcome.

No other `src/` defects were found by the integration scenarios.

## 7. Flaky tests

### F-1: `RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds`. Upper bound exceeded under load (test defect, fixed)

- **Observed:** once, in the pre-freeze all-TFM run on net11.0, while four test processes ran in parallel on the 2-core machine:
  ```
  Assert.InRange() Failure: Value not in range
  Range:  (00:00:00.0950000 - 00:00:01.0050000)
  Actual: 00:00:01.0700661
    at RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds() in RetryScheduleTests.cs:78
  ```
  It passed in the other 5 pre-freeze executions (3 net10.0 runs and the net8.0, net9.0 and net10.0 processes of that same all-TFM run).
- **Cause:** the old assertion measured the gap between handler invocations on the application clock, with a fixed allowance of 805 ms for polling, round trips and scheduling. That gap measures machine load as well as the retry schedule. The library was not at fault: the database-side measurements added afterwards show the applied delays always inside `[d/2, d]`.
- **Fix** (in the test only):
  - The schedule is now verified exactly on the database clock through a test-only history trigger: the applied delay must be within `[d/2, d]` ± 1 ms, and the next claim must not happen before `available_at`.
  - The handler-observed gap keeps its strict lower bound (`>= d/2`). Its upper bound is now a gross sanity check (`d + 5 s`).
  - This is stricter on the property that matters (the schedule) and no longer depends on host load.
- **After the fix:** 7/7 passes, with applied delays 120.8 to 193.3 ms (bounds 100 to 200) and 247.3 to 397.0 ms (bounds 200 to 400).

No other intermittent failure was observed. The duplicate counts in scenario 5 vary from run to run (0 to 3), but that is allowed at-least-once behaviour (see section 9), not a flaky assertion.

## 8. Timings

Final net10.0 runs (each run on its own, other TFMs idle):

| Metric | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| S1: enqueue 2,000 msgs (200 tx, 4 producers) | 2,077 ms | 1,076 ms | 1,947 ms |
| S1: wall time, enqueue start to all Delivered | **2,890 ms** (692 msg/s) | **3,797 ms** (527 msg/s) | **3,395 ms** (589 msg/s) |
| S1: deliveries per host | 525/500/500/475 | 650/450/475/425 | 450/475/550/525 |
| S1: duplicates / lease lost | 0 / 0 | 0 / 0 | 0 / 0 |
| S2: child start until it received the message | 8,749 ms | 1,097 ms | 1,450 ms |
| S2: redelivery after lease expiry (DB clock) | +33 ms | +57 ms | +61 ms |
| S2: total | 12.6 s | 5.1 s | 5.5 s |
| S3: crash loop total (3 spawns) | 10.0 s | 9.5 s | 10.0 s |
| S5: messages / deliveries / duplicates | 568 / 568 / 0 | 597 / 600 / 3 | 528 / 528 / 0 |
| S5: backends terminated | 62 | 71 | 68 |
| S5: claim failures / persist retries / persist given up | 4 / 12 / 0 | 3 / 24 / 3 | 1 / 28 / 0 |
| S5: health samples Healthy / Degraded / Unhealthy | 77 / 35 / 23 | 90 / 25 / 22 | 111 / 6 / 9 |
| S8: applied retry delays (DB clock) | 121.0 / 314.6 ms | 178.3 / 271.5 ms | 158.7 / 385.7 ms |

Final all-TFM run (4 TFM processes in parallel, sharing CPU and database):

| Metric | net8.0 | net9.0 | net10.0 | net11.0 |
|---|---|---|---|---|
| S1: wall time (throughput) | 3,641 ms (549/s) | 5,549 ms (360/s) | 4,060 ms (493/s) | 4,023 ms (497/s) |
| S1: duplicates | 0 | 0 | 0 | 0 |
| S2: child start until message received | 8,456 ms | 8,390 ms | 4,123 ms | 8,208 ms |
| S2: redelivery after lease expiry | +121 ms | +72 ms | +104 ms | +31 ms |
| S3: crash loop total | 10.9 s | 12.0 s | 11.1 s | 11.0 s |
| S5: messages / deliveries / duplicates | 384 / 386 / 2 | 459 / 459 / 0 | 468 / 468 / 0 | 377 / 378 / 1 |
| S8: applied retry delays | 193.3 / 397.0 ms | 175.6 / 338.2 ms | 181.5 / 247.5 ms | 120.8 / 345.1 ms |

The throughput numbers come from a correctness test (handler latency 1 to 5 ms, 8 slots per host, about 12 pooled connections per host) on a laptop CPU. They are not benchmarks.

## 9. Limitations and observations

- **Duplicates after a database outage are by design, but worth documenting.**
  - An outcome is given up after 3 persist attempts spread over about 0.6 s (`OutboxDispatcher.PersistAttempts`, 200 ms then 400 ms). If the database is unreachable for longer than that while deliveries finish, those messages are redelivered after their lease. Scenario 5 saw 0 to 3 such duplicates per run (event 1009 count == duplicate count).
  - With the default 2-minute lease, the redelivery is also delayed by up to 2 minutes. Consumers must be idempotent, which matches the at-least-once contract.
- **Error text may carry payload data through user exceptions.** DbRelay never logs payloads itself, and scenario 9a passes. However, it stores and logs the handler's exception message and `DeliveryResult` reason verbatim (events 1005 and 1006, and `last_error`). If a handler's exception text contains payload values (for example .NET's `FormatException: The input string '<value>' was not in a correct format.`), those values reach logs and the database. This is worth stating in the security docs.
- **Docker is not running**, so Testcontainers start-up was not exercised. All database tests used the configured cluster.
- **PostgreSQL 17.5 only.** The architecture lists 15 and 17/18; 15 and 18 were not tested.
- **Windows only.** Process-kill semantics were verified with `TerminateProcess`; Linux `SIGKILL` was not tested.
- **Invariant 5 (database clock)** is exercised implicitly: lease expiry and retry availability were checked on the database clock. It was not tested with a deliberately skewed application clock.
- **A shutdown cancelling an in-flight claim statement** could, in a very narrow window, commit a claim the client never reads. Those rows would wait one lease. This was not reproduced and is noted as theoretical.
- **The new projects are not in `DbRelay.slnx`** (see section 3).
- **SQL Server: pending.** `tests/DbRelay.SqlServer.Tests` is still being written by the SQL Server engineer and was not run here. The integration scenarios currently target PostgreSQL only.

---

## Addendum: re-verification after BUG-1 and the review fixes (principal architect, 2026-11-21)

The sections above are the QA engineer's original report. Since then:
- BUG-1 was fixed (`ErrorText` sanitisation plus the fallback outcome).
- Independent review findings CR-01 to CR-22 were addressed; see the resolution log in `CODE_REVIEW_REPORT.md`.
- Several tests were added or revised.

Every suite was then re-run by the architect, with nothing else running on the machine.

**Environment:** Windows 10 22H2, Intel i5-5350U (2 cores / 4 threads), .NET SDK 11.0.100 with runtimes 8.0.34 / 9.0.23 / 10.0.15 /
11.0.0, PostgreSQL 17.5 (private cluster, localhost:55432), SQL Server 2022 Express RTM (`dbrelay_tests` RCSI off,
`dbrelay_tests_rcsi` RCSI on). Docker was not running, so all database tests used `DBRELAY_TEST_*`; nothing was skipped.

**Command:** `dotnet test --project tests/<project> -c Release --no-build` (all four target frameworks, after a clean
`dotnet build DbRelay.slnx -c Release`: 0 warnings, 0 errors).

| Project | net8.0 | net9.0 | net10.0 | net11.0 | Total | Failed | Skipped |
|---|---|---|---|---|---|---|---|
| DbRelay.Tests (unit) | 87 | 87 | 87 | 87 | **348** | 0 | 0 |
| DbRelay.PostgreSql.Tests | 75 | 75 | 75 | 75 | **300** | 0 | 0 |
| DbRelay.SqlServer.Tests (conformance runs twice: RCSI off and on) | 93 | 93 | 93 | 93 | **372** | 0 | 0 |
| DbRelay.IntegrationTests (PostgreSQL) | 17 | 17 | 17 | 17 | **68** | 0 | 0 |
| **All** | | | | | **1,088** | **0** | **0** |

The integration suite was also run twice back-to-back on all TFMs after the last test revision: 68/68 both times.

**Status changes since the original report:**
- **BUG-1:** **fixed**. `SecurityTests.Error_text_that_postgresql_cannot_store_still_records_the_outcome` (3 cases) passes on all TFMs.
- **Scenario 4b** (`StaleWorkerTests`) was revised because of CR-05. A handler that ignores its timeout is now abandoned inside its lease, so
  its late result is never written. The test is now `Handler_that_ignores_its_timeout_is_abandoned_and_its_late_result_never_overwrites_the_redelivery`.
  It asserts event 1013, no event 1007, exactly one redelivery, and that the row is unchanged by the late result.
  The store-level stale-token guard is still covered by scenario 4.
- **Scenario 7** (shutdown timeout) was revised because of CR-06. Interrupted in-flight deliveries keep their attempt
  (`last_error` = "Delivery was interrupted by host shutdown."). Unstarted claims give it back.
- **Scenario 1** was renamed to `Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run` (T-08).
- **New tests:**
  - Conformance (both providers): `Claim_tolerates_rows_with_headers_DbRelay_did_not_write`, which writes bad rows directly with SQL.
  - Unit:
    - hung-handler abandonment;
    - shutdown-outlasting handler reaches dead-letter;
    - ambiguous-commit acknowledgement;
    - outcome fallback when the reason cannot be stored;
    - header tolerance (8 cases);
    - storable error text;
    - identifier control characters;
    - generic type names;
    - serializer options not frozen;
    - forgotten-message guard.
- **SQL Server:** no longer pending; see the table above.
- **Samples:** `PostgreSql.Sample` was re-run after the fixes. `/health` returned 200, `POST /orders` was delivered on attempt 1, and the status was read back via `/outbox/messages/{id}`.

**Open items:**
- **K6:** a one-off 5/364 SQL Server failure under heavy machine overload (2026-11-20). Not reproduced in four later full runs. The failing test names were not captured.
- **Testcontainers path:** only exercised in its "Docker unavailable → skip" branch locally. CI uses service containers and the same env-var path.
- **Database versions:** only PostgreSQL 17.5 and SQL Server 2022 have been tested. The CI matrix adds PostgreSQL 15/16/18 once it runs.
### Superseding run after the follow-up audit fixes (2026-11-22)

| Project | Per TFM | Total (4 TFMs) | Failed | Skipped |
|---|---|---|---|---|
| DbRelay.Tests | 96 | **384** | 0 | 0 |
| DbRelay.PostgreSql.Tests | 75 | **300** | 0 | 0 |
| DbRelay.SqlServer.Tests | 95 | **380** | 0 | 0 |
| DbRelay.IntegrationTests | 19 | **76** | 0 | 0 |
| **All** | | **1,140** | **0** | **0** |

**New tests:**
- **FU-01:** SQL Server invalid-JSON and invalid-UTF-16 headers.
- **FU-T1:** a real dispatcher's lost lease.
- **FU-04:** prompt shutdown with a blocked claim.
- **FU-02:** orphan limit.
- **FU-05:** negative ambiguous acknowledgement.
- **FU-07:** nested-generic names.
- **Unreadable rows:** placeholder dead-lettering.

**Mutation checks (fix reverted temporarily, new test confirmed failing, fix restored):** FU-01 on SQL Server (RCSI off and on) and FU-04.

### Run after batched acknowledgements, the BA review fixes and the soak runs (2026-11-22)

| Project | Per TFM | Total (4 TFMs) | Failed | Skipped |
|---|---|---|---|---|
| DbRelay.Tests | 104 | **416** | 0 | 0 |
| DbRelay.PostgreSql.Tests | 84 | **336** | 0 | 0 |
| DbRelay.SqlServer.Tests (RCSI off and on) | 113 | **452** | 0 | 0 |
| DbRelay.IntegrationTests | 19 | **76** | 0 | 0 |
| **All** | | **1,280** | **0** | **0** |

The build had 0 warnings, and `dotnet format --verify-no-changes` was clean.

**Soak and chaos runs** (`docs/soak/`): PostgreSQL 2 h, SQL Server 1 h, and PostgreSQL 30 min on the final code.
- **Result:** every run passed all 9 invariants: atomicity, completeness, poison messages dead-lettered, a full drain, bounded attempts, and every Delivered row reaching the sink.
- **Messages:** 518,026 committed in total, 0 lost and 0 orphaned.
- **Chaos:** 187 hard worker kills, 100 graceful stops and 5,083 terminated database sessions.
- **Duplicates:** 7 in total, which at-least-once delivery allows.
- **Resources:** the long-lived worker's memory, threads and handles stayed flat.

The 2-hour PostgreSQL run used the build from before the BA review fixes; the other two used the final code.

**Mutation checks:** for the BA-02 release token guard (both providers) and the batcher lost-wake-up race, the fix was reverted temporarily, the new test was confirmed failing, and the fix was restored.

**CI and flaky-test fixes:**
- **CI run 45023327704 failed 1,278/1,280.** `Delivered_acknowledgements_are_batched_under_concurrent_load` failed on net8.0 and net9.0 on Linux.
- **A second local flake:** while the suite was being re-run repeatedly, `Claiming_pauses_while_abandoned_handlers_fill_every_slot` failed 2 of about 17 full-suite runs.
- **Cause:** both were timing assumptions in the tests, not product defects.
  - The in-memory store never overlapped acknowledgements.
  - A blocking handler starved the thread pool, and the test settled on a fixed delay.
- **Fix:** both tests were made deterministic, and both still fail on the corresponding mutation (batching off; abandoned-handler limit removed).
- **Result:** the unit suite then passed 10 consecutive full runs (4,160/4,160), and CI run 45025481068 is green at 1,280/1,280 with 0 skipped.

### Run after the 0.2 engineering backlog (2026-11-22)

| Project | Per TFM | Total (4 TFMs) | Failed | Skipped |
|---|---|---|---|---|
| DbRelay.Tests | 107 | **428** | 0 | 0 |
| DbRelay.PostgreSql.Tests | 100 | **400** | 0 | 0 |
| DbRelay.SqlServer.Tests (RCSI off and on) | 122 | **488** | 0 | 0 |
| DbRelay.IntegrationTests | 19 | **76** | 0 | 0 |
| **All** | | **1,392** | **0** | **0** |

`dotnet format --verify-no-changes` was clean, and CI run 45051359799 is green.

**New tests:**
- **T-03:** realistic-table PostgreSQL plan test.
- **K13:** schema-name sweep.
- **CR-21 / CR-18:** paging and payload-budget conformance; enqueue cap.
- **Multi-row insert:** every field of every row.
- **CR-11 / T-05:** null-transaction behaviour on both providers.
- **BA-05:** load test and deterministic hand-off test.
- **BA-07:** SQL Server stress test.

**Mutation checks** (fix reverted temporarily, new test confirmed failing, fix restored):
- **Confirmed:** CR-03 statistics, payload budget on both providers, multi-row field mapping, BA-05 promotion.
- **Inconclusive:** the BA-07 deadlock fixes, because reproduction depends on machine load (see the resolution log in CODE_REVIEW_REPORT.md).

**Soak (final code, 30 min per provider):**
- **Result:** both passed all 9 invariants.
- **Messages:** 139,066 committed, with 0 lost and 0 duplicates.
- **Chaos:** 59 hard kills and 1,345 terminated sessions.