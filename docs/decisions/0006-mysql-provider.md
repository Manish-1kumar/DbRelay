# ADR 0006: MySQL and MariaDB provider

- **Status:** Accepted, 2026-11-27 (design). Verified in CI before release.
- **Deciders:** owner (scope, ADR 0005: "Oracle first, then MySQL/MariaDB"; "pick the next database and start with that too"),
  principal architect (design)
- **Extends:** ADR 0005. It does not change the public API of `DbRelay.Abstractions` or `DbRelay`.

## Context

MySQL is widely used with Dapper, and it is the engine behind Amazon RDS for MySQL and Aurora MySQL, which ADR 0005 named as
the AWS databases that need a new provider. MariaDB shares the protocol and most of the SQL. Both support
`FOR UPDATE SKIP LOCKED` (MySQL 8.0.1+, MariaDB 10.6+), which DbRelay's claim needs.

## Decision

Add **`DbRelay.MySql`**, one package for MySQL and MariaDB.

### Scope and dependencies
- **Database versions:**
  - MySQL 8.0 and later, which includes Aurora MySQL 3 and RDS for MySQL 8.x;
  - MariaDB 10.11 and later.
- **Tested in CI:** MySQL 8.0, 8.4 LTS and MariaDB 10.11 and 11.4 LTS, in containers. Aurora and RDS are untested until
  someone runs the suite there.
- **Driver:** `MySqlConnector` 2.6 (MIT license, real async I/O). The Oracle-licensed `MySql.Data` is not used.

### Public API
The API mirrors the other providers; nothing new is added to the core packages.
- `UseMySql(connectionString, ...)`
- `UseMySql(Func<IServiceProvider, MySqlConnection>, ...)`
- `MySqlOutboxOptions`: `Schema`, `TableName`, `CommandTimeout`.

### Schema
- **A schema is a database in MySQL.** `Schema` defaults to null, meaning the connection's current database, and DbRelay
  never creates databases. That is the same rule as Oracle.
- **Identifiers:** validated by `SqlIdentifier` and quoted with backticks.
- **Table options:** InnoDB, `utf8mb4` with the `utf8mb4_bin` collation, so message types and other text compare exactly
  and store any Unicode.

### Column types

| Column | Type | Note |
|---|---|---|
| `id`, `lock_token` | `BINARY(16)` | Written with `UNHEX(@hex)` and read with `HEX(...)`, so the stored bytes never depend on the caller's `GuidFormat` connection option |
| `payload` | `LONGBLOB` | |
| `payload_size` | `BIGINT`, stored generated column (`OCTET_LENGTH(payload)`) | Computed once at insert, so a claim applies its budget without reading payloads (MY-01) |
| `headers`, `last_error` | `MEDIUMTEXT` | |
| Text columns | `VARCHAR(n)` | Same lengths as SQL Server |
| `status` | `TINYINT` | With an unnamed `CHECK` (enforced from MySQL 8.0.16 and MariaDB 10.2). Unnamed, because MySQL renames generated constraint names with the table, so re-creating it after a `RENAME TABLE` cannot collide (MY-09) |
| `attempt_count` | `INT` | |
| Times | `DATETIME(6)` | UTC from `UTC_TIMESTAMP(6)`, the database clock (invariant 5) |

### Indexes
Neither engine has partial indexes. **Indexed virtual generated columns** give the same effect:
- `claim_at = CASE WHEN status IN (0, 1) THEN available_at END`
- `cleanup_at = CASE WHEN status = 2 THEN delivered_at END`
- `dead_at = CASE WHEN status = 3 THEN dead_lettered_at END`

Rows of other statuses have NULL keys, and a range such as `claim_at <= now` skips them. Statements filter and order on the
generated columns, so the plans do not depend on how the optimizer treats `CASE`. Both engines support indexes on virtual
columns. The claim index also holds `status`, so the pending and processing counts read only the index.

### Isolation and locking
DbRelay runs its own write statements in explicit **`READ COMMITTED`** transactions. MySQL's default, `REPEATABLE READ`,
takes next-key (gap) locks on scanned ranges. With those, a claim or cleanup could block producers inserting new rows into
the same index range, and deadlock with them. READ COMMITTED locks only the rows it reads.

### Claim
One short transaction on a DbRelay connection, with no handler running (invariant 3):
1. `SELECT ... FORCE INDEX (ix_claim) ... FOR UPDATE SKIP LOCKED` on `claim_at <= now` ordered by `claim_at`, `LIMIT` the
   batch size, returning each `payload_size`. Forcing the index keeps the claim a range scan in order (MY-12): a sort would lock
   every due row for the whole claim.
2. The payload budget (ADR 0004) is applied in the store, and the first row is always taken.
3. `UPDATE ... WHERE id IN (...) AND status IN (0, 1) AND available_at <= now` leases the rows and counts the attempt
   (invariant 10). The re-check matches the Oracle provider's (OR-01).
4. The claimed rows are read by ID, and the transaction commits.

### Guarded updates and acknowledgements
- **Single-row state changes** are guarded by `WHERE id = ... AND lock_token = ...` (invariant 4).
- **Batched acknowledgements (ADR 0003):** neither engine has `UPDATE ... RETURNING`, so a batch runs in one transaction:
  1. `SELECT id, lock_token ... WHERE id IN (...) FOR UPDATE` locks the rows, in the canonical byte order (BA-07).
  2. The store keeps the items whose token still matches.
  3. One `UPDATE` changes exactly those rows.

### Inserts
- **Always on the caller's connection and transaction** (invariants 1 and 2).
- **One multi-row `INSERT` statement for up to 1,000 rows,** at 10 parameters per row, within the 65,535-parameter limit.
  One statement is atomic, so DbRelay never begins a transaction on the caller's connection.
- **`transaction: null` while the connection has a transaction:** MySqlConnector rejects the command, as SqlClient does. The
  provider explains the fix in the error, the same way as CR-11.

### Schema creation
- **`CREATE TABLE IF NOT EXISTS`,** with the indexes inline, plus `INSERT IGNORE` into the version table. This is idempotent
  and safe to run concurrently.
- **It always runs on DbRelay's own connection,** because DDL commits implicitly.

### Errors retried
- 1213 (deadlock) and 1205 (lock wait timeout) are retried for single statements and DbRelay's own transactions, which the
  server has rolled back.

## Consequences
- **A large enqueue call must fit in the server's `max_allowed_packet`:** 64 MiB by default on MySQL 8, 16 MiB on
  MariaDB. The provider guide must say so.
- **The batched-acknowledgement and claim paths take two to three round trips** instead of one, inside short transactions.
- **Aurora and RDS** are expected to behave like the engine versions they run, but stay listed as untested until verified.

## Review follow-ups (2026-11-27)

The independent review (CODE_REVIEW_REPORT.md, "MySQL provider review (ADR 0006)") found no Critical or High issues. The design
above includes its fixes. These also change the provider's behaviour:

- **DbRelay's own transactions are begun with SQL,** with `SET TRANSACTION ISOLATION LEVEL READ COMMITTED; START TRANSACTION`,
  and ended with `COMMIT` or `ROLLBACK` (MY-04).
  - MySqlConnector's `BeginTransaction` sets the isolation level for the whole session, which would leave a pooled connection at
    READ COMMITTED for the application.
  - With a statement-based binary log, the session default is kept, because READ COMMITTED would fail with error 1665 (MY-05).
- **Calls that could exceed `max_allowed_packet` are rejected before they are sent** (MY-02). The server would otherwise
  close the caller's connection.
- **With no `Schema`,** an insert on a connection to a different current database than DbRelay's is rejected (MY-07).
- **`EnsureCreatedAsync` and the health check verify the table's shape:** its indexes and columns (MY-08).
- **Batched acknowledgements** are guarded by `(id, lock_token)` pairs in SQL, and must change exactly the locked rows (MY-06).
- **Retries:** error 1020 is retried (MariaDB 11.8+ snapshot isolation, MY-11).
- **The storage interface accepts at most 1,000 records per insert** (MY-14).
- **Cached insert texts** are limited to 64 rows (MY-03).
