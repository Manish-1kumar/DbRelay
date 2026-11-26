# Implementation Tracker

Statuses: NOT STARTED · IN PROGRESS · IMPLEMENTED - NOT VERIFIED · TESTED · REVIEWED · COMPLETED · BLOCKED

"Tests (actual)" are results re-run by the principal architect (2026-11-21, all four TFMs, nothing skipped), not just what the
implementing agent reported.

| ID | Task | Owner | Status | Tests (actual) | Review | Limitations |
|---|---|---|---|---|---|---|
| T1.x | Environment, agent team, competitor research | Architect, research | COMPLETED | n/a | Key claims re-verified | USPTO not queried |
| T2.x | Differentiation, architecture, risks, roadmap | Architect | COMPLETED | n/a | Owner-approved (D1 amended) | |
| T3.1 | Decisions D1–D10 (ADR 0001) | Owner + architect | COMPLETED | n/a | | |
| T4.1 | Build infrastructure (CPM, per-TFM pins, warnings as errors, analyzers, MTP) | Architect | COMPLETED | 0 warnings/0 errors; `dotnet format` clean | Reviewed | SDK 11 `sln add` bug, so the slnx is maintained by hand |
| T4.2 | DbRelay.Abstractions | Architect | COMPLETED | via unit + conformance | Reviewed (CR-11 doc fix, CR-22 guard) | SPI/interface evolution (CR-24) open before 1.0 |
| T4.3 | Core: options, serializer, type registry, UUIDv7, Outbox, Admin, SPI | Architect | COMPLETED | Unit 348/348 | Reviewed; CR-01/02/12/13/14/20/22 fixed | |
| T4.4 | Storage conformance suite | Architect | COMPLETED | 37 tests × 3 configurations (PG, SQL RCSI off/on) | Reviewed; T-01/T-06 fixed | |
| T5.1 | PostgreSQL provider | postgresql-engineer | COMPLETED | 300/300 | Reviewed; CR-03 fixed | Tested on PG 17.5 only (CI matrix adds 15/16/18) |
| T6.1 | Dispatcher, retry, maintenance, health, diagnostics | Architect | REVIEWED | Unit dispatcher tests + integration | CR-04/05/06/09/10/19/25 fixed; follow-up audit in progress | |
| T7.1 | Crash recovery and dead-letter | Architect + QA | COMPLETED | Process-kill + crash-loop integration tests | Reviewed | |
| T8.1 | SQL Server provider | sqlserver-engineer | COMPLETED | 372/372 | Reviewed | K6 (one unreproduced overload failure); SQL Server 2022 Express only; package deferred to 0.2.0 |
| T9.1 | Integration, crash, concurrency, interruption, security tests | qa-reliability | COMPLETED | 68/68 (×2 consecutive) | Reviewed; 2 tests revised for CR-05/CR-06 | Integration suite runs on PostgreSQL only |
| T10.1 | Benchmarks | Architect | COMPLETED | `docs/benchmarks.md` | n/a | Single-machine baseline |
| T11.1 | Docs and samples | docs-devex | COMPLETED | 3 samples built and run; snippets compiled | Architect spot-check and post-fix updates | NuGet README relative links (needs the repository URL) |
| T12.1 | CI workflow, Dependabot, packaging | Architect | COMPLETED | First GitHub Actions run green: 1,140/1,140, 0 skipped; PG 15/16/18 jobs green (run 44996266454) | | |
| T13.1 | Production-readiness audit | code-reviewer | COMPLETED | 1,140 test runs after FU fixes, 0 failed | Initial review, follow-up audit (FU-01..FU-10) and resolution logs in CODE_REVIEW_REPORT.md | No independent re-audit of the FU fixes |
| T15.1 | Batched acknowledgements + `IOutboxBatchStore` (ADR 0003) | Architect | REVIEWED | Unit 416/416 (104 × 4 TFMs); conformance batch tests on PG and SQL (RCSI off/on); interleaved A/B throughput (`docs/benchmarks.md` §3) | BA review: 0 Critical/High; BA-01..BA-12 fixed or accepted; BA-02 and the lost-wake-up fix mutation-checked | BA-05 bounded hand-off and BA-07 lock stress test deferred |
| T16.1 | Soak/chaos harness and long runs | Architect | COMPLETED | PostgreSQL 2 h, SQL Server 1 h, PostgreSQL 30 min (final code): PASS 9/9 each, 0 lost (`docs/soak/`) | | Single machine; SQL Server Express |
| T14.1 | Release 0.1.0 | Architect | COMPLETED | Publish run 45692836402; installed from nuget.org and delivered end to end | | `DbRelay.SqlServer` not packed (0.2.0) |
| T17.1 | 0.2.0: `DbRelay.SqlServer` package, 0.1.0 package-validation baseline, SQL Server 2019/2025 CI jobs | Architect | TESTED | Local: build 0 warnings; 4 packages packed; baseline validation mutation-checked (a removed public member fails the pack). PR #1 CI run 45740606055: 1,392/1,392, 0 skipped; SQL Server 2019 (15.0.4510.2) and 2025 (17.0.5065.9) provider suites 122/122 each, 0 skipped | | Azure SQL untested (K7) |

## Bugs found and fixed

| ID | Found by | Description | Status |
|---|---|---|---|
| B1 | postgresql-engineer (conformance) | Bulk requeue/delete could exceed maxCount (subquery re-scan) | Fixed |
| B2 | postgresql-engineer | Conformance base lacked logging, so ValidateOnBuild failed | Fixed |
| B3 | Architect self-review | Busy dispatcher reported as stalled by the health check | Fixed |
| B4 | sqlserver-engineer (plans) | Proposed single-statement SQL Server claim locked every due row | Fixed by design change (ADR 0002) |
| BUG-1 | qa-reliability | Unstorable error text (NUL or split surrogate) left poison messages redelivered every lease | Fixed |
| CR-01 | code-reviewer | One malformed stored row stalled its claim batch forever (High) | Fixed |
| CR-02..05, T-01 | code-reviewer | Medium findings | Fixed |
| T-06 | code-reviewer | Mojibake test literals (architect's PowerShell 5.1 edits) | Fixed |

## File ownership

| Path | Owner |
|---|---|
| src/DbRelay.Abstractions, src/DbRelay | principal-architect / core-developer / dispatcher-engineer |
| src/DbRelay.PostgreSql, tests/DbRelay.PostgreSql.Tests | postgresql-engineer |
| src/DbRelay.SqlServer, tests/DbRelay.SqlServer.Tests | sqlserver-engineer |
| tests/DbRelay.Storage.Conformance | principal-architect |
| tests/DbRelay.IntegrationTests, tests/DbRelay.CrashWorker, TEST_REPORT.md | qa-reliability (architect revised two tests and appended an addendum after review fixes) |
| README, docs (guides), samples, CHANGELOG, CONTRIBUTING | docs-devex (architect updated for post-review behaviour changes) |
| CODE_REVIEW_REPORT.md | code-reviewer (architect appended the resolution log only) |
| docs/decisions, architecture docs, benchmarks, CI | principal-architect |
