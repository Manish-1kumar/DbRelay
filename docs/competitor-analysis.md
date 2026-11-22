# Competitor Analysis

Date of research: 2026-11-18. Sources: NuGet API/listings, GitHub source/issues/API, official documentation. Every row below links to a source checked on that date. **(inferred)** marks conclusions not directly verified. Download counts are NuGet `totalDownloads` (all versions). The principal architect independently re-checked the claims marked ✔ against source/nuspecs.

Fair-play rule: competitors are described by what their own code and docs show. Many of them do far more than DbRelay intends to.

---

## 1. Framework outboxes

These are the big three, plus Brighter.

### 1.1 Paramore Brighter
- **Version:** 10.8.1 (2026-10-22). Roughly monthly releases; very active (≈2.5k stars, 91 open issues). MIT. [releases](https://github.com/BrighterCommand/Brighter/releases)
- **Targets and size:** net8/9/10 (core also netstandard2.0). Core has 3.77M downloads; `Outbox.PostgreSql` 168K; `Outbox.MsSql` 399K.
- **Dapper write path:**
  - A handler gets `IAmATransactionConnectionProvider`, writes with Dapper on its connection/transaction, calls `DepositPostAsync(request, provider)`, commits through the provider, then calls `ClearOutboxAsync` or lets the sweeper dispatch. [sample](https://github.com/BrighterCommand/Brighter/blob/master/samples/WebAPI/WebAPI_Dapper/GreetingsApp/Handlers/AddGreetingHandlerAsync.cs)
  - You must adopt Brighter's `IRequest` types, message mappers, the producer registry and `CommandProcessor`.
  - Using `OutboxProducerMediator` directly is possible but undocumented, and its constructor needs about 8 collaborators.
- **Claiming:**
  - The outstanding-message query is `SELECT * ... WHERE dispatched IS NULL ... LIMIT/OFFSET`, with **no row-level claim**. ✔ [PostgreSqlQueries.cs](https://github.com/BrighterCommand/Brighter/blob/master/src/Paramore.Brighter.Outbox.PostgreSql/PostgreSqlQueries.cs)
  - Multi-instance safety comes from **one global "OutboxSweeper" distributed lock**. ✔ [TimedOutboxSweeper.cs](https://github.com/BrighterCommand/Brighter/blob/master/src/Paramore.Brighter.Outbox.Hosting/TimedOutboxSweeper.cs)
  - The default lock is in-memory, so without extra configuration every instance sweeps.
- **Failure handling:**
  - Retries come from the producer pipeline: 3 attempts with linear backoff, plus a per-topic circuit breaker.
  - The outbox has **no attempt count, lease, lock token or error column**. ✔ It also has no poison/dead-letter handling. A failure channel is only proposed ([#4725](https://github.com/BrighterCommand/Brighter/issues/4725)).
- **Strengths:**
  - OpenTelemetry, meters and sweeper health checks.
  - Inbox for 8 stores.
  - About 12 transports, including database "messaging gateways".
  - Archiver.
- **Pain points:**
  - #4489: a failed send can look like a success.
  - #4628: circuit-breaker state is per instance.
  - #4678: a bulk add silently lost messages (fixed).
  - #2193: no retention policy.
  - #4133: only one outbox per host.
  - #4228: a V11 schema rework is planned.
- **Dependencies:** core pulls Polly, Newtonsoft.Json, NJsonSchema and OpenTelemetry. The Postgres outbox adds Npgsql, DI extensions and more.

### 1.2 MassTransit
- **Version:** 9.3.0 (2026-11-18). 266M downloads.
- **License:**
  - **v9+ is commercial** (Massient; free for organisations under $1M revenue, and for nonprofits). ✔ [nuspec licenseUrl](https://api.nuget.org/v3-flatcontainer/masstransit/9.3.0/masstransit.nuspec) · [massient.com](https://massient.com/)
  - v8 (8.5.12) remains Apache-2.0, but massient.com states **v8 support ends 2026-12-31**.
- **Outbox:**
  - **EF Core or MongoDB only.** The Dapper integration covers sagas only.
  - Issue [#5982](https://github.com/MassTransit/MassTransit/issues/5982) "Dapper repository is missing Outbox integration" was **closed as not planned**. ✔
- **Mechanics:**
  - Bus outbox (writes through DbContext `SaveChanges`) and consumer outbox (inbox + outbox).
  - Row locks are SKIP LOCKED / UPDLOCK+READPAST inside Serializable transactions, with ordered delivery per outbox.
  - Delivery retry arrived only in 9.2.1. There is **no outbox dead-lettering** ([#6231](https://github.com/MassTransit/MassTransit/issues/6231), open).
- **Strengths:** the most mature .NET messaging framework. Consumer exactly-once via inbox, sagas, many transports, OTel.

### 1.3 Wolverine (WolverineFx)
- **Version:** 6.64.2 (2026-11-16). net9/10. 12.5M downloads. MIT.
- **Commercial add-ons:** paid support and CritterWatch (BSL).
- **Release churn:** v5, v6 and v7-alpha are all active in 2026.
- **ADO.NET path:**
  - `context.EnlistInOutboxAsync(new DatabaseEnvelopeTransaction(db, tx))` works on a caller `DbTransaction`, but it is **undocumented** (found in tests).
  - A bug where the caller's transaction was disposed was only recently fixed.
  - It requires the Wolverine runtime (`UseWolverine()`, generated handlers, endpoint model).
- **Mechanics:** owner ids, leader-assigned durability agents, advisory locks, SKIP LOCKED, stale-time reclaim.
- **Strengths:** rich retry policies with full jitter, a durable dead-letter table with replay, inbox and idempotency, OTel, local/DB queues without a broker.
- **Recent correctness issues:**
  - Double handling: #5107.
  - Message loss: #5276, #5278.

### 1.4 DotNetCore.CAP
- **Version:** 10.0.2 (2026-08-01). net8/9/10. 15.6M downloads. MIT. 7.1k stars.
- **ADO.NET path:**
  - `connection.BeginTransaction(capPublisher)` then `publisher.Publish(...)`. CAP **creates and owns the transaction** through an AsyncLocal, so you can't hand it a transaction you already started (inferred from the API shape).
  - `BeginTransactionAsync` is sync-over-async.
- **Dependency:** **CAP.PostgreSql and CAP.SqlServer depend on `Microsoft.EntityFrameworkCore.Relational`** even for Dapper users. ✔ [nuspec](https://api.nuget.org/v3-flatcontainer/dotnetcore.cap.postgresql/10.0.2/dotnetcore.cap.postgresql.nuspec)
- **A broker transport is required** (RabbitMQ, Kafka, ASB, SQS, NATS, Redis, Pulsar). The in-memory queue is a dev-only community package, and there is no outbox-only mode ([#1606](https://github.com/dotnetcore/CAP/issues/1606)).
- **Retry:**
  - Fixed interval (60s) × 50 attempts, then a callback. No exponential backoff or jitter found.
  - The retry scan has no row lock. Coordination is an opt-in coarse `cap.lock` TTL row.
  - With N replicas, poison messages retry N× as often ([#1797](https://github.com/dotnetcore/CAP/issues/1797), by design).
  - SQL Server cleanup deadlocks: [#1809](https://github.com/dotnetcore/CAP/issues/1809), #1811.
- **Strengths:** free dashboard, inbox (`received`), many brokers, simple publish API, large community.

---

## 2. Standalone and small outbox libraries

| Package | Latest | Downloads | DBs | Caller-owned ADO.NET tx | Claim model | Retry/DLQ | Notes |
|---|---|---|---|---|---|---|---|
| [OutboxKit](https://github.com/yakshavefx/outboxkit) (`YakShaveFx.OutboxKit.*`) | 0.3.3 (2026-07) | Core ~3.1K ✔ | PG, MySQL, Mongo | **N/A: read side only**; you insert rows yourself | Single ordered reader (FOR UPDATE / advisory lock) | You return successes; no backoff/DLQ | Credible toolkit; no SQL Server; net8 |
| [Twinbox](https://github.com/villa313/twinbox) | 1.3.0 (2026-10) | ~2.4K ✔ | PG, MSSQL, MySQL, Oracle, EF, Mongo | Yes | Row lease, SKIP LOCKED / READPAST (README) | Backoff+jitter, breaker, DLQ observer | Closest feature overlap; broad (10 transports, webhooks, dashboard, inbox). First published 2026-09-30 |
| [Relayer](https://github.com/thomhurst/Relayer) | 0.1.2 (**2026-11-15**) | ~1.3–1.6K ✔ | PG, MSSQL, MySQL | Yes (ADO.NET or EF) | "Fenced claims" ✔ | Retries, DLQ | Broker-client wrapper framework from a well-known OSS author; 3 days old |
| [Gizza.MessageBroker.Persistence](https://github.com/burakoner/Gizza.MessageBroker) | 2.0.3 (2026-08) | ~840 ✔ | PG, MSSQL, SQLite, Oracle | **No**: `SaveAsync` takes no transaction; opens its own connection | Lease columns + SKIP LOCKED; completion filtered by id only (no token fencing, inferred from SQL) | attempt_count, retry delay | Tied to Gizza broker abstraction |
| [Rebus outbox](https://github.com/rebus-org/Rebus.SqlServer) | MSSQL 8.4.2 (2026-10); PG 9.1.1 (2024-05) | (whole packages) | MSSQL, PG | Yes, `scope.UseOutbox(conn, tx)` | UPDLOCK, READPAST | Rebus transport retries/error queue | Requires Rebus bus and transport; PG outbox has open bugs ([#55](https://github.com/rebus-org/Rebus.PostgreSql/issues/55)) |
| [SimpleOutbox](https://github.com/alexandrereyes/SimpleOutbox) | 2.0.0 (2026-01) | ~1.9K | PG | **EF Core only** | SKIP LOCKED, partition FIFO | Backoff, max retries | Dormant since Jan 2026 |
| [TinyOutbox](https://github.com/mustafaa428/TinyOutbox) | 1.0.3 (2026-09) | ~530 | PG (Dapper), others via EF | Yes | SKIP LOCKED / optimistic lease | Backoff, max retries, inbox | RabbitMQ transport |
| [Relay.Outbox](https://github.com/thatjimmi/relay) | 0.0.2 (2026-03) | 178 ✔ | MSSQL | Yes (raw ADO.NET) | SKIP LOCKED-style | Retry + DLQ | Name proximity |
| [SqlTransactionalOutbox](https://github.com/cajuncoding/SqlTransactionalOutbox) | 2.0.1 (2026-03) | ~11.7K | MSSQL | Yes | Single processor via app lock (FIFO) | Max attempts | Azure Functions / ASB focus |
| [Yautbox](https://github.com/maximiliysiss/yautbox) | 1.4.0 (2026-08) | ~1.4K | PG | Own provider | Advisory locks, sequential | Handler Retry() | Broker-free typed handlers |

The research agent also confirmed these exist but they're small or product-specific: ZeroAlloc.Outbox, LiteBus.Outbox, NQueue, LinKit.Outbox.Data, Sa.Outbox.PostgreSql, OutboxCore.Dapper, Outboxer (inactive), Rask.Outbox (framework-bound), NServiceBus outbox (commercial). These names do **not** exist on NuGet: Dapper.Outbox, Outbox.Dapper, ElasticOutbox, Tharga.Outbox.

---

## 3. Comparison against DbRelay's target

| Capability | Brighter | MassTransit | Wolverine | CAP | Small libs (best of) | **DbRelay v0.1 (planned)** |
|---|---|---|---|---|---|---|
| Enqueue on a transaction *you* already began (Dapper) | via provider abstraction | ✘ (EF/Mongo) | undocumented | ✘ (CAP owns tx) | Twinbox, TinyOutbox, Relayer | ✔ `EnqueueAsync(tx, msg)` |
| No framework/runtime adoption | ✘ | ✘ | ✘ | ✘ | OutboxKit, Twinbox (partly) | ✔ |
| No broker required | DB gateway | SQL transport | local queues | ✘ | some | ✔ handler is any code |
| Per-row claim that scales across instances | ✘ (global lock) | ordered, per-outbox | ✔ | partial | several | ✔ SKIP LOCKED / READPAST |
| Lock-token fencing on completion | ✘ | n/a (tx-held locks) | owner ids | ✘ | Relayer ✔, Gizza ✘ | ✔ |
| Crash-loop bound (attempts counted at claim) | ✘ | ✘ | (inferred partial) | ✘ | not verified | ✔ |
| Exponential backoff + jitter | linear | 9.2.1+ | ✔ full jitter | fixed | Twinbox ✔ | ✔ equal jitter |
| Outbox dead-letter + requeue | ✘ | ✘ (#6231) | ✔ | failed state + dashboard | some | ✔ |
| EF Core dependency | no | **yes** | optional | **yes** | varies | **never** |
| Inbox / exactly-once consumers | ✔ | ✔ | ✔ | partial | some | ✘ (roadmap A) |
| Broker integrations | ~12 | many | many | 7+ | varies | ✘ (roadmap C) |
| Dashboard | ✘ | ✘ | paid | ✔ free | Twinbox | ✘ (roadmap D) |
| Maturity / adoption | high | very high | high | high | very low | none yet |

The basic claim SQL is **not** a differentiator: MassTransit, Wolverine, CAP and most small libraries already use SKIP LOCKED / READPAST.

---

## 4. Naming: "DbRelay"

**Verified free:**
- NuGet has no `DbRelay`, `Db.Relay`, `DbRelay.Core`, `DbRelay.PostgreSql`, `DbRelay.SqlServer` or `DbRelay.Abstractions` package. ✔ The flat-container API returns 404 for each.
- npm, PyPI and crates.io are free (research agent).

**Prior use:**
- [github.com/dbrelay](https://github.com/dbrelay) (a user account) hosts "DB Relay", a GPL NGINX HTTP-to-database gateway launched at OSCON 2010. Its last code push was 2012.
- dbrelay.com is now a parked domain offered for sale.

**Confusion risk:**
- "Relay" is overloaded in .NET: GraphQL Relay, Azure Relay and `Relay.Core`.
- Two outbox packages already use the word: `Relay.Outbox` (tiny) and `Relayer.Outbox.*`, published 2026-11-15 by a well-known author.

**Trademark:**
- A best-effort web search found no DB RELAY / DBRELAY mark. We could not query USPTO directly from this environment.
- **No legal clearance has been performed.** Check USPTO, EUIPO and WIPO manually (classes 9 and 42) before 1.0.

**Assessment:** legal risk low (inferred); discoverability and confusion risk medium.

**Alternatives verified free on NuGet:** `LeaseOutbox`, `DbOutbox`, `CommitRelay`.

**Recommendation:**
- Keep **DbRelay**. It's descriptive ("database" + the well-known "outbox relay" role), short, and its ID is free.
- Reserve the `DbRelay.*` ID prefix on NuGet before the first publish.
- Decide on the name now, before any package is published.
