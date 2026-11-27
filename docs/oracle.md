# Oracle guide

```shell
dotnet add package DbRelay.Oracle --version 0.3.0
```

The design is in [ADR 0005](decisions/0005-oracle-and-mysql-providers.md).

Supported: Oracle Database 19c and later. **Tested:** Oracle Database 23ai Free and 21c Express Edition, in Linux containers in
CI. 19c is not tested yet, because it has no free container image. Provider: `DbRelay.Oracle`, built on ODP.NET Core
(`Oracle.ManagedDataAccess.Core`, under Oracle's license).

## Registration

```csharp
builder.Services.AddDbRelay(relay =>
{
    relay.UseOracle(connectionString, oracle => oracle.Schema = "app"); // Schema is optional
    relay.UseDeliveryHandler<MyDeliveryHandler>();
});
```

Enqueue on your own `OracleConnection` and `OracleTransaction`:

```csharp
await using var connection = new OracleConnection(connectionString);
await connection.OpenAsync(ct);
await using var transaction = (OracleTransaction)await connection.BeginTransactionAsync(ct);

// ... your business writes on this connection ...
await outbox.EnqueueAsync(transaction, new OrderPlaced(orderId, customer, total), ct);

await transaction.CommitAsync(ct);
```

`UseOracle(Func<IServiceProvider, OracleConnection>, ...)` is also available. The factory must return a **new, unopened**
connection each time; DbRelay opens and disposes it.

## Schema

- **The schema is a user.** In Oracle, a schema belongs to a user, and **DbRelay never creates users.**
  - `Schema` defaults to the connection's own user, which is the usual setup: the application's user owns its tables.
  - If you set `Schema`, that user must exist, and the connection's user needs rights to create and use tables in it.
- **Names are upper case.** DbRelay stores its names in upper case and quotes them, so they match unquoted names in your own
  SQL.
  - `TableName = "outbox_messages"` (the default) creates `OUTBOX_MESSAGES`.
  - The version table is `DBRELAY_SCHEMA_VERSION`, prefixed because it shares your schema.
- **Creating the schema:** `EnsureCreatedAsync()` and `GetCreateScript()` use the script embedded in the provider:
  [`src/DbRelay.Oracle/Scripts/001_create_outbox.sql`](../src/DbRelay.Oracle/Scripts/001_create_outbox.sql).
  - The script is one PL/SQL block ending with `/`, so you can run it in SQL*Plus or SQLcl.
  - It is idempotent and non-destructive: every object is created separately, and "already exists" errors are ignored.
  - Several instances may run it at once.
  - Oracle commits DDL implicitly, so DbRelay always runs it on its own connection, never yours.

### Indexes

Oracle has no partial indexes, so DbRelay uses **function-based indexes**. Rows of other statuses map to NULL keys, which
Oracle does not store, so each index stays as small as a partial index would be.

| Index | Key | Used by |
|---|---|---|
| `IX_<table>_CLAIM` | `CASE WHEN STATUS IN (0, 1) THEN AVAILABLE_AT END`, then the same form on `STATUS` | Claims, statistics |
| `IX_<table>_CLEANUP` | `CASE WHEN STATUS = 2 THEN DELIVERED_AT END` | Retention cleanup, statistics |
| `IX_<table>_DEAD` | `CASE WHEN STATUS = 3 THEN DEAD_LETTERED_AT END` | Dead-letter requeue, statistics |

- **DbRelay's statements are hinted to these indexes,** so their plans do not depend on optimizer statistics.
  - CI showed a full scan plus sort for the claim on a 20,000-row table before the hints.
  - Unlike SQL Server, Oracle ignores a hint whose index is missing, so a dropped index makes these statements slower but
    does not break them.
- **Keep the indexes.** Rebuilding them is fine.
- **The plans are tested:** the provider test `Claim_cleanup_dead_letter_and_statistics_plans_use_the_function_based_indexes`
  checks them.

## Behaviour specific to Oracle

- **Empty strings are NULL.** Oracle stores `''` as NULL, so an empty `Destination`, `CorrelationId` or `CausationId` reads
  back as `null`. An empty payload is stored as an empty BLOB and reads back as empty.
- **`transaction: null` on a connection that has a transaction joins that transaction,** as on PostgreSQL. ODP.NET runs
  every command in its connection's transaction. Pass the transaction anyway, so the intent is explicit.
- **`EnqueueManyAsync` is always one statement,** so it is all-or-nothing on its own, and DbRelay never begins a
  transaction on your connection.
  - Up to 90 messages are one `INSERT ALL` statement.
  - Up to the maximum of 1,000, they are one anonymous PL/SQL block of `INSERT ALL` statements, which Oracle runs and rolls
    back as one statement.
  - If one row fails, nothing from the call is left in your transaction, even if you catch the error and commit.
- **Claims** use `FOR UPDATE SKIP LOCKED` in a PL/SQL block that fetches and locks one row at a time, so concurrent
  dispatchers never wait for each other.
  - Each locked row is checked again before it is claimed, so a message that changed after the claim chose it, for example
    one just acknowledged, is never claimed twice.
  - The claimed rows are then read by ID, with their payloads in the same round trip.
- **The health check fails if an outbox index is missing or unusable.** Oracle ignores a hint whose index is missing, so
  DbRelay would otherwise keep working with full table scans. Run `EnsureCreatedAsync` to recreate a missing index, or
  `ALTER INDEX ... REBUILD` an unusable one.
- **Times** are `TIMESTAMP(6)` in UTC, read from the database clock (`SYS_EXTRACT_UTC(SYSTIMESTAMP)`).
- **Text is Unicode whatever the database character set:** text columns are `NVARCHAR2` and `NCLOB`, so message types,
  headers and errors survive even on a database created with a single-byte character set such as `WE8MSWIN1252`.
- **No session settings are changed.** DbRelay's connections may come from a pool you share, so it never runs
  `ALTER SESSION`.

## Permissions

| Operation | Needs |
|---|---|
| Enqueue (your connection) | `INSERT` on the outbox table |
| Dispatcher, cleanup, admin, health check | `SELECT, INSERT, UPDATE, DELETE` on the outbox table |
| `EnsureCreatedAsync` | `CREATE TABLE` in the schema (or `CREATE ANY TABLE` and `CREATE ANY INDEX` for another schema), a tablespace quota, and `SELECT, INSERT` on `DBRELAY_SCHEMA_VERSION` |

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON APP.OUTBOX_MESSAGES TO APP_RUNTIME;
```

## Useful queries

```sql
-- Backlog by status (0 pending, 1 processing, 2 delivered, 3 dead-lettered)
SELECT STATUS, COUNT(*) FROM OUTBOX_MESSAGES GROUP BY STATUS;

-- Oldest due message
SELECT MIN(AVAILABLE_AT) FROM OUTBOX_MESSAGES WHERE STATUS IN (0, 1) AND AVAILABLE_AT <= SYS_EXTRACT_UTC(SYSTIMESTAMP);

-- Recent dead letters
SELECT RAWTOHEX(ID), MESSAGE_TYPE, DEAD_LETTERED_AT, DBMS_LOB.SUBSTR(LAST_ERROR, 200, 1)
FROM OUTBOX_MESSAGES WHERE STATUS = 3 ORDER BY DEAD_LETTERED_AT DESC FETCH FIRST 20 ROWS ONLY;
```

## Testing the provider

Set `DBRELAY_TEST_ORACLE` to an ODP.NET connection string for a user that can create and drop users. Each test gets its own
schema, which in Oracle is a user. In CI this is `SYSTEM` on the container's pluggable database:

```text
User Id=system;Password=<password>;Data Source=localhost:1521/FREEPDB1
```

Without it, the Oracle tests are reported as skipped. See [CONTRIBUTING.md](../CONTRIBUTING.md).
