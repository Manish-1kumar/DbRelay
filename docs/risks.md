# DbRelay Risk Register

Date: 2026-11-18 · Owner: Principal Architect · Review at the end of each milestone.

Severity = impact × likelihood (H/M/L).

## Product risks

| ID | Risk | Sev | Mitigation |
|---|---|---|---|
| P1 | **Weak differentiation.** Brighter, CAP, Wolverine and standalone toolkits already offer outboxes. DbRelay could be "yet another outbox". | H | Compete on a narrow, verifiable promise rather than breadth: no framework adoption, a per-row lease claim that scales out, first-class poison handling, a tiny dependency graph and a tested crash story. See `product-differentiation.md`. Do not chase broker or inbox breadth in v0.x. |
| P2 | **Maintenance sustainability.** Outbox libraries live for years. One maintainer is a risk to adopters. | M | Small surface area, a provider conformance test suite, clear CONTRIBUTING, and ADRs that explain *why*. |
| P3 | **Name collision.** An inactive "DB Relay" product (NGINX HTTP-to-database gateway, github.com/dbrelay) exists, and "Relay" is a common software word. No trademark clearance has been performed. | M | NuGet ID is free (verified 2026-11-18). Get a professional trademark search before 1.0 if commercial use is planned. Alternative names are kept in `competitor-analysis.md`. |
| P4 | **Scope creep** (inbox, brokers, dashboard) before the core is trustworthy. | M | The roadmap gates each item on core stability. Broker packages ship only when independently tested. |

## Technical risks

| ID | Risk | Sev | Mitigation |
|---|---|---|---|
| T1 | **Duplicate delivery** when a worker delivers, then crashes or loses the DB before acknowledging. | Inherent | Documented at-least-once guarantee. Stable `Id` for consumer idempotency. Lease checks before delivery reduce, but cannot remove, duplicates. |
| T2 | **Lease expires during a slow delivery.** Another worker redelivers, and the first worker's ack must not overwrite the second's state. | H | Lock-token-guarded updates. `DeliveryTimeout` is shorter than `LeaseDuration` (enforced by validation). A local deadline check runs before each delivery, and a `dbrelay.lease.lost` metric is emitted. Concurrency tests cover stale tokens. |
| T3 | **Crash/poison loops.** A message that kills the process is retried forever. | H | Attempts are counted at claim time. A claim with `attempt_count >= MaxAttempts` goes to dead-letter without being delivered. A process-kill integration test covers this. |
| T4 | **Clock skew** between app servers breaks lease and schedule comparisons. | M | Only the database clock (`now()` / `SYSUTCDATETIME()`) is used for availability and leases. The app computes durations only. |
| T5 | **SQL Server claim semantics** (READPAST with RCSI, lock escalation, filtered-index matching, parameter sniffing). | H | Provider-specific design. Batch cap of 1000. Literal status predicates. Plans are verified. Tests run with RCSI on and off. |
| T6 | **PostgreSQL table bloat and vacuum pressure** from frequent updates to an indexed column. | M | Partial indexes. Retention cleanup in bounded batches. Autovacuum tuning (fillfactor, per-table autovacuum settings) documented in `postgres.md`. Benchmark long-running churn. |
| T7 | **Caller misuse of transactions** (completed transaction, wrong connection, a provider type that doesn't match). | M | Eager validation with actionable exceptions. Tests for every misuse path. |
| T8 | **Database outage** crashes or wedges the hosted service, or causes log storms. | M | Catch at the loop boundary, back off exponentially, rate-limit logs, report Degraded health, and run an interruption integration test. |
| T9 | **Graceful shutdown** leaves leases held, delaying redelivery by up to `LeaseDuration`. | L | Release unfinished leases on stop (token-guarded). Worst case is redelivery after the lease expires. |
| T10 | **Ordering expectations.** Users assume FIFO. | M | State clearly that there is no ordering guarantee. Per-aggregate ordering is on the roadmap. |
| T11 | **Hot-path performance** (one round trip per acknowledgement). | L | Measure in M10 first. Batch acknowledgements only if the data justifies it. |
| T12 | **Infrastructure for tests.** Docker isn't running locally and the PostgreSQL credentials are unknown. | M | Testcontainers when Docker runs; connection strings in `DBRELAY_TEST_*` otherwise. CI on GitHub-hosted Linux runners with Docker. Skipped tests are reported as skipped. |
| T13 | **Dependency drift.** Microsoft.Extensions 11.x and Npgsql majors force upgrades onto consumers. | L | Lowest supported versions (10.0.x) as lower bounds. Dependabot. Package validation baseline after 0.1. |

## Security risks

| ID | Risk | Sev | Mitigation |
|---|---|---|---|
| S1 | SQL injection via configurable schema/table names. | H | Strict identifier regex + quoting; everything else is parameterized. Tests feed hostile identifiers. |
| S2 | Unsafe deserialization / type activation from a `message_type` string. | H | Never resolve types from DB strings. Typed reads require an explicit `T`. System.Text.Json only (no polymorphic `$type` handling by default). |
| S3 | Sensitive data in logs or `last_error`. | M | No payload logging. Exception text is truncated and stored without stack traces. This is documented so handler authors avoid secrets in exception messages. |
| S4 | Resource exhaustion (huge payloads, unbounded batches or concurrency). | M | `MaxPayloadBytes`, plus upper bounds on `BatchSize` and `MaxConcurrency`, enforced by options validation. |
| S5 | Vulnerable transitive dependencies. | M | `dotnet list package --vulnerable --include-transitive` in CI, plus Dependabot. |
