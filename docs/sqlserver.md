# SQL Server guide

Supported: SQL Server 2019 and later, and Azure SQL Database. **Tested: SQL Server 2022 Express (16.0.1000.6) on Windows only**
(K7); 2019 and Azure SQL are not tested yet. Provider: `DbRelay.SqlServer`, built on Microsoft.Data.SqlClient.

## Schema

The embedded script is [`src/DbRelay.SqlServer/Scripts/001_create_outbox.sql`](../src/DbRelay.SqlServer/Scripts/001_create_outbox.sql).
With the defaults (`Schema = "dbrelay"`, `TableName = "outbox_messages"`) it creates:

| Object | Purpose |
|---|---|
| schema `[dbrelay]` | |
| table `[dbrelay].[outbox_messages]` | clustered primary key `pk_outbox_messages` on `[sequence] bigint IDENTITY` |
| `ux_outbox_messages_id` | unique nonclustered on `[id]`: lookups and duplicate-ID rejection |
| `ix_outbox_messages_claim` | `([available_at]) INCLUDE ([status]) WHERE [status] IN (0, 1)`: claiming |
| `ix_outbox_messages_delivered` | `([delivered_at]) WHERE [status] = 2`: retention cleanup |
| `ix_outbox_messages_dead_lettered` | `([dead_lettered_at]) INCLUDE ([message_type]) WHERE [status] = 3`: dead letters and bulk requeue |
| table `[dbrelay].[schema_version]` | one row per outbox table, `('outbox:outbox_messages', 1)` |

Design choices:

- **Clustered on an identity column, not on the GUID.** Message IDs, even UUIDv7, do not sort sequentially in
  `uniqueidentifier` order, so a clustered GUID key would cause page splits.
- **Payload is `varbinary(max)`**, byte-faithful. Headers are JSON text in `nvarchar(max)`. `last_error` is `nvarchar(2000)`.
- **Times are `datetime2(3)` UTC** from `SYSUTCDATETIME()`, truncated (not rounded) to milliseconds, so a stored "now" is never
  in the future.
- **Batch limits.** An insert statement carries at most 200 rows (2,000 of SQL Server's 2,100 parameters), and larger
  `EnqueueManyAsync` calls are split into several statements. Claims are capped at 1,000 rows and bulk requeue and delete at
  1,000 rows per statement, well below the 5,000-lock escalation threshold.

The script is idempotent (`IF ... IS NULL CREATE ...`) and never drops or alters anything. `EnsureCreatedAsync` runs it in one
transaction holding an exclusive `sp_getapplock` on the schema, so instances starting at the same time serialize instead of racing.

## ANSI_NULLS, QUOTED_IDENTIFIER and `sqlcmd -I`

The claim, cleanup and dead-letter indexes are **filtered indexes**. SQL Server requires `QUOTED_IDENTIFIER ON` and
`ANSI_NULLS ON` to create a filtered index and to modify rows in a table that has one. Otherwise it raises Msg 1934:

```text
Msg 1934, Level 16, State 1
CREATE INDEX failed because the following SET options have incorrect settings: 'QUOTED_IDENTIFIER'. ...
```

- The script starts with `SET ANSI_NULLS ON; SET QUOTED_IDENTIFIER ON;` in its own batch. Run as a whole, it works in `sqlcmd`
  with or without `-I`. This was verified on SQL Server 2022 by running it twice, both ways.
- **`sqlcmd` defaults `QUOTED_IDENTIFIER` to OFF** unless you pass `-I`. If your migration tool splits the script, drops the
  first batch, or runs fragments in separate sessions, the `CREATE INDEX` statements fail with the error above. This was
  reproduced. Always pass `-I`:

  ```powershell
  sqlcmd -S <server> -d <database> -E -C -I -b -i 001_create_outbox.sql
  ```

- At runtime, Microsoft.Data.SqlClient connections default to both settings ON. If your application turns
  `QUOTED_IDENTIFIER` or `ANSI_NULLS` OFF on a connection, for example in a stored procedure created with the wrong settings,
  outbox inserts on that connection will fail. DbRelay deliberately issues no `SET` statements on your connection, because it
  must not change your session.

## Running the script through a migration tool

The script uses `GO` batch separators. That suits sqlcmd, SSMS and migration tools that understand `GO`. Generate it with your
configured names:

```csharp
var services = new ServiceCollection()
    .AddLogging()
    .AddDbRelay(relay =>
    {
        relay.UseSqlServer("Server=unused", sql => sql.Schema = "messaging");
        relay.DisableDispatcher();
    })
    .BuildServiceProvider();

File.WriteAllText("V7__dbrelay_outbox.sql", services.GetRequiredService<IOutboxSchemaManager>().GetCreateScript());
```

Alternatively, copy the embedded file and replace `{{schema}}` and `{{table}}`. Then never call `EnsureCreatedAsync` in the
application.

## READ_COMMITTED_SNAPSHOT (RCSI)

RCSI is ON by default in Azure SQL Database and often enabled on-premises. DbRelay works with it **on and off**. The whole
provider conformance suite runs twice, once against a database with RCSI off and once with it on, and the lock-skipping
claim test (`Claim_skips_rows_locked_by_an_in_flight_claim_without_blocking`) runs in both modes.

Why it is safe: the claim reads with explicit `UPDLOCK, READPAST, ROWLOCK` hints, which take real update locks and skip
locked rows whatever the database's snapshot setting. Two claimers therefore never receive the same row, and an uncommitted
enqueue is never claimed. Outcome updates are guarded by the lock token, so correctness does not depend on the isolation
level at all.

Your own business transaction can use any isolation level. The outbox insert simply runs inside it.

## Index hints (ADR 0002) and what they mean for DBAs

DbRelay's claim, cleanup and bulk-requeue statements name its indexes (`INDEX([ix_<table>_claim])`, and so on) and use
`FORCESEEK`. [ADR 0002](decisions/0002-sqlserver-two-phase-claim.md) explains why. Without the hints, SQL Server chose plans
that read and U-locked **every** due row before applying `TOP`: 500 rows locked to claim 100, a starved concurrent claimer,
and a risk of lock escalation to a table lock. With the two-phase claim and the hints, actual plans read and lock exactly the
batch.

Operational consequences:

- **Do not drop, rename or disable DbRelay's indexes.** A statement whose hinted index is missing **fails** (SQL Server reports
  that the index named in the hint does not exist) rather than silently degrading (K8). Claims, cleanup and requeue then stop, the dispatcher logs claim
  failures (event 1002) and the health check reports Degraded. Re-run the create script to restore them.
- **Index maintenance is fine.** Rebuilding or reorganising the indexes, including online, keeps their names.
- **Do not add filtered indexes with different predicates expecting DbRelay to use them.** It will not.
- The provider test `Claim_cleanup_requeue_and_statistics_plans_use_the_filtered_indexes` checks the plans.

## Permissions

| Operation | Needs |
|---|---|
| Enqueue (your connection) | `INSERT` on the outbox table |
| Dispatcher, cleanup, admin, health check | `SELECT, INSERT, UPDATE, DELETE` on the outbox table |
| `EnsureCreatedAsync` | `CREATE SCHEMA` (if the schema does not exist), `CREATE TABLE`, `ALTER` on the schema, and `SELECT, INSERT` on `schema_version` |

```sql
GRANT SELECT, INSERT, UPDATE, DELETE ON [dbrelay].[outbox_messages] TO [app_user];
```

## Connections and transactions

- `UseSqlServer(connectionString)` creates a new `SqlConnection` for each DbRelay operation (pooled by SqlClient as usual).
  `UseSqlServer(sp => new SqlConnection(...))` lets you build connections yourself, for example with an Azure AD token. The
  factory must return a **new, unopened** connection each time. DbRelay opens and disposes it, and refuses one that is already
  open.
- `CommandTimeout` (default 30 s, range 1 s to 1 h) applies to every DbRelay statement, including the insert on your
  connection.
- **`EnqueueManyAsync` with no transaction and more than 200 messages** needs several statements. To stay all-or-nothing,
  DbRelay then begins and commits **its own** transaction on your connection (K9). If your connection is enlisted in an
  ambient `TransactionScope`, pass an explicit transaction or keep batches at 200 or fewer.
- Wrapped connections (for example MiniProfiler's `ProfiledDbConnection`) are rejected (K10). Pass the inner `SqlConnection`
  and `SqlTransaction`.
- Statistics (`GetStatisticsAsync`, gauges, threshold health checks) read with `READPAST`, so rows locked at that instant are
  not counted. Treat the numbers as approximate under load.

## Useful queries

```sql
-- What is due or leased right now?
SELECT TOP (50) [id], [message_type], [status], [attempt_count], [available_at], [locked_by], [last_error]
FROM [dbrelay].[outbox_messages] WHERE [status] IN (0, 1) ORDER BY [available_at];

-- Look at a JSON payload.
SELECT CAST([payload] AS varchar(max)) FROM [dbrelay].[outbox_messages] WHERE [id] = '...';
```

`CAST(... AS varchar(max))` shows the payload correctly only for ASCII content; non-ASCII UTF-8 characters are garbled in
the output (the stored bytes are unaffected). Prefer `IOutboxAdmin` for changes, and never edit `status` or `lock_token` by hand while
dispatchers are running.
