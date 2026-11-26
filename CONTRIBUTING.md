# Contributing to DbRelay

Thanks for your interest. DbRelay is a small library with a strong focus on correctness. A change is welcome when it keeps the
library small and its guarantees provable.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md). Report security issues privately as described
in [SECURITY.md](SECURITY.md), never in a public issue.

## Prerequisites

- The .NET 11 SDK (`global.json` pins `11.0.100` with `latestFeature` roll-forward). The libraries target
  `net8.0;net9.0;net10.0;net11.0`, so running every test needs the .NET 8, 9, 10 and 11 runtimes.
- For database tests, one of:
  - **Docker**, used through Testcontainers (`postgres:17-alpine` and `mcr.microsoft.com/mssql/server:2022-latest`); or
  - your own servers, selected with environment variables (see below).

## Build and test

```powershell
dotnet restore
dotnet build -c Release              # warnings are errors
dotnet test -c Release               # unit, provider, conformance and integration tests
dotnet format --verify-no-changes
dotnet pack -c Release -o artifacts
```

Run one project or one target framework:

```powershell
dotnet test --project tests/DbRelay.Tests/DbRelay.Tests.csproj -c Release -f net10.0
```

### Database tests

Provider, conformance and integration tests run against **real databases**. Each fixture picks a database in this order:

1. the environment variable, if it is set. If that database cannot be reached, the PostgreSQL tests **fail**, while the
   SQL Server tests are reported as skipped with the connection error as the reason;
2. a Testcontainers container, if Docker is running;
3. otherwise every database test is **skipped**.

| Variable | Example |
|---|---|
| `DBRELAY_TEST_POSTGRES` | `Host=localhost;Username=postgres;Password=...;Database=dbrelay_tests` |
| `DBRELAY_TEST_SQLSERVER` | `Server=.\SQLEXPRESS;Integrated Security=true;TrustServerCertificate=true;Database=dbrelay_tests` |
| `DBRELAY_TEST_ORACLE` | `User Id=system;Password=...;Data Source=localhost:1521/FREEPDB1` (a user that can create and drop users: each test gets its own schema, which in Oracle is a user) |

Tests create their own uniquely named schemas (for example `t_<hex>` and `it_<hex>`) and drop them afterwards, so they can share a
database. The SQL Server suite runs the conformance tests twice, with `READ_COMMITTED_SNAPSHOT` off and on. It uses a second
database for the RCSI-on run (`<database>_rcsi`). On a local server the fixture creates both databases, so the login needs permission
to create databases; on Azure SQL you create them yourself (below).

**Skipped is not passed.** If database tests were skipped, say so in your pull request.

#### Azure SQL Database (free offer)

The SQL Server suite can run against Azure SQL Database. On Azure SQL (detected by `SERVERPROPERTY('EngineEdition') = 5`) the
fixture **never creates or alters databases**: a database made with `CREATE DATABASE` is not covered by the
[free offer](https://learn.microsoft.com/azure/azure-sql/database/free-offer) and is billed. It uses only the two databases you
create, and skips a run whose database is missing or set up wrong, with the fix as the reason.

1. In the Azure portal, create a SQL database named `dbrelay_tests`, and select **Start free** in the **Create a database** pane.
   - When the free limit is reached, the database should **auto-pause until next month**. With the CLI, this is
     `az sql db create ... --use-free-limit true --free-limit-exhaustion-behavior AutoPause`. Never choose "continue using the
     database for additional charges": you can't switch back.
   - Create a new logical server with SQL authentication.
   - Under **Networking**, allow your current client IP address.
2. Create a second free database named `dbrelay_tests_rcsi` on the same server. It keeps Azure's default,
   `READ_COMMITTED_SNAPSHOT ON`.
3. Connect to the server's `master` database, with no other connections open to `dbrelay_tests`, and run:
   `ALTER DATABASE [dbrelay_tests] SET READ_COMMITTED_SNAPSHOT OFF;`
4. Set `DBRELAY_TEST_SQLSERVER` to
   `Server=tcp:<server>.database.windows.net,1433;Database=dbrelay_tests;User ID=<login>;Password=<password>;Encrypt=true`.
   Keep the password out of files you commit.
5. Run one target framework to save free compute: `dotnet test --project tests/DbRelay.SqlServer.Tests -c Release -f net10.0`.

A free database uses its monthly allowance (100,000 vCore seconds) while it is online, including the 15 idle minutes before it
pauses. An open SSMS, Query editor or VS Code connection keeps it online. Do not run the soak harness or benchmarks against it.

### Test projects

| Project | What it covers |
|---|---|
| `tests/DbRelay.Tests` | Unit tests: options validation, retry math, serialization, registration, and the dispatcher against an in-memory store |
| `tests/DbRelay.Storage.Conformance` | The behavioural contract every storage provider must pass (a class library, not run on its own) |
| `tests/DbRelay.PostgreSql.Tests` | PostgreSQL provider tests plus the conformance suite |
| `tests/DbRelay.SqlServer.Tests` | SQL Server provider tests plus the conformance suite (RCSI off and on) |
| `tests/DbRelay.Oracle.Tests` | Oracle provider tests plus the conformance suite |
| `tests/DbRelay.IntegrationTests` | Real hosts and real child processes against PostgreSQL: crash recovery, stale workers, database interruption, shutdown, retry schedule, security |
| `tests/DbRelay.CrashWorker` | The child process the crash tests start and kill (not a test project) |

[docs/reliability.md](docs/reliability.md) maps each documented guarantee to the tests that prove it. If you change a guarantee,
update that table.

### Cleaning up after interrupted test runs

Each database test creates its own schema and drops it when the test finishes. The name is `t_`, `p_` or (for integration
tests) `it_`, followed by 16 hex characters: 8 for the creation time and 8 random. If a test process is killed (Ctrl+C, a
debugger stop, an IDE cancel), its schemas stay behind. The next test run removes them automatically once they are more than
6 hours old (`tests/Shared/TestSchemaNames.cs`). Younger schemas are never touched, because the target frameworks run their
tests in parallel against the same database.

To remove every test schema at once, for example after renaming the format, run the following against a **test** database
only, while no tests are running:

```sql
-- PostgreSQL (run against the test database only)
DO $$ DECLARE s text;
BEGIN
  FOR s IN SELECT nspname FROM pg_namespace WHERE nspname ~ '^(t|it|p)_[0-9a-f]{16}$' LOOP
    EXECUTE format('DROP SCHEMA %I CASCADE', s);
  END LOOP;
END $$;
```

```sql
-- SQL Server (run in dbrelay_tests and dbrelay_tests_rcsi only)
DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'DROP TABLE ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N';'
FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name LIKE 't[_]________________' OR s.name LIKE 'p[_]________________' OR s.name LIKE 'it[_]________________';
EXEC (@sql);
SET @sql = N'';
SELECT @sql += N'DROP SCHEMA ' + QUOTENAME(name) + N';' FROM sys.schemas
WHERE name LIKE 't[_]________________' OR name LIKE 'p[_]________________' OR name LIKE 'it[_]________________';
EXEC (@sql);
```

## Engineering rules

The full list is in [CLAUDE.md](CLAUDE.md). The ones reviewers check first:

1. DbRelay never commits, rolls back or disposes a caller-owned connection or transaction.
2. The outbox insert uses exactly the caller's connection and transaction.
3. No database transaction is held open while a delivery handler runs.
4. Every state change after a claim is guarded by the claim's lock token.
5. Lease and availability comparisons use the database clock.
6. Delivery is at-least-once. Never document or imply exactly-once.
7. Never create a CLR type from a string read from the database.
8. All SQL is parameterized. Identifiers come only from configuration validated by `SqlIdentifier` and are quoted.
9. Never log payloads or connection strings.
10. Attempts are counted at claim time.

Coding standards: nullable enabled, warnings as errors, XML docs on every public API, central package management (no versions
in project files), async with `CancellationToken` everywhere, `ConfigureAwait(false)` in library code, `LoggerMessage`
source-generated logging, and `TimeProvider` for application-side time. Prefer `internal`; a new public type needs a
justification.

## Adding a storage provider

A provider implements `DbRelay.Storage.IOutboxStore` (in the `DbRelay` package) and an `IOutboxSchemaManager`, and registers
them from a `Use<Provider>` extension method on `DbRelayBuilder`:

```csharp
public static DbRelayBuilder UseMyDatabase(this DbRelayBuilder relay, string connectionString)
{
    relay.UseStorage(sp => new MyOutboxStore(connectionString));
    relay.Services.AddSingleton<IOutboxSchemaManager>(sp => new MySchemaManager(connectionString));
    return relay;
}
```

`src/DbRelay.PostgreSql` and `src/DbRelay.SqlServer` are the reference implementations. The XML docs on `IOutboxStore` are
the specification. In short:

- `InsertAsync` must use exactly the connection and transaction it is given, and must never commit, roll back, close or dispose
  them. `ValidateCallerResources` rejects connection and transaction types of other providers.
- `ClaimAsync` claims atomically with row-skipping locks (or an equivalent), sets `available_at = db_now + lease`, increments
  `attempt_count`, and stores the lock token. Two concurrent claims must never return the same row.
- `MarkDeliveredAsync`, `ScheduleRetryAsync`, `MarkDeadLetteredAsync` and `ReleaseAsync` are guarded by
  `id AND lock_token`, and return `false` when the guard does not match.
- Every time comparison uses the database clock.
- Identifiers are validated with `SqlIdentifier.Validate` and quoted; everything else is a parameter.

Then prove it with the shared conformance suite. Reference `tests/DbRelay.Storage.Conformance` from your provider's test
project and derive from `OutboxStoreConformanceTests`, implementing its hooks: `SkipReason`, `UseStorage`,
`OpenConnectionAsync`, `CreateOrdersTableSql`, `ExpireAsync`, `GetDatabaseNowAsync` and `DropSchemaAsync`.
`tests/DbRelay.PostgreSql.Tests/PostgreSqlConformanceTests.cs` is a complete example in about 30 lines. A provider is not
accepted until it passes the whole suite against a real database, plus provider-specific tests for its locking behaviour and
its query plans.

## Adding a delivery handler

Delivery handlers are application code: DbRelay does not ship transport packages in 0.x. Implement `IMessageDeliveryHandler`
and register it with `relay.UseDeliveryHandler<THandler>()`. See
[`samples/CustomHandler.Sample`](samples/CustomHandler.Sample) and the handler section of the README. A good handler:

- passes the `CancellationToken` to every network call;
- returns `Delivered`, `Retry(reason, retryAfter)` or `DeadLetter(reason)` explicitly, and lets unexpected exceptions bubble
  up (they are retried unless `RetryOptions.IsPermanentFailure` classifies them);
- sends `OutboxMessage.Id` to the receiver as an idempotency key;
- keeps reasons short and free of payload data and secrets (they are stored and logged).

If you want to propose a reusable handler (for example for a broker), open a feature request first.

## Samples and documentation

Samples live in `samples/`, target `net10.0` and must build with `dotnet build samples/<name> -c Release`. Code in the README
and in `docs/` is copied from the samples, between `// snippet:` and `// end-snippet` markers. Change the sample first and then
the docs, never the other way round.

## Pull requests

1. For anything beyond a small fix, open an issue first to agree on the approach. Changes to the public API, the database
   schema or a delivery guarantee need an ADR in `docs/decisions/`.
2. Keep pull requests focused. Include tests; never weaken an existing test to make it pass.
3. Fill in the pull request template, including the test summary and whether any database tests were skipped.
4. Update `CHANGELOG.md` under **Unreleased** when behaviour or API changes.
5. A maintainer reviews against the invariants above before merging.
