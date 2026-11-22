# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Before 1.0, minor versions may contain breaking changes.

## [Unreleased]

## [0.1.0] - 2026-11-23

First release.

### Added

- `DbRelay.Abstractions`: `IOutbox`, `OutboxExtensions`, `EnqueueOptions`, `RawOutboxMessage`, `OutboxMessage`,
  `IMessageDeliveryHandler`, `DeliveryResult`, `IMessageSerializer`, `OutboxMessageTypeAttribute`, `IOutboxAdmin`,
  `IOutboxSchemaManager` and the query and statistics models. It has no dependencies.
- `DbRelay` engine:
  - `AddDbRelay` registration with startup-validated options (`OutboxOptions`, `DispatcherOptions`, `RetryOptions`,
    `MaintenanceOptions`);
  - a System.Text.Json serializer and logical message type names;
  - a background dispatcher with batched claims, database-clock leases, lock-token-guarded outcomes, bounded concurrency and
    graceful shutdown;
  - exponential backoff with equal jitter, handler-supplied `Retry-After`, permanent-failure classification, dead-lettering
    and requeue;
  - retention cleanup of delivered messages;
  - the `DbRelay` `ActivitySource` and `Meter`, W3C trace-context propagation from enqueue to delivery, and a health check
    (`AddHealthChecks().AddDbRelay()`).
- `DbRelay.PostgreSql`: `UsePostgreSql` with a connection string, an `NpgsqlDataSource` or a data source factory. Claims use
  `FOR UPDATE SKIP LOCKED`. Payloads are stored as `bytea`. Schema creation is explicit and idempotent, and the script is
  embedded.
- `DbRelay.SqlServer` (source only; **the NuGet package ships in 0.2.0**, following the package strategy): `UseSqlServer` with
  a connection string or a connection factory. Claims are two-phase (`UPDLOCK, READPAST, ROWLOCK`; see ADR 0002) and work with
  and without `READ_COMMITTED_SNAPSHOT`. Schema creation is explicit and idempotent, and the script is embedded. It passes the
  same conformance suite as PostgreSQL on SQL Server 2022.
- Samples: PostgreSQL minimal API, SQL Server worker, and an HTTP webhook delivery handler.
- Documentation: getting started, configuration, architecture, PostgreSQL and SQL Server guides, reliability contract with
  test traceability, troubleshooting, migrating from a hand-rolled outbox, and benchmarks.
- **Batched acknowledgements** (ADR 0003). "Delivered" outcomes and shutdown releases are written in group-commit batches when
  the storage provider supports it; both built-in providers do. Batching adds no latency: a lone acknowledgement is written
  immediately with the single-row statement. Every row is still guarded by its own lock token. It is on by default and can be
  turned off with `DispatcherOptions.BatchAcknowledgements`. Measured gains are in `docs/benchmarks.md`.
- **New storage SPI capability `IOutboxBatchStore`** with `OutboxAcknowledgement`. This is an optional interface, so existing
  `IOutboxStore` implementations keep working unchanged.
- **New metric `dbrelay.ack.batch_size`.**
- **Soak/chaos harness `tests/DbRelay.Soak`.** Producers with rollbacks, worker-process kills and graceful stops, database session
  kills, unreliable handlers, and invariant verification after a drain.

### Behaviour and limits (developed after the release candidate)

- `DbRelay.PostgreSql` depends on Npgsql 10.0.4 or later.
- **Payload budgets** (CR-18, ADR 0004).
  - `EnqueueManyAsync` rejects a call whose serialized payloads total more than 64 MiB, or more than `MaxPayloadBytes` if that
    is larger.
  - A claim stops adding messages once their payloads reach 64 MiB; the first message is always claimed. New
    `OutboxLimits.BatchPayloadBudgetBytes` and init-only `ClaimRequest.MaxPayloadBytes`; custom stores keep compiling, and
    the conformance suite checks the rule.
- PostgreSQL `EnqueueManyAsync` writes all messages with one `INSERT ... SELECT FROM unnest(...)` statement instead of one
  command per row (about 0.4 ms less per 10 messages locally; `docs/benchmarks.md`).
- **`OutboxQuery.Skip` is limited to 10,000** (CR-21); larger values throw `ArgumentOutOfRangeException`. Narrow the query
  with filters instead of paging deeper.
- PostgreSQL admin queries sort only `(created_at, id)` and then fetch the page's rows, as the SQL Server provider does. On a
  1M-row table, a page at `Skip = 10,000` went from 1.6 s with about 400 MB spilled to disk to 0.5 s in memory.
- **Batch leadership is bounded** (BA-05). A leader writes at most 4 round trips, then hands off to the oldest waiting follower
  instead of holding its delivery slot for a whole burst.
- **SQL Server:** passing `transaction: null` while the connection has a transaction in progress now fails with a message that
  says to pass that transaction (CR-11). The provider-specific behaviour itself is unchanged and documented: PostgreSQL joins
  the transaction, SQL Server rejects the call.

### Fixed before release (found by the QA and independent review passes)

- A stored row that DbRelay can't read no longer stalls its whole claim batch. Header parsing never throws, including on invalid UTF-16, and any row a provider can't map is dead-lettered as an `UnreadableRow` placeholder without calling the handler (CR-01, FU-01).
- Abandoned handlers are bounded: delivery pauses (health Degraded, gauge `dbrelay.delivery.abandoned.active`) while too many are still running (FU-02).
- A delivery interrupted by the host shutdown timeout keeps its attempt, so attempts stay bounded across forced restarts (CR-06). Shutdown is prompt even when the database blocks the claim (FU-04).
- Failure reasons containing NUL characters or split surrogate pairs are sanitised, and an outcome whose reason still cannot be
  stored is recorded with a fixed fallback reason, so a poison message is not redelivered every lease (BUG-1).
- Delivery handlers that ignore their cancellation token are abandoned after `DeliveryTimeout` plus a short grace period
  instead of holding a concurrency slot forever (CR-05).
- Schema and table names with a trailing newline are rejected (CR-02).
- The PostgreSQL statistics query uses the partial indexes instead of a full table scan, and statistics are only refreshed on
  instances that run the dispatcher (CR-03). Cleanup catches up immediately when it falls behind (CR-04).
- **SQL Server deadlocks found by a new stress test** (BA-07).
  - Two batched acknowledgements for overlapping messages, for example a stale and a current claim, could lock the id index
    in opposite orders. Items are now sent in SQL Server's `uniqueidentifier` order. PostgreSQL also uses a canonical order,
    as defence in depth.
  - The claim's payload-budget read could deadlock with a concurrent acknowledgement under READ COMMITTED. It now uses
    `READPAST`, like the rest of the claim. This regression was introduced and fixed within this release cycle.
  - Deadlock victims were always retried, but all three attempts could fail.
- The abandoned-handler count, used by the health check and the gauge, could be left permanently stale by a race. It is now a
  single atomic counter.

### Known limitations

See [KNOWN_ISSUES.md](KNOWN_ISSUES.md) and the Limitations section of the [README](README.md#limitations).

[Unreleased]: https://github.com/Manish-1kumar/DbRelay/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Manish-1kumar/DbRelay/releases/tag/v0.1.0
