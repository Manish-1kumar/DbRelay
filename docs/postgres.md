# PostgreSQL guide

Supported: PostgreSQL 15 and later. **Tested: 17.5 only** (see [KNOWN_ISSUES.md](../KNOWN_ISSUES.md), K7). Provider:
`DbRelay.PostgreSql`, built on Npgsql.

## Schema

`EnsureCreatedAsync()` and `GetCreateScript()` use the script embedded in the provider:
[`src/DbRelay.PostgreSql/Scripts/001_create_outbox.sql`](../src/DbRelay.PostgreSql/Scripts/001_create_outbox.sql).
With the defaults (`Schema = "dbrelay"`, `TableName = "outbox_messages"`) it creates:

| Object | Purpose |
|---|---|
| schema `dbrelay` | `CREATE SCHEMA IF NOT EXISTS` |
| table `dbrelay.outbox_messages` | the outbox (columns below) |
| index `ix_outbox_messages_claim` on `(available_at) WHERE status IN (0, 1)` | claiming: pending rows and leased rows share one range |
| index `ix_outbox_messages_cleanup` on `(delivered_at) WHERE status = 2` | retention cleanup |
| index `ix_outbox_messages_dead` on `(dead_lettered_at) WHERE status = 3` | dead-letter inspection and bulk requeue |
| table `dbrelay.schema_version` | one row per outbox table: `('outbox:outbox_messages', 1)` |

| Column | Type | Notes |
|---|---|---|
| `id` | `uuid` primary key | UUIDv7 by default (time-ordered, good B-tree locality), or your `EnqueueOptions.MessageId` |
| `message_type` | `text` | logical name, never used to create types |
| `destination` | `text` null | routing hint |
| `payload` | `bytea` | exact bytes (ADR 0001, D4). Inspect JSON with `convert_from(payload, 'UTF8')::jsonb` |
| `content_type` | `text` | for example `application/json` |
| `headers` | `jsonb` null | custom headers and `traceparent` / `tracestate` |
| `correlation_id`, `causation_id` | `text` null | |
| `status` | `smallint` | 0 Pending, 1 Processing, 2 Delivered, 3 DeadLettered (check constraint) |
| `attempt_count` | `integer` | incremented at claim time |
| `created_at`, `scheduled_at`, `available_at` | `timestamptz` | `available_at` is both the due time and the lease expiry |
| `lock_token` | `uuid` null | fences every update after a claim |
| `locked_by` | `text` null | worker ID, for diagnostics only |
| `last_attempt_at`, `delivered_at`, `dead_lettered_at` | `timestamptz` null | |
| `last_error` | `text` null | sanitized, at most 2,000 characters |

Index and constraint names are derived from the table name. If `ix_<table>_<purpose>` would exceed PostgreSQL's 63-byte
identifier limit, the name is shortened deterministically with a hash, so two indexes can never collide. Several outbox tables
can share one schema.

The script is idempotent and non-destructive: every statement is `IF NOT EXISTS` or `ON CONFLICT DO NOTHING`, and nothing
is altered or dropped. `EnsureCreatedAsync` runs it in one transaction under a transaction-scoped advisory lock, so instances
starting at the same moment do not race on the system catalogs.

## Running the script through a migration tool

If the application's database user must not run DDL, generate the script once and commit it to your migrations
(Flyway, DbUp, Grate, Sqitch, a DBA ticket and so on). `GetCreateScript()` returns the script with your configured names filled
in. It does not connect to the database:

```csharp
var services = new ServiceCollection()
    .AddLogging()
    .AddDbRelay(relay =>
    {
        relay.UsePostgreSql("Host=unused", pg => pg.Schema = "messaging");
        relay.DisableDispatcher();
    })
    .BuildServiceProvider();

File.WriteAllText("V7__dbrelay_outbox.sql", services.GetRequiredService<IOutboxSchemaManager>().GetCreateScript());
```

Alternatively, copy the embedded `.sql` file and replace its `{{schema}}`, `{{table}}`, `{{ck_status}}`, `{{ix_claim}}`,
`{{ix_cleanup}}` and `{{ix_dead}}` placeholders by hand. The defaults are listed in the file header. With custom table names,
prefer `GetCreateScript()`, because it applies the same name shortening as the runtime. The script needs no special settings;
`psql -f` runs it as is. Then simply never call `EnsureCreatedAsync` in the application.

Future schema versions will ship as new, additive, numbered scripts recorded in `schema_version`.

## Permissions

| Operation | Needs |
|---|---|
| Enqueue (your connection) | `USAGE` on the schema, `INSERT` on the outbox table |
| Dispatcher, cleanup, admin, health check (DbRelay's data source) | `USAGE` on the schema; `SELECT, INSERT, UPDATE, DELETE` on the outbox table (`INSERT` only for `EnqueueStandaloneAsync`) |
| `EnsureCreatedAsync` | `CREATE` on the database (or an existing schema you own), plus the right to create tables and indexes in it |

A least-privilege setup, with the owner creating objects through migrations:

```sql
GRANT USAGE ON SCHEMA dbrelay TO app_user;
GRANT SELECT, INSERT, UPDATE, DELETE ON dbrelay.outbox_messages TO app_user;
```

The dispatcher, admin API and health check use the connection string or data source you give `UsePostgreSql`. Your enqueue
calls use your own connection. The two can be different roles.

## Queue-table maintenance: autovacuum and bloat

An outbox is a queue: every message is inserted, updated two or more times (claim, then outcome) and finally deleted by
retention cleanup. Every update changes indexed columns (`status`, `available_at`, `delivered_at`), so PostgreSQL cannot use
HOT updates, and each change leaves a dead tuple in the heap and in the indexes. The claim query walks the claim index from the
oldest `available_at`, so dead entries at the front of that index cost every poll. The recommendations below are standard
PostgreSQL queue-table practice. **They are not measured by DbRelay's test suite.** Tune them with your own monitoring.

- **Vacuum the table more often than the global default.** The default `autovacuum_vacuum_scale_factor` (20% of the table) is
  far too lazy for a queue. Per-table settings, as a starting point:

  ```sql
  ALTER TABLE dbrelay.outbox_messages SET (
      autovacuum_vacuum_scale_factor = 0.0,
      autovacuum_vacuum_threshold = 5000,
      autovacuum_analyze_scale_factor = 0.0,
      autovacuum_analyze_threshold = 5000,
      autovacuum_vacuum_cost_delay = 0
  );
  ```

- **Avoid long-running transactions** on the same cluster, including idle-in-transaction sessions and long reporting queries
  on a primary or on a replica with `hot_standby_feedback`. They hold back the vacuum horizon, so dead tuples cannot be removed,
  and claims slow down as they accumulate. Set `idle_in_transaction_session_timeout`.
- **Keep retention short.** `MaintenanceOptions.DeliveredRetention` defaults to 1 day. A smaller table keeps vacuum cheap.
  Dead letters are never deleted automatically: requeue or purge them deliberately.
- **Watch the table:**

  ```sql
  SELECT n_live_tup, n_dead_tup, last_autovacuum, autovacuum_count
  FROM pg_stat_user_tables WHERE schemaname = 'dbrelay' AND relname = 'outbox_messages';
  ```

  A `n_dead_tup` that keeps growing, or a rising `dbrelay.claim.duration` histogram, means vacuum is not keeping up.
- **Rebuild bloated indexes online** if they still grow despite vacuum: `REINDEX INDEX CONCURRENTLY dbrelay.ix_outbox_messages_claim;`
- **Statistics are a full count.** `GetStatisticsAsync`, the gauges and threshold-based health checks count rows by status
  (K12). On very large tables, keep `StatisticsInterval` at a minute or more, and probe health thresholds infrequently.

Partitioning the outbox table is not supported in 0.1.

## Connection usage

- The dispatcher uses short autocommitted statements: one for each claim and one for each outcome. No connection is held while
  handlers run, so a few pooled connections serve many concurrent deliveries.
- `CommandTimeout` (default 30 s) applies to every DbRelay command, including inserts on your connection.
- Profiling wrappers around `NpgsqlConnection` are rejected (K10). Pass the inner connection and transaction.
- Behind PgBouncer, transaction pooling works for DbRelay's statements, because each claim and outcome is a single
  autocommitted statement and no session state is used. **This is not tested.** `EnsureCreatedAsync` takes a
  transaction-scoped advisory lock, which is also compatible with transaction pooling.

## Useful queries

```sql
-- What is due or leased right now?
SELECT id, message_type, status, attempt_count, available_at, locked_by, last_error
FROM dbrelay.outbox_messages WHERE status IN (0, 1) ORDER BY available_at LIMIT 50;

-- Dead letters by type.
SELECT message_type, count(*), max(dead_lettered_at) FROM dbrelay.outbox_messages WHERE status = 3 GROUP BY message_type;

-- Look at a JSON payload.
SELECT convert_from(payload, 'UTF8')::jsonb FROM dbrelay.outbox_messages WHERE id = '...';
```

Prefer `IOutboxAdmin` for changes (requeue, purge). Never edit `status` or `lock_token` by hand while dispatchers are running.
