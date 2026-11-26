# Known Issues

| ID | Issue | Impact | Status |
|---|---|---|---|
| K1 | Testcontainers fallback path never exercised (local Docker off; CI deliberately uses shared service containers via `DBRELAY_TEST_*`) | Only matters for contributors who rely on Docker locally | Open: low priority |
| K2 | Local PostgreSQL credentials unknown | Not needed: tests use a private cluster on port 55432 created from the installed PG 17 binaries | Mitigated |
| K3 | Local .NET SDK 10.0.401 / runtime 10.0.12 behind latest 10.0.15 | Dev machine only; build uses a user-local .NET 11 SDK plus patched 8/9/10 runtimes | Mitigated |
| K4 | Project agents were not loaded in the original Claude Code session | Early delegation used general-purpose subagents with role charters | Resolved (agents available since 2026-11-19) |
| K5 | No trademark clearance performed for "DbRelay" | Naming risk before public 1.0 | Open: manual USPTO/EUIPO/WIPO check recommended |
| K6 | One all-TFM SQL Server test run (2026-11-20, 11m46s, overlapping other test processes) had 5 failures of 364; which tests failed was not captured | Probably timeouts under overload on SQL Server Express (2 cores). A real concurrency bug is not ruled out | Not reproduced in 4 later full runs (364/364, 372/372 ×3, ~45–80 s each). Open: watch in CI |
| K7 | Tested: PostgreSQL 15.20, 16, 17.5, 18 (Linux CI + Windows); SQL Server 2019 (15.0.4510), 2022 (Express on Windows, Developer container on Linux CI) and 2025 (17.0.5065) | Azure SQL Database unverified | Open (SQL Server 2019 and 2025 added to CI in 0.2.0) |
| K8 | SQL Server: index hints mean DbRelay statements fail if a DBA drops a DbRelay index | Operational | By design (ADR 0002); documented |
| K9 | SQL Server: an `EnqueueManyAsync` with **no** caller transaction and more than 200 messages begins its own transaction on the caller's connection (to stay all-or-nothing) | Could conflict with an ambient `TransactionScope` enlistment | Open: document; consider rejecting instead |
| K10 | Wrapped/profiled connections (e.g. MiniProfiler `ProfiledDbConnection`) are rejected by provider type validation | Users of connection wrappers can't enqueue on the wrapper | Open: document; consider unwrapping support later |
| K11 | Handler exception messages are stored (truncated) in `last_error` and logged | If handlers put payload data or secrets in exception messages, it is persisted | Document in reliability/security guidance |
| K12 | The exact Delivered count in `GetStatisticsAsync` is proportional to retained delivered rows | PostgreSQL: index-only scan, about 0.2–0.3 s per 2M retained rows on a laptop (after the CR-03 fix; before: full heap scan). Runs only on dispatcher instances | Mitigated; documented in `docs/configuration.md` |
| K13 | Test processes that are force-killed leave their randomly named `t_*` test schemas behind | Test databases only | **Resolved:** schema names embed their creation time, and each test fixture drops test schemas more than 6 hours old (never a live parallel run's). Manual cleanup is still documented in CONTRIBUTING |
| K14 | Public interfaces can't gain members without breaking implementers (CR-24) | Was blocking batched acknowledgements | **Resolved** by ADR 0003: new storage capabilities are optional interfaces (`IOutboxBatchStore`). `IOutbox`/`IOutboxAdmin` remain pre-1.0 and may still change |
| K15 | Per-message acknowledgement round trip was the measured throughput bottleneck | Was a ~3.5–3.9k msg/s ceiling on the benchmark laptop | **Resolved** by batched acknowledgements (up to about 2.8× under load; `docs/benchmarks.md` §3). Residual: a single SQL Server dispatcher at concurrency 8 measured a 5–8% lower median (within noise) |
