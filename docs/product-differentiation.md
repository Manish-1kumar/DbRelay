# Product Differentiation

Date: 2026-11-18 · Based on `competitor-analysis.md`.

## 1. Honest assessment of the original pitch

The brief proposed differentiating on "lightweight, Dapper-friendly, transport-independent". The research shows that pitch alone is **too weak**:

- **"Supports Dapper"** is already true of Brighter, CAP and several small libraries (Twinbox, TinyOutbox, Relay.Outbox, Relayer).
- **"Uses SKIP LOCKED / READPAST"** is what MassTransit, Wolverine, CAP and most small libraries already do.
- **"Lightweight standalone outbox"** is a crowded niche. Twinbox (Sep 2026) and Relayer (Nov 15, 2026) launched recently with overlapping features. None is established yet (each has under 3K downloads), but that also means nobody has proven the niche will pay off.

The biggest competitor is not on NuGet at all: it's the **hand-rolled outbox**. Most Dapper teams write a `outbox` table plus a `BackgroundService` and quietly ship the classic bugs:
- no fencing, so a stale worker overwrites newer state;
- no bound on crash loops;
- app clocks compared across servers;
- a transaction held open across the HTTP call;
- retries in lockstep;
- no dead-letter and no way to requeue.

DbRelay wins if it is **the library you adopt instead of writing that code**, and if it is visibly more trustworthy than what you would have written.

## 2. Positioning

> **DbRelay: the transactional outbox for teams that already own their SQL.**
> Enqueue on the `DbTransaction` you already have. Deliver with one interface. Every reliability guarantee is written down and proven by tests against real PostgreSQL and SQL Server.

Target user: a .NET team on Dapper/ADO.NET with PostgreSQL or SQL Server. They need reliable side effects (publish to a broker, call a webhook, sync a search index, send email) and they don't want to adopt a messaging framework, a broker abstraction or EF Core.

Not the target user: teams that want sagas, consumer-side exactly-once, routing topologies or a broker abstraction. Brighter, MassTransit, Wolverine and CAP serve them well, and we will say so in the README.

## 3. Differentiators (each must be verifiable, not a slogan)

| # | Differentiator | Why it matters | Evidence vs alternatives | How we prove it |
|---|---|---|---|---|
| 1 | **Joins *your* transaction, with zero framework.** `outbox.EnqueueAsync(transaction, message)` on a transaction you began yourself, typically with Dapper. | It's the core value of an outbox, and the frameworks make it hard. | MassTransit: EF/Mongo only (#5982 not planned). CAP: owns the transaction and pulls in EF Core. Wolverine: undocumented API plus its runtime. Brighter: CommandProcessor, mappers and producer registry. | Transaction tests: commit together, rollback, foreign/completed transaction, connection left open and usable. |
| 2 | **A published reliability contract.** `docs/reliability.md` lists every guarantee and failure mode: fenced completion, attempts counted at claim (crash loops terminate), database-clock leases, no transaction across delivery, at-least-once with stable ids. Each line links to the test that proves it. | Teams adopt infrastructure they can trust and audit. That's the gap hand-rolled and young libraries leave. | Gaps in incumbents: Brighter has no attempt or poison handling, MassTransit has no outbox dead-letter (#6231), CAP retries N× with N replicas (#1797). Young libraries mostly don't document failure semantics. | Guarantee → test traceability table. Process-kill crash test and multi-worker contention tests run in CI against real databases. |
| 3 | **A delivery contract you can implement in five minutes.** One method: `DeliverAsync(OutboxMessage, ct)` → `Delivered` / `Retry(after)` / `DeadLetter`. | Sending a webhook, a Kafka produce or a search-index update shouldn't require writing a "transport". | CAP and Brighter make a custom target a transport implementation; MassTransit and Wolverine need their endpoint model. | Samples: HTTP webhook handler, in-process handler, broker SDK handler. |
| 4 | **Minimal, auditable footprint.** Abstractions: 0 deps. Core: Microsoft.Extensions.* abstractions only. Provider: just Npgsql or SqlClient. No EF Core, Polly, Newtonsoft or OpenTelemetry SDK. | Fewer dependency conflicts and less surface for supply-chain risk. | CAP pulls in EF Core Relational. Brighter core pulls in Polly, Newtonsoft.Json, NJsonSchema and OpenTelemetry. | `dotnet list package --include-transitive` published in the release notes. |
| 5 | **A schema you own.** Small, documented, versioned table. You create it explicitly or via plain SQL scripts for DbUp, Flyway, Grate or your DBA. No auto-migrations. | DBAs and migration-tool users won't accept libraries that mutate schemas at startup. | Brighter's 24-column schema has had 7 migrations, with a rework planned (#4228). | Scripts shipped as files and embedded resources; idempotent `EnsureCreatedAsync`. |
| 6 | **Byte-faithful payloads, any format.** JSON by default; raw bytes (protobuf, Avro, signed JSON) are delivered exactly as enqueued. | Relays must not rewrite payloads. | jsonb-based designs normalise documents (inferred general property of jsonb). | Round-trip byte equality tests. |
| 7 | **Operations built in.** Dead-letter inspection, requeue, statistics, health check, OTel traces linked from enqueue to delivery, and metrics, all with no dashboard required. | On-call engineers need to answer "what's stuck and why?" | Hand-rolled outboxes rarely have any of this. | Admin API tests; sample OTel output. |

## 4. Improvements recommended before implementation

These sharpen the differentiation and are already reflected in `architecture-proposal.md`:

1. **Reliability contract as a first-class deliverable.** `docs/reliability.md` with a guarantee-to-test traceability table, kept in CI (M9).
2. **Shared provider conformance test suite.** Both providers must pass identical behavioural tests. This makes PostgreSQL/SQL Server parity provable, and gives third-party providers (MySQL, etc.) a ready test kit later.
3. **"Replace your hand-rolled outbox" guide.** Show the classic bugs and how DbRelay handles each one. This targets the real competitor.
4. **Explicit "when not to use DbRelay" section** in the README pointing to Brighter, MassTransit, Wolverine and CAP. Honesty builds trust and avoids poor-fit adopters.
5. **No breadth race.** No inbox, broker packages or dashboard in v0.x until the core contract is proven in the field.

## 5. Residual risk

Even with these differentiators there is **no technical moat**. A well-resourced competitor (for example Relayer) can match features quickly. DbRelay's durable advantages have to be focus, correctness evidence, documentation quality and a stable, small API. We will treat these as product features and protect them in reviews.
