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
| `ID`, `LOCK_TOKEN` | `RAW(16)` | Bound as `Guid.ToByteArray()` |
| `PAYLOAD` | `BLOB` | |
| `HEADERS`, `LAST_ERROR` | `CLOB` | 2,000 characters can exceed 4,000 bytes in AL32UTF8 |
| Text columns | `VARCHAR2(n CHAR)` | Same lengths as SQL Server |
| `STATUS` | `NUMBER(1)` | With a check constraint |
| `ATTEMPT_COUNT` | `NUMBER(10)` | |
| Times | `TIMESTAMP(3)` | UTC |

### Database clock and intervals
- **Clock:** every time is `SYS_EXTRACT_UTC(SYSTIMESTAMP)` (invariant 5). The application clock is never used.
- **Intervals:** passed as milliseconds and converted with `NUMTODSINTERVAL`.

### Partial indexes
Oracle has none. Function-based indexes give the same effect, because a key that is entirely NULL is not indexed.
- **Claim index:** `CASE WHEN STATUS IN (0, 1) THEN AVAILABLE_AT END`.
- **Cleanup index:** the same form on `DELIVERED_AT` for status 2.
- **Dead-letter index:** the same form on `DEAD_LETTERED_AT` for status 3.
- **Queries must repeat the exact same expression** for the optimizer to use the index. Tests pin the plans, as T-03 does
  for PostgreSQL.

### Claim
`FOR UPDATE ... SKIP LOCKED` cannot be combined with `FETCH FIRST` or `ROWNUM` without returning too few rows, so the claim
is **one anonymous PL/SQL block**, autocommitted as one statement:
1. It opens a cursor `SELECT ... ORDER BY <claim expression>, ID FOR UPDATE SKIP LOCKED` and fetches row by row. Rows are
   locked as they are fetched.
2. It stops at the batch size, or at the payload budget (ADR 0004): `DBMS_LOB.GETLENGTH`, with the first row always taken.
3. It updates each fetched row: lock token, worker, attempt + 1 (invariant 10), and a lease ending at `now + lease`.
4. It returns the claimed IDs in claim order and a cursor over the claimed rows.

### Guarded updates, acknowledgements and inserts
- **State changes after a claim:** single statements guarded by `WHERE ID = :id AND LOCK_TOKEN = :token` (invariant 4).
- **Batched acknowledgements (ADR 0003):**
  - one PL/SQL `FORALL` over bound arrays;
  - `SQL%BULK_ROWCOUNT` reports which items changed;
  - items are sorted by their stored byte order (BA-07).
- **Inserts:** always on the caller's connection and transaction (invariants 1 and 2). Several rows go in **one statement**,
  so they are stored all-or-nothing without DbRelay ever beginning a transaction on the caller's connection.
- **`transaction: null` on a connection that has a transaction:** ODP.NET runs the command in that transaction. This is
  the same documented behaviour as PostgreSQL, and a test pins it.

### Schema creation
- **Before 23ai there is no `IF NOT EXISTS`,** so each object is created in a PL/SQL block that ignores ORA-00955 ("name
  already used"). The script stays idempotent and non-destructive.
- **Oracle DDL commits implicitly,** so `EnsureCreatedAsync` runs on DbRelay's own connection, never the caller's.
- **Concurrent instances creating the schema** are serialized with `DBMS_LOCK.REQUEST`. That needs `EXECUTE ON DBMS_LOCK`;
  without it, creation falls back to tolerating ORA-00955.

### To be verified empirically before release
These go into the provider tests:
- array binding of `BLOB` values;
- the atomicity of the multi-row insert when one row fails;
- the claim block under 8 concurrent claimers, with no row claimed twice;
- the index plans of the claim, cleanup and statistics queries.

## Consequences
- **Two more packages** to release and maintain, with a CI job per database version.
- **The Oracle provider is more PL/SQL-heavy than the others.** Its code review must cover cursor and lock behaviour
  specifically.
- **MongoDB users stay unsupported** until there is demand.
