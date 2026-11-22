# Reliability contract

This page lists every guarantee DbRelay makes, every failure mode it handles, and the tests that prove each one. If a
guarantee is not on this page, do not rely on it.

Test locations:

| Short name | Project | Database |
|---|---|---|
| **Conformance** | `tests/DbRelay.Storage.Conformance/OutboxStoreConformanceTests.cs`, run by `PostgreSqlConformanceTests`, `SqlServerConformanceTests` (RCSI off) and `SqlServerRcsiConformanceTests` (RCSI on) | real PostgreSQL and SQL Server |
| **Integration** | `tests/DbRelay.IntegrationTests` (real hosts, real child processes) | real PostgreSQL only |
| **PG provider** | `tests/DbRelay.PostgreSql.Tests/PostgreSqlProviderTests.cs` | real PostgreSQL |
| **SQL provider** | `tests/DbRelay.SqlServer.Tests/SqlServerProviderTests.cs` | real SQL Server |
| **Unit** | `tests/DbRelay.Tests` (`CoreTests.cs`, `DispatcherTests.cs`; in-memory store) | none |

Test results for each run are recorded in [TEST_REPORT.md](../TEST_REPORT.md). Database tests that cannot reach a database are
reported as **skipped**, never as passed.

## Delivery semantics

DbRelay delivers **at least once**. A committed message is handed to your handler one or more times, until the handler's
outcome is recorded as Delivered or the message is dead-lettered. **Duplicates are expected** in these situations:

1. The handler succeeded, but the worker crashed before recording success. The message is redelivered after its lease.
2. The handler succeeded, but the database was unreachable while the outcome was recorded (3 tries over about 0.6 s). The
   message is redelivered after its lease (log event 1009).
3. The handler ran longer than the lease (it ignored its cancellation token). Another worker redelivered the message, and the
   slow worker's outcome was rejected (event 1007, `dbrelay.lease.lost`).
4. The host's shutdown timeout cancelled an in-flight handler. The message is returned to Pending (the attempt stays counted) and delivered again later.

Consumers must be idempotent. `OutboxMessage.Id` is the same on every attempt; use it as the deduplication key.

There is **no ordering guarantee**. Claims are roughly ordered by due time, but concurrency within a process, several
processes, and retries all reorder messages.

## Guarantees and the tests that prove them

### Atomicity with your transaction

| Guarantee | Tests |
|---|---|
| The outbox row commits together with business data written by Dapper in the same transaction | Conformance `Enqueue_commits_together_with_business_data_written_by_Dapper` |
| Rolling back the transaction removes the message | Conformance `Enqueue_is_rolled_back_with_business_data` |
| An uncommitted message is never claimed | Conformance `Enqueue_is_invisible_to_claimers_until_commit` |
| Over 200 concurrent transactions ending in commit, rollback, dispose-without-commit or a business failure after the enqueue, the delivered set equals the committed set exactly, with a dispatcher polling throughout | Integration `TransactionAtomicityTests.Only_committed_business_transactions_are_ever_delivered` |
| A batch enqueue is all-or-nothing | Conformance `EnqueueMany_inserts_all_messages_atomically`; SQL provider `Multi_statement_insert_without_caller_transaction_is_all_or_nothing`, `EnqueueMany_with_1000_messages_is_chunked_below_the_2100_parameter_limit` |
| A duplicate message ID fails, and the caller can still roll back | Conformance `Enqueue_with_duplicate_message_id_fails_and_caller_can_roll_back` |

### Resource ownership (invariants 1 and 2)

| Guarantee | Tests |
|---|---|
| DbRelay never commits, rolls back, closes or disposes the caller's connection or transaction | Conformance `Enqueue_never_commits_closes_or_disposes_caller_resources`; Unit `OutboxValidationTests.Enqueue_never_completes_or_disposes_caller_resources`; Integration `Only_committed_business_transactions_are_ever_delivered` (the connection is still open and usable after every transaction) |
| A completed transaction is rejected | Conformance `Enqueue_rejects_completed_transaction` |
| A transaction from another connection is rejected | Conformance `Enqueue_rejects_transaction_from_another_connection` |
| A closed connection is rejected | Conformance `Enqueue_rejects_closed_connection` |
| A connection or transaction of another provider is rejected | Conformance `Enqueue_rejects_connection_of_another_provider`; PG provider `Insert_rejects_a_non_npgsql_transaction_type`; Unit `OutboxValidationTests.Rejects_invalid_caller_resources` |
| `transaction: null` auto-commits on the caller's connection | Conformance `Enqueue_without_transaction_autocommits_on_caller_connection` |
| A data source you supply is never disposed; one DbRelay built is disposed with the container | PG provider `Caller_owned_data_source_is_not_disposed_with_the_service_provider`, `Owned_data_source_is_disposed_with_the_service_provider` |
| Factory connections are opened by DbRelay; already-open ones are refused | SQL provider `Connection_factory_connections_are_opened_by_DbRelay_and_open_ones_are_refused` |

### No transaction during delivery (invariant 3)

| Guarantee | Tests |
|---|---|
| While handlers run, the dispatcher's sessions have no open transaction and hold no relation, tuple or transaction-ID locks | Integration `GracefulShutdownTests.StopAsync_finishes_in_flight_deliveries_and_releases_the_rest_with_attempts_restored` |
| Each delivery gets its own DI scope, disposed afterwards | Unit `DispatcherTests.Each_delivery_gets_its_own_scope` |

### Exclusive claims and fencing (invariant 4)

| Guarantee | Tests |
|---|---|
| Concurrent claimers never receive the same message | Conformance `Concurrent_claimers_never_receive_the_same_message` |
| A message under a valid lease is not claimed | Conformance `Claim_does_not_return_messages_under_a_valid_lease` |
| SQL Server: a claim skips rows locked by an in-flight claim without blocking, with RCSI off and on | SQL provider `Claim_skips_rows_locked_by_an_in_flight_claim_without_blocking` |
| Four hosts sharing 2,000 messages: every message delivered, no duplicates, no lost leases | Integration `MultiWorkerTests.Four_hosts_deliver_2000_messages_with_no_duplicates_in_a_crash_free_run` (this measures a crash-free run; it does **not** mean delivery is exactly-once in general) |
| An update with a wrong lock token changes nothing | Conformance `Transitions_with_a_wrong_token_change_nothing`, `Transitions_on_unknown_message_return_false` |
| A stale token cannot mark delivered, schedule a retry, dead-letter or release, either while the new owner holds the lease or after it delivered | Integration `StaleWorkerTests.Stale_lock_token_cannot_overwrite_while_the_new_owner_delivers_or_after_it_delivered` |
| A handler that ignores its timeout is abandoned (event 1013) inside its lease; its late result is never written, and the message is redelivered once | Integration `StaleWorkerTests.Handler_that_ignores_its_timeout_is_abandoned_and_its_late_result_never_overwrites_the_redelivery`; Unit `DispatcherTests.Lost_leases_are_detected_and_never_overwrite_the_new_owner` |

### Crash recovery and bounded attempts (invariants 5 and 10)

| Guarantee | Tests |
|---|---|
| A message whose worker process is killed mid-delivery stays Processing, is redelivered once the lease expires on the database clock (attempt 2) and ends Delivered | Integration `CrashRecoveryTests.Killed_worker_process_message_is_redelivered_after_lease_expiry` |
| A message that kills its worker every time ends DeadLettered after `MaxAttempts`, and the final claim does not call the handler | Integration `CrashRecoveryTests.Message_that_kills_its_worker_every_time_ends_dead_lettered_after_MaxAttempts`; Unit `DispatcherTests.Crash_looping_messages_are_dead_lettered_without_delivery` |
| Expired leases are reclaimed; claims count the attempt | Conformance `Claim_reclaims_messages_whose_lease_expired`, `Claim_marks_messages_processing_and_counts_the_attempt` |
| Scheduled messages are not claimed early | Conformance `Claim_skips_messages_scheduled_in_the_future` |
| Retry delays are applied by the database clock, inside `[d/2, d]`, and the next claim never happens before `available_at` | Integration `RetryScheduleTests.Fails_twice_then_succeeds_with_gaps_inside_the_jitter_bounds` |
| Delivered and dead-lettered messages are never claimed again | Conformance `Claim_never_returns_delivered_or_dead_lettered_messages` |

### Retry, timeout and dead-letter behaviour

| Guarantee | Tests |
|---|---|
| Transient failures retry with backoff, then dead-letter at `MaxAttempts` | Unit `DispatcherTests.Transient_failures_are_retried_with_backoff_then_dead_lettered`; Integration `RetryScheduleTests.Exhausted_message_is_dead_lettered_and_manual_requeue_redelivers_it_from_attempt_one` |
| Exceptions are transient by default and can succeed later | Unit `DispatcherTests.Exceptions_are_transient_and_succeed_on_a_later_attempt` |
| `DeadLetter` results and permanent exceptions skip retries | Unit `DispatcherTests.Dead_letter_result_and_permanent_exceptions_skip_retries` |
| A failure classifier that throws is treated as transient | Unit `DispatcherTests.A_throwing_failure_classifier_is_treated_as_transient` |
| A handler's `RetryAfter` overrides the backoff and is capped at `MaxDelay` | Unit `DispatcherTests.Handler_retry_after_overrides_backoff_and_is_capped` |
| A handler returning `null` counts as a failure | Unit `DispatcherTests.Null_results_are_treated_as_failures` |
| Deliveries over `DeliveryTimeout` are cancelled and retried | Unit `DispatcherTests.Deliveries_exceeding_the_timeout_are_cancelled_and_retried` |
| Backoff math: exponential growth, equal-jitter bounds, cap, no overflow | Unit `RetryPolicyTests.Delay_grows_exponentially_with_equal_jitter_bounds`, `Delay_is_capped_at_max_delay_and_never_overflows`, `Real_random_jitter_spreads_retries_within_bounds` |
| Concurrency never exceeds `MaxConcurrency` | Unit `DispatcherTests.Concurrency_never_exceeds_the_configured_maximum` |
| Requeue affects only dead-lettered messages and resets attempts; bulk requeue honours type and count | Conformance `Requeue_only_affects_dead_lettered_messages_and_resets_attempts`, `RequeueDeadLettered_respects_type_filter_and_max_count` |
| Release returns a message without counting the attempt | Conformance `Release_returns_message_without_counting_the_attempt` |

### Shutdown

| Guarantee | Tests |
|---|---|
| `StopAsync` lets in-flight deliveries finish and record Delivered; unstarted claims are released to Pending with attempts restored; nothing stays Processing | Integration `GracefulShutdownTests.StopAsync_finishes_in_flight_deliveries_and_releases_the_rest_with_attempts_restored`; Unit `DispatcherTests.Graceful_shutdown_lets_in_flight_deliveries_finish_and_releases_unstarted_messages` |
| When the shutdown timeout expires, in-flight handlers are cancelled and returned with the attempt counted; unstarted claims are released with it given back | Integration `GracefulShutdownTests.Shutdown_timeout_cancels_in_flight_deliveries_and_returns_every_claim`; Unit `DispatcherTests.Shutdown_timeout_cancels_in_flight_deliveries_and_returns_them_with_the_attempt_counted` |
| A handler that always outlasts the shutdown window still reaches dead-letter (attempts are bounded across forced restarts) | Unit `DispatcherTests.Handlers_that_always_outlast_shutdown_still_reach_dead_letter` |
| A handler that ignores its cancellation token cannot hold a concurrency slot forever | Unit `DispatcherTests.Handlers_that_ignore_cancellation_are_abandoned_and_do_not_hold_slots` |
| One stored row with headers DbRelay did not write cannot stall a claim batch | Conformance `OutboxStoreConformanceTests.Claim_tolerates_rows_with_headers_DbRelay_did_not_write` (PostgreSQL and SQL Server, RCSI off and on) |
| An acknowledgement that committed but timed out is not misreported as a lost lease | Unit `DispatcherTests.An_acknowledgement_that_committed_but_timed_out_is_not_reported_as_a_lost_lease` |
| ...and an ambiguous acknowledgement after a reclaim is still reported as a lost lease (the check matches the claim, not just the worker) | Unit `DispatcherTests.Ambiguous_acknowledgement_after_a_reclaim_is_still_reported_as_a_lost_lease` |
| Headers that are invalid JSON or invalid UTF-16 (accepted by SQL Server `nvarchar`) cannot stall a claim; a row a provider cannot map is dead-lettered without calling the handler | SQL Server `SqlServerConformanceTestsBase.Claim_tolerates_headers_that_are_invalid_json_or_invalid_utf16` (RCSI off and on; mutation-checked); Unit `DispatcherTests.Unreadable_rows_are_dead_lettered_without_calling_the_handler_and_do_not_block_the_batch` |
| Abandoned handlers are bounded: delivery pauses and health reports Degraded while too many are still running | Unit `DispatcherTests.Claiming_pauses_while_abandoned_handlers_fill_every_slot` |
| A real dispatcher whose lease was taken over cannot overwrite the new owner (event 1007) | Integration `StaleWorkerTests.Real_dispatcher_whose_lease_was_taken_over_cannot_overwrite_the_new_owner` |
| Shutdown is prompt even when the database blocks the claim | Integration `GracefulShutdownTests.Shutdown_is_prompt_even_when_the_claim_is_blocked_by_the_database` (mutation-checked) |
| Messages without enough lease left to finish are released instead of delivered | Unit `DispatcherTests.Messages_with_too_little_lease_left_are_released_instead_of_delivered` |

### Database outages

| Guarantee | Tests |
|---|---|
| With the dispatcher's own connections repeatedly terminated, the host keeps running, health goes non-Healthy and recovers, and every message is delivered | Integration `DatabaseInterruptionTests.Dispatcher_survives_terminated_connections_and_delivers_everything` |
| Claim failures never stop the dispatcher; health is Degraded during the outage and Healthy afterwards | Unit `DispatcherTests.Database_outages_do_not_stop_the_dispatcher` |
| Outcome writes are retried on transient database errors | Unit `DispatcherTests.Outcome_updates_are_retried_when_the_database_blips` |
| An outcome whose reason the database rejects is still recorded, with a fixed fallback reason | Unit `DispatcherTests.An_outcome_whose_reason_cannot_be_stored_is_recorded_with_a_fallback_reason`; Integration `SecurityTests.Error_text_that_postgresql_cannot_store_still_records_the_outcome` |
| A busy dispatcher (all slots occupied, not polling) is not reported as stalled | Unit `DispatcherStateTests.Finished_deliveries_count_as_progress_while_no_polls_happen` |

### Payloads, schema and security

| Guarantee | Tests |
|---|---|
| Raw payload bytes are stored and delivered unchanged | Conformance `EnqueueRaw_preserves_payload_bytes_exactly`; Unit `OutboxValidationTests.Raw_payloads_are_stored_unchanged` |
| All metadata round-trips | Conformance `Enqueue_stores_all_metadata` |
| W3C trace context is captured without overriding user headers | Unit `OutboxValidationTests.Captures_w3c_trace_context_without_overriding_user_headers` |
| Payloads and connection strings never appear in logs while delivering, retrying and dead-lettering | Integration `SecurityTests.Payloads_and_connection_strings_never_appear_in_logs_while_delivering_retrying_and_dead_lettering` |
| A malformed connection string is rejected without being echoed | PG provider `Malformed_connection_string_is_rejected_without_echoing_it` |
| Stored error text is truncated to 2,000 characters, has no stack trace, and is always storable | Integration `SecurityTests.Long_error_text_is_truncated_to_2000_characters_in_the_database`, `Long_retry_reason_is_truncated_while_the_message_waits_for_its_retry`; Unit `SerializationTests.Error_text_is_truncated_without_stack_traces`, `Error_text_is_always_storable` |
| Hostile schema and table names are rejected (SQL injection through configuration) | Unit `SqlIdentifierTests.Rejects_unsafe_identifiers`; PG provider `Hostile_schema_names_are_rejected_at_registration`, `Hostile_table_names_are_rejected_at_registration`, `Hostile_names_are_rejected_for_every_overload`; SQL provider `Hostile_or_invalid_identifiers_are_rejected_when_registering` |
| Schema creation is idempotent, safe to run concurrently, and never destructive | Conformance `Schema_EnsureCreated_is_idempotent`; PG provider `EnsureCreated_is_safe_when_run_concurrently`; SQL provider `EnsureCreated_is_safe_under_concurrent_execution`, `Create_script_quotes_configured_names_and_is_never_destructive` |
| Claims use the claim index | PG provider `Claim_query_can_use_the_partial_claim_index`; SQL provider `Claim_cleanup_requeue_and_statistics_plans_use_the_filtered_indexes` |
| Invalid options fail host startup | Unit `OptionsValidationTests.Invalid_options_fail_host_startup`, `Lease_must_be_at_least_twice_the_delivery_timeout` |
| Retention deletes only delivered messages older than the retention | Conformance `DeleteDelivered_respects_retention_and_status` |

## Guarantees enforced by design, without a dedicated test

Be precise about what is proven and what is only structural:

- **No CLR type is created from a database string (invariant 7).** No API accepts a type name for deserialization: handlers
  call `IMessageSerializer.Deserialize<T>` with a type they choose. No test can prove a negative here; the protection comes from
  the API shape.
- **All SQL is parameterized (invariant 8).** Every provider statement is a fixed string with parameters. Identifiers are
  covered by the hostile-name tests above.
- **The database clock is used for every lease and schedule comparison (invariant 5).** The lease-expiry and retry-schedule
  tests measure on the database clock, but no test runs with a deliberately skewed application clock.

## Known gaps in the evidence

- The integration suite (crash, multi-worker, interruption, shutdown, security) runs against **PostgreSQL only**. SQL Server is
  covered by the conformance and provider suites.
- Only PostgreSQL 17.5 and SQL Server 2022 Express on Windows have been tested. Process-kill semantics were verified with Windows
  `TerminateProcess`, not Linux `SIGKILL`.
- The Testcontainers path has not run on the development machine (Docker was not running). It is expected to run in CI.
- One all-framework SQL Server run on 2026-11-20 had 5 unidentified failures under heavy machine overload; later runs were clean
  (K6). A concurrency bug is not ruled out until it is re-checked under deliberate load.
- In theory, a shutdown that cancels an in-flight claim statement could commit a claim the client never reads. Those rows wait
  for one lease. This has not been reproduced.

## Operational consequences

- **Idempotent consumers are mandatory.** Deduplicate on `OutboxMessage.Id`.
- **Handlers must honour their cancellation token.** A handler that outlives `LeaseDuration` creates duplicates.
- **Keep secrets out of error text.** Exception messages and reasons are stored in `last_error` and logged (K11). For example,
  `FormatException` messages can contain the offending input value.
- **Dead letters need an owner.** They are kept until someone requeues them; alert on `dbrelay.messages.dead_lettered` or the
  health check's `MaxDeadLettered`.
