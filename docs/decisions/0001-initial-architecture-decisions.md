# ADR 0001: Initial architecture decisions (D1–D10)

- **Status:** Accepted, 2026-11-18
- **Deciders:** project owner (D1), principal architect (D2–D10, delegated by the owner: "rest should be your decision")
- **Context:** `docs/architecture-proposal.md`, `docs/competitor-analysis.md`

## D1: Target frameworks: `net8.0;net9.0;net10.0;net11.0`
- **Decision:** the owner chose to target all four frameworks.
- **Context:** .NET 8 and 9 reached end of support on 2026-11-10. .NET 8 still has a very large installed base, so supporting it widens adoption. The net9 build is technically redundant (the net8 build runs on .NET 9), but it is kept at the owner's request.
- **Consequences:**
  - Microsoft.Extensions.* dependencies are pinned per TFM (8.0.x / 9.0.x / 10.0.x / 11.0.x), so consumers are never forced onto a newer major version.
  - Build requires the .NET 11 SDK.
  - Tests run on all four runtimes.
  - APIs added after .NET 8 (for example `Guid.CreateVersion7`) are not used directly; DbRelay ships its own UUIDv7 generator.
  - We will drop EOL TFMs in a future **major** version only.

## D2: Providers use plain ADO.NET internally (no Dapper dependency)
- **Decision:** providers execute their fixed statements with `DbCommand`.
- **Rationale:** no Dapper version conflicts for consumers, no reflection on hot paths, one less dependency.
- **Compatibility:** Dapper compatibility is proven by tests and samples that write business data with Dapper inside the same transaction.

## D3: Explicit transaction parameter; no ambient transaction in v0.1
- **Decision:** `EnqueueAsync(transaction, message)` derives the connection from the transaction you pass in.
- **Rationale:** no `TransactionScope` enlistment, because a second connection escalates to a distributed transaction.
- **Naming:** the non-atomic path is named `EnqueueStandaloneAsync`.
- **Future:** an AsyncLocal unit-of-work scope may be added later without breaking changes.

## D4: PostgreSQL payload stored as `bytea`
- **Decision:** the payload column is `bytea`; `headers` (DbRelay-owned metadata) uses `jsonb`.
- **Rationale:** payloads stay byte-faithful and any content type works. `jsonb` would normalise documents and reject non-JSON payloads.
- **SQL Server:** equivalently uses `varbinary(max)`.

## D5: Dead letters stay in the outbox table
- **Decision:** dead-lettered messages keep `status = 3` in the outbox table.
- **Rationale:** requeue is a single guarded `UPDATE`; no cross-table moves.

## D6: Attempt history
- **Decision:** v0.1 stores `attempt_count`, `last_attempt_at` and `last_error`.
- **Rationale:** per-attempt history goes to logs and traces; a history table is deferred.

## D7: Package split
- **Decision:** `DbRelay.Abstractions` (zero dependencies), `DbRelay` (engine + storage SPI), `DbRelay.PostgreSql`, `DbRelay.SqlServer`.

## D8: Versioning
- **Decision:** the first release is `0.1.0`, following SemVer 2.0.
- **Rationale:** pre-1.0 signals the API may change based on real-world feedback.

## D9: License
- **Decision:** MIT.

## D10: Supported databases
- **Decision:** PostgreSQL 15+, and SQL Server 2019+ / Azure SQL.
- **Testing:** PostgreSQL 17 locally (15 and 17 in CI); SQL Server 2022.

## Supplementary decisions made during M3 (architect)
- **S1: Name.** Keep **DbRelay**. The NuGet ID is free; reserve the `DbRelay.*` prefix before the first publish; get a manual trademark check before 1.0.
- **S2: Scheduling with the database clock.** `EnqueueOptions` offers `Delay` (relative, evaluated with the database clock) and `DeliverAt` (absolute). They are mutually exclusive.
- **S3: Retention purge uses database time.** `PurgeDeliveredAsync(TimeSpan olderThan)` and the cleanup retention are relative to database time.
- **S4: `OutboxMessage` is plain data.** It has public `required init` properties so handlers are easy to unit test. Typed deserialization goes through the injected `IMessageSerializer`; `OutboxMessage` holds no hidden service references.
- **S5: Dispatcher dispatch loop.** It claims a batch, starts each message when a concurrency slot frees, and claims again once every claimed message has *started*. This avoids head-of-line blocking without hoarding leases.
