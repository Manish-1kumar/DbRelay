# DbRelay - Engineering Guide for Claude Code

DbRelay is a lightweight transactional outbox library for .NET. Business data and outbox messages are written in the **same caller-owned database transaction**; a background dispatcher later delivers messages to a user-supplied delivery handler with **at-least-once** semantics.

Primary users: C#/.NET developers using Dapper or ADO.NET with PostgreSQL (first) and SQL Server (second). No EF Core. No mandatory broker or messaging framework.

## Current phase

See `PROJECT_STATUS.md` and `IMPLEMENTATION_TRACKER.md` before starting any work. Architecture is defined in `docs/architecture-proposal.md` and ADRs in `docs/decisions/`. **Do not implement anything that contradicts an accepted ADR**; propose a new ADR instead.

## Repository layout

```
src/DbRelay.Abstractions   Public contracts used by application and handler code. Zero dependencies.
src/DbRelay                Engine: DI, options, serialization, dispatcher, retry, diagnostics, storage SPI.
src/DbRelay.PostgreSql     PostgreSQL provider (Npgsql).
src/DbRelay.SqlServer      SQL Server provider (Microsoft.Data.SqlClient).
tests/*                    xUnit v3. Provider tests use real databases.
samples/*                  Runnable samples; documentation snippets come from here.
benchmarks/*               BenchmarkDotNet + throughput harness.
docs/                      Guides, research, ADRs (docs/decisions).
.claude/agents/            Role charters for the engineering team.
```

## Build and test

```powershell
dotnet restore DbRelay.slnx
dotnet build DbRelay.slnx -c Release   # warnings are errors
dotnet test --solution DbRelay.slnx -c Release   # unit + provider + integration
dotnet format --verify-no-changes
dotnet pack -c Release -o artifacts
```

Database tests use Testcontainers when Docker is running. Otherwise set:
- `DBRELAY_TEST_POSTGRES` - e.g. `Host=localhost;Username=postgres;Password=...;Database=dbrelay_tests`
- `DBRELAY_TEST_SQLSERVER` - e.g. `Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true;Database=dbrelay_tests`

If neither is available, database tests are **skipped and must be reported as skipped**, never as passed.

## Non-negotiable invariants

1. DbRelay never commits, rolls back or disposes a caller-owned connection or transaction.
2. The outbox insert uses exactly the caller's connection and transaction; the pairing is validated.
3. No database transaction is held open while a delivery handler runs.
4. Every state change after a claim is guarded by the claim's lock token (`WHERE id = @id AND lock_token = @token`).
5. Lease and availability comparisons use the **database clock**, not application clocks.
6. Delivery is at-least-once. Never document or imply exactly-once.
7. Never activate a CLR type from a string read from the database.
8. All SQL is parameterized. Schema/table names come only from configuration validated by `SqlIdentifier` and are quoted.
9. Never log message payloads or connection strings. Stored error text is truncated.
10. Attempts are counted at claim time so crash loops terminate in dead-letter.

## Coding standards

- Libraries and tests target `net8.0;net9.0;net10.0;net11.0` (ADR 0001 D1); build requires the .NET 11 SDK (`global.json`). Do not use APIs missing on .NET 8 (e.g. `System.Threading.Lock`, `Guid.CreateVersion7`, `Guid.Version`). Nullable enabled, `TreatWarningsAsErrors`, XML docs for all public APIs.
- Tests run on Microsoft.Testing.Platform: `dotnet test --project <path> -c Release [-f net10.0]`. Test projects reference `xunit.v3` only (no Microsoft.NET.Test.Sdk / xunit.runner.visualstudio).
- Central package management (`Directory.Packages.props`); no versions in project files.
- Async all the way, `CancellationToken` on every async API, `ConfigureAwait(false)` in library code.
- Prefer `internal`. Every new public type needs justification against the approved API.
- `LoggerMessage` source-generated logging. `TimeProvider` for app-side time.
- Dispose every `DbCommand`, `DbDataReader`, owned connection and `CancellationTokenSource`.
- Tests accompany every change. Never weaken a test to make it pass.

## Team process

- The principal architect coordinates; file ownership is listed in each `.claude/agents/*.md` charter. Do not edit files owned by another role without coordination.
- Subagents must return a written report: files changed, commands run, **actual** results.
- Never trust a claim that tests passed - re-run them and read the output.
- Track work in `IMPLEMENTATION_TRACKER.md` with statuses: NOT STARTED, IN PROGRESS, IMPLEMENTED - NOT VERIFIED, TESTED, REVIEWED, COMPLETED, BLOCKED.

## Approval gates (do not cross without explicit user approval)

- Publishing to NuGet, pushing to GitHub, creating releases.
- Material changes to the approved architecture or public API.
- Deleting or overwriting user files.
