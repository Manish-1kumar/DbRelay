# ADR 0005: Oracle and MySQL providers; no MongoDB provider for now

- **Status:** Accepted, 2026-11-26 (Oracle design). The MySQL design gets its own ADR when that work starts.
- **Deciders:** owner (scope: "Oracle first, then MySQL/MariaDB"; "skip MongoDB for now"), principal architect (design)
- **Extends:** ADR 0001 D10 (supported databases). It does not change the public API of `DbRelay.Abstractions` or `DbRelay`.

## Context

The owner asked for more databases before testing on Azure and AWS: Oracle, MongoDB, and "any database of AWS".

- **AWS:** Amazon RDS for PostgreSQL, Aurora PostgreSQL and RDS for SQL Server run engines DbRelay already supports, so
  they need testing, not code. Amazon RDS and Aurora **MySQL** need a new provider. DynamoDB has neither SQL transactions
  nor row locks with skip semantics, so it does not fit.
- **MongoDB:** DbRelay's core contract is "enqueue on the caller's `DbConnection` and `DbTransaction`". MongoDB has
  neither, and multi-document transactions need a replica set. Supporting it would need a second, session-based enqueue API.
- **Oracle** fits the existing contract: ADO.NET (`OracleConnection`, `OracleTransaction`) and `FOR UPDATE SKIP LOCKED`.

## Decision

1. **Add `DbRelay.Oracle`** next, then **`DbRelay.MySql`** (MySQL 8.0+, MariaDB 10.6+). Each is a separate package that
   passes the full storage conformance suite and has its own CI jobs.
2. **No MongoDB provider for now.** Revisit only if users ask for it, with an ADR for the session-based API.
3. AWS RDS/Aurora PostgreSQL and RDS SQL Server are documented as supported once they have been tested.

## Oracle provider design

### Scope and dependencies
- **Database versions:** Oracle Database 19c and later.
  - **Tested in CI:** 23ai Free (`gvenzl/oracle-free`) and 21c XE (`gvenzl/oracle-xe`).
  - **Untested:** 19c, which has no free container image. Listed in KNOWN_ISSUES until tested.
- **Driver:** `Oracle.ManagedDataAccess.Core` (ODP.NET Core, net8.0 asset). It is under Oracle's license, not MIT;
  DbRelay references it and does not redistribute it.

### Public API
The API mirrors the other providers; nothing new is added to the core packages.
- `UseOracle(connectionString, ...)`
- `UseOracle(Func<IServiceProvider, OracleConnection>, ...)`
- `OracleOutboxOptions`: `Schema`, `TableName`, `CommandTimeout`.

### Schema
- **The Oracle schema is a user.** `EnsureCreatedAsync` creates the tables and indexes but never a user. `Schema` defaults
  to null, meaning the connection's current schema.
- **Identifiers:** validated by `SqlIdentifier`, uppercased, and quoted (`"OUTBOX_MESSAGES"`), so they match unquoted
  references in hand-written SQL.

### Column types

| Column | Type | Note |
|---|---|---|
| `ID`, `LOCK_TOKEN` | `RAW(16)` | Bound as `Guid.ToByteArray()`; a check constraint requires `ID` to be 16 bytes (OR-06) |
| `PAYLOAD` | `BLOB` | |
| `HEADERS`, `LAST_ERROR` | `NCLOB` | Unicode whatever the database character set (OR-08) |
| Text columns | `NVARCHAR2(n)` | Same lengths as SQL Server; Unicode whatever the database character set (OR-08) |
| `STATUS` | `NUMBER(1)` | With a check constraint |
| `ATTEMPT_COUNT` | `NUMBER(10)` | |
| Times | `TIMESTAMP(6)` | UTC. Microseconds, so a stored time is never rounded up past the database clock |

### Database clock and intervals
- **Clock:** every time is `SYS_EXTRACT_UTC(SYSTIMESTAMP)` (invariant 5). The application clock is never used.
- **Intervals:** passed as milliseconds and converted with `NUMTODSINTERVAL`.

### Partial indexes
Oracle has none. Function-based indexes give the same effect, because a key that is entirely NULL is not indexed.
- **Claim index:** `CASE WHEN STATUS IN (0, 1) THEN AVAILABLE_AT END`, then the same form on `STATUS`.
- **Cleanup index:** the same form on `DELIVERED_AT` for status 2.
- **Dead-letter index:** the same form on `DEAD_LETTERED_AT` for status 3.
- **Queries repeat the exact same expression,** and the claim, cleanup and dead-letter cursors and the statistics counts are
  **hinted** to their index.
  - **Why hints:** CI showed the optimizer choosing a full scan plus sort for the claim on a 20,000-row table.
  - **Unlike SQL Server (ADR 0002),** Oracle ignores a hint whose index is missing, so a dropped index slows these statements
    but cannot break them.
- **Pending and processing counts** come from one fast full scan of the claim index: its row count, and the sum of its 0/1
  status key. Comparing the status key to a constant does not work, because the optimizer simplifies
  `CASE ... END = 0` to `STATUS = 0` and then scans the table.
- **The plan test** (`Claim_cleanup_dead_letter_and_statistics_plans_use_the_function_based_indexes`) checks the exact cursor
  queries on 20,000 rows: each uses its index, none scans the table, and the claim does not sort.

### Claim
`FOR UPDATE ... SKIP LOCKED` cannot be combined with `FETCH FIRST` or `ROWNUM` without returning too few rows, so the claim
is **one anonymous PL/SQL block**, autocommitted as one statement:
1. It opens a cursor `SELECT ... ORDER BY <claim expression> FOR UPDATE SKIP LOCKED` and fetches row by row. Rows are
   locked as they are fetched.
2. It stops at the batch size, or at the payload budget (ADR 0004): `DBMS_LOB.GETLENGTH`, with the first row always taken.
3. It updates each fetched row: lock token, worker, attempt + 1 (invariant 10), and a lease ending at `now + lease`. The
   update re-checks `STATUS IN (0, 1) AND AVAILABLE_AT <= now` on the row it locked, and only rows it changed are claimed (OR-01).
   The cursor chose the row from its read snapshot, so a row changed in between is never claimed twice.
4. It returns the claimed IDs in claim order, as one string of 32 hex digits per ID (32,000 characters for the maximum batch of
   1,000, within `VARCHAR2(32767)`). This avoids PL/SQL array binds, whose type rules differ between Oracle and driver
   versions.
5. The store then reads the claimed rows by primary key on its own command, so their payloads come back inline (OR-11). A
   first version returned a `REF CURSOR`, whose LOB fetch size could not be confirmed.

### Guarded updates, acknowledgements and inserts
- **State changes after a claim:** single statements guarded by `WHERE ID = :id AND LOCK_TOKEN = :token` (invariant 4).
- **Batched acknowledgements (ADR 0003):**
  - one PL/SQL `FORALL` block; the IDs and tokens arrive as two hex strings;
  - `SQL%BULK_ROWCOUNT` reports which items changed;
  - items are sorted by their stored `RAW(16)` byte order (BA-07).
- **Inserts:** always on the caller's connection and transaction (invariants 1 and 2).
  - Up to 90 rows go in **one `INSERT ALL` statement**, which is all-or-nothing on its own. Oracle allows at most 999
    columns across the clauses, and each row sets 10.
  - Up to 1,000 rows (the enqueue limit) go in **one anonymous PL/SQL block** of `INSERT ALL` statements. Oracle runs a
    block called from the client as one statement and rolls all of it back on an error (OR-04).
  - So DbRelay never begins a transaction on the caller's connection, and a failed call leaves no rows in the caller's
    transaction. A first version began its own transaction above 90 rows, as SQL Server does above 200 (K9).
- **`transaction: null` on a connection that has a transaction:** ODP.NET runs the command in that transaction. This is
  the same documented behaviour as PostgreSQL, and a test pins it.

### Schema creation
- **Before 23ai there is no `IF NOT EXISTS`,** so each object is created in a PL/SQL block that ignores ORA-00955 ("name
  already used"). The script stays idempotent and non-destructive.
- **Oracle DDL commits implicitly,** so `EnsureCreatedAsync` runs on DbRelay's own connection, never the caller's.
- **Concurrent instances creating the schema:** "already exists" errors are ignored, and `DDL_LOCK_TIMEOUT` makes DDL wait
  for another instance's lock. On 21c XE, four instances still hit ORA-04020 (library cache deadlock), so
  `EnsureCreatedAsync` retries the whole idempotent script on ORA-04020, ORA-04021 and ORA-00054. This needs no extra
  grants, unlike `DBMS_LOCK`.

### Verified in CI
CI run 45872607007, on Oracle 23ai Free across all 4 target frameworks and on 21c XE: 82/82 tests each, 0 skipped. The
tests cover:
- the full storage conformance suite, including 8 concurrent claimers with no message claimed twice and the 250-message
  atomic `EnqueueMany`;
- all-or-nothing inserts with a primary-key violation part-way, for one statement and for several;
- the null-transaction behaviour;
- an empty payload;
- concurrent schema creation;
- the plan test above.

## Consequences
- **Two more packages** to release and maintain, with a CI job per database version.
- **The Oracle provider is more PL/SQL-heavy than the others.** Its code review must cover cursor and lock behaviour
  specifically.
- **MongoDB users stay unsupported** until there is demand.
