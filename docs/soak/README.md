# Soak and chaos test results

The harness is `tests/DbRelay.Soak`. One coordinator process does the following:

- runs producers that commit (and roll back 20% of) business transactions containing outbox messages;
- starts several worker processes, each running a DbRelay dispatcher;
- applies chaos while the load runs;
- after a drain, checks the invariants against the database.

The chaos is:

- hard-killing workers every 20–60 s (70%);
- stopping workers gracefully (30%);
- terminating every worker database session every 45–90 s.

Worker `w0` is never killed, so its memory, threads and handles can be tracked over the whole run.

```powershell
dotnet run -c Release --project tests/DbRelay.Soak -- coordinator --provider PostgreSql --minutes 120 --workers 3 --rate 40 --report soak.md
```

Oracle and MySQL soaks run on GitHub Actions (`.github/workflows/soak.yml` and `soak-mysql.yml`), with the database in a service
container. GitHub limits a job to 6 hours, so their load phase is 5 h 20 min. For Oracle and MySQL, workers connect as a throwaway
database user whose sessions the chaos loop kills.

The connection strings come from `DBRELAY_TEST_POSTGRES` / `DBRELAY_TEST_SQLSERVER`. Each run uses its own `soak_*` schema and
drops it afterwards (pass `--keep` to keep it).

## Runs

| Date | Provider | Duration | Messages committed | Hard kills / graceful stops | Sessions terminated | Lost | Duplicates | p99 latency | Result | Report |
|---|---|---|---|---|---|---|---|---|---|---|
| 2026-11-22 | PostgreSQL 17.5 | 2 h | 300,378 | 112 / 53 | 3,175 | 0 | 1 | 859 ms | **PASS 9/9** | [report](2026-11-22-postgresql-120min.md) |
| 2026-11-22 | SQL Server 2022 Express | 1 h | 141,024 | 49 / 31 | 1,130 | 0 | 6 | 767 ms | **PASS 9/9** | [report](2026-11-22-sqlserver-60min.md) |
| 2026-11-22 | PostgreSQL 17.5 (BA fixes) | 30 min | 76,624 | 26 / 16 | 778 | 0 | 0 | 808 ms | **PASS 9/9** | [report](2026-11-22-postgresql-30min-fixed.md) |
| 2026-11-22 | SQL Server 2022 Express (0.2 backlog) | 30 min | 67,544 | 30 / 13 | 514 | 0 | 0 | 861 ms | **PASS 9/9** | [report](2026-11-22-sqlserver-30min-0.2-backlog.md) |
| 2026-11-22 | PostgreSQL 17.5 (0.2 backlog) | 30 min | 71,522 | 29 / 15 | 831 | 0 | 0 | 1,397 ms | **PASS 9/9** | [report](2026-11-22-postgresql-30min-0.2-backlog.md) |
| 2026-11-27 | Oracle 23ai Free (GitHub Actions) | 5 h 20 min | 38,797 (+9,909 rolled back) | 337 / 121 | 5,130 | 0 | 0 | 349 ms | **PASS 9/9** | [report](2026-11-27-oracle-320min.md) |

The "0.2 backlog" runs use the code after these changes:
- the payload budgets (CR-18);
- the multi-row PostgreSQL insert;
- bounded batch leadership (BA-05);
- the SQL Server deadlock fixes (BA-07).

In every run, the long-lived worker's memory, threads and handles stayed flat. No unexpected errors (event 1011) were logged.

All runs were on one Windows 10 laptop, with the databases on the same machine. Delivery is at-least-once, so the duplicate
deliveries in the reports are expected. They come from workers killed between the handler finishing and its acknowledgement
being written. They are not failures.

The maximum latency (10.5–13.4 s) is a message held by a hard-killed worker. It is claimed again only after its 10 s lease
expires, which is the designed recovery path.

Code version: the 2-hour PostgreSQL run used the batched-acknowledgement build **before** the BA review fixes
(`CODE_REVIEW_REPORT.md`, BA-01..BA-12). Those fixes include BA-03, which changed both providers' batch statements to return
(id, token) pairs. The runs listed after it use the fixed code.
