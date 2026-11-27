# MySQL and MariaDB guide

```shell
dotnet add package DbRelay.MySql --version 0.3.0
```

The design is in [ADR 0006](decisions/0006-mysql-provider.md).

**Supported:**
- MySQL 8.0 and later, including Amazon RDS for MySQL 8.x and Aurora MySQL 3;
- MariaDB 10.11 and later.

**Tested in CI:** MySQL 8.0, MySQL 8.4 LTS, MariaDB 10.11 and MariaDB 11.4, in containers. Amazon RDS and Aurora are not tested
yet. They run the same engines, so they are expected to behave the same.

**Provider:** `DbRelay.MySql`, built on MySqlConnector (MIT license).

## Registration

```csharp
builder.Services.AddDbRelay(relay =>
{
    relay.UseMySql(connectionString, mysql => mysql.Schema = "app"); // Schema is optional
    relay.UseDeliveryHandler<MyDeliveryHandler>();
});
```

Enqueue on your own `MySqlConnection` and `MySqlTransaction`:

```csharp
await using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync(ct);
await using var transaction = await connection.BeginTransactionAsync(ct);

// ... your business writes on this connection and transaction ...
await outbox.EnqueueAsync(transaction, new OrderPlaced(orderId, customer, total), ct);

await transaction.CommitAsync(ct);
```

`UseMySql(Func<IServiceProvider, MySqlConnection>, ...)` is also available. The factory must return a **new, unopened**
connection each time; DbRelay opens and disposes it.

## Schema

- **The schema is a database.** In MySQL, a schema is a database, and **DbRelay never creates databases.**
  - `Schema` defaults to the connection's current database (`Database=` in the connection string).
  - If you set `Schema`, that database must exist, and the connection's user needs rights on it.
- **Creating the tables:** `EnsureCreatedAsync()` and `GetCreateScript()` use
  [`src/DbRelay.MySql/Scripts/001_create_outbox.sql`](../src/DbRelay.MySql/Scripts/001_create_outbox.sql).
  - It uses `CREATE TABLE IF NOT EXISTS` with the indexes inline, and `INSERT IGNORE` into `dbrelay_schema_version`.
  - It is idempotent and safe to run from several instances at once.
  - DDL commits implicitly, so DbRelay always runs it on its own connection, never yours.
- **Tables** are InnoDB with `utf8mb4` and the `utf8mb4_bin` collation, so any Unicode text is stored and message types compare
  exactly.
- **Table names are at most 58 characters.** MySQL names the table's CHECK constraint `<table>_chk_1`, within its
  64-character limit.

### Indexes

Neither engine has partial indexes, so DbRelay adds **virtual generated columns** and indexes them. The columns are NULL for
rows of other statuses, and DbRelay's statements filter and order on them.

| Column and index | Value | Used by |
|---|---|---|
| `claim_at`, `ix_<table>_claim` (with `status`) | `available_at` while pending or processing | Claims, statistics |
| `cleanup_at`, `ix_<table>_cleanup` | `delivered_at` once delivered | Retention cleanup, statistics |
| `dead_at`, `ix_<table>_dead` | `dead_lettered_at` once dead-lettered | Dead-letter requeue, statistics |

The provider test `Claim_cleanup_dead_letter_and_statistics_plans_use_the_generated_column_indexes` checks the plans with
`EXPLAIN` on 20,000 rows.

## Behaviour specific to MySQL and MariaDB

- **IDs are `BINARY(16)`.** DbRelay writes them with `UNHEX` and reads them with `HEX`, so the stored bytes do not depend on
  MySqlConnector's `GuidFormat` option. In SQL, `HEX(id)` shows the message ID without dashes.
- **`transaction: null` while the connection has a transaction is rejected,** as on SQL Server. MySqlConnector requires the
  active transaction on every command. The error says to pass it.
- **`EnqueueManyAsync` is always one multi-row `INSERT`,** so it is all-or-nothing on its own, and DbRelay never begins a
  transaction on your connection.
- **Large calls must fit in `max_allowed_packet`.** One enqueue call is one packet, and the server closes a connection that
  sends a bigger one.
  - The default limit is 64 MiB on MySQL 8 and 16 MiB on MariaDB.
  - A call that could come close is checked against the server's limit **before** it is sent. It is rejected with an
    explanation, and your connection and transaction stay intact.
  - The check counts the bytes as they are sent, including MySQL escaping, so it rejects only calls that really could exceed
    the limit. Split the work into several calls, or raise the limit.
- **With no `Schema` configured, enqueue on a connection to DbRelay's database.** An unqualified table resolves to the
  connection's current database. If yours differs from the one in DbRelay's connection string, the enqueue is rejected,
  rather than storing messages the dispatcher never sees.
- **Isolation:** DbRelay's own claims, batched acknowledgements, cleanup and bulk requeue run in short `READ COMMITTED`
  transactions.
  - Under the default, `REPEATABLE READ`, their range scans would take gap locks that could block your inserts.
  - DbRelay begins these transactions with `SET TRANSACTION`, which applies to one transaction only. So a pooled connection you
    share keeps its own isolation level, even with `ConnectionReset=false`.
  - Your own transactions are not affected.
  - With a **statement-based binary log** (`binlog_format=STATEMENT`), InnoDB rejects writes under `READ COMMITTED`. DbRelay
    detects this and uses your server's default isolation instead. Row-based and mixed logging, the defaults, are unaffected.
- **Claims** use `SELECT ... FOR UPDATE SKIP LOCKED`, forced onto the claim index so they never sort and lock every due row.
  Concurrent dispatchers never wait for each other or claim the same message.
- **The health check verifies the table's shape:** that it answers, and has its three indexes and every column the provider
  uses. `EnsureCreatedAsync` runs the same check, because `CREATE TABLE IF NOT EXISTS` accepts an existing table of any shape.
- **Server query logs can contain values.** MySqlConnector sends parameter values in the statement text. If the server's
  general log or slow query log is on, payloads and headers can appear there. DbRelay itself never logs them.
- **Times** are `DATETIME(6)` in UTC, from the database clock (`UTC_TIMESTAMP(6)`).
- **Empty strings stay empty** (unlike Oracle).

## Permissions

| Operation | Needs |
|---|---|
| Enqueue (your connection) | `INSERT` on the outbox table |
| Dispatcher, cleanup, admin, health check | `SELECT, INSERT, UPDATE, DELETE` on the outbox table |
| `EnsureCreatedAsync` | `CREATE` on the database, and `SELECT, INSERT` on `dbrelay_schema_version` |

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON app.outbox_messages TO 'app_runtime'@'%';
```

## Useful queries

```sql
-- Backlog by status (0 pending, 1 processing, 2 delivered, 3 dead-lettered)
SELECT status, COUNT(*) FROM outbox_messages GROUP BY status;

-- Oldest due message
SELECT MIN(claim_at) FROM outbox_messages WHERE claim_at <= UTC_TIMESTAMP(6);

-- Recent dead letters
SELECT HEX(id), message_type, dead_lettered_at, LEFT(last_error, 200)
FROM outbox_messages WHERE dead_at IS NOT NULL ORDER BY dead_at DESC LIMIT 20;
```

## Testing the provider

Set `DBRELAY_TEST_MYSQL` to a MySqlConnector connection string, without a database, for a user that can create and drop
databases. Each test gets its own schema, which in MySQL is a database. In CI this is `root`:

```text
Server=127.0.0.1;Port=3306;User ID=root;Password=<password>;SslMode=Preferred;AllowPublicKeyRetrieval=true
```

Without it, the MySQL tests are reported as skipped. See [CONTRIBUTING.md](../CONTRIBUTING.md).
