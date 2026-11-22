# Release Checklist: 0.1.0 (release candidate)

_Last updated: 2026-11-21 · Packages in this release: `DbRelay.Abstractions`, `DbRelay`, `DbRelay.PostgreSql`. `DbRelay.SqlServer` is built and
tested but not packed until 0.2.0 (project brief §15)._

Legend: ✅ done and verified · ⏳ done locally, awaiting an external step · ❌ not done · 🔒 requires owner approval

## Definition of Done (project brief §20)

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Architecture documented | ✅ | `docs/architecture.md`, `docs/architecture-proposal.md`, ADR 0001–0002 |
| 2 | Competitor analysis complete | ✅ | `docs/competitor-analysis.md` (sourced; key claims re-verified) |
| 3 | Public APIs stable and documented | ✅ (pre-1.0) | XML docs on all public members (build enforces). Open decision before 1.0: SPI evolution (CR-24) |
| 4 | Core library builds | ✅ | `dotnet build DbRelay.slnx -c Release`: 0 warnings, 0 errors, 4 TFMs |
| 5 | PostgreSQL persistence works | ✅ | 300/300 provider tests |
| 6 | Transaction integration verified | ✅ | Conformance transaction tests (commit/rollback/ownership/validation) + integration atomicity (200 random commit/rollback) |
| 7 | Background delivery works | ✅ | Dispatcher unit tests, integration, sample run |
| 8 | Custom handlers work | ✅ | `CustomHandler.Sample` (webhook 200/429/422) run against PostgreSQL |
| 9 | Retry behaviour works | ✅ | Retry schedule measured on the DB clock within jitter bounds |
| 10 | Dead-letter handling works | ✅ | Exhaustion, permanent failure, crash loop, requeue tests |
| 11 | Multiple worker instances work safely | ✅ | 4 hosts × 2,000 messages, 0 duplicates; 8 concurrent claimers × 300, no double claim (both providers) |
| 12 | Lease recovery works | ✅ | Conformance reclaim tests + process-kill test |
| 13 | Crash scenarios tested | ✅ | Real child-process kill (redelivery after lease) and crash-loop → dead-letter |
| 14 | At-least-once documented | ✅ | README, `docs/reliability.md` (guarantee → test table) |
| 15 | Security review complete | ✅ | `CODE_REVIEW_REPORT.md`: initial review, follow-up audit, and both resolution logs |
| 16 | Performance benchmarks exist | ✅ | `docs/benchmarks.md` (BenchmarkDotNet + throughput sweep, environment recorded) |
| 17 | Documentation accurate | ✅ | Snippets copied from compiling samples or compiled in a scratch project; docs updated after review fixes |
| 18 | Sample applications run | ✅ | All 3 run against real DBs (docs engineer); PostgreSQL sample re-run after the fixes |
| 19 | CI passes | ✅ | First GitHub Actions run (ubuntu, service containers) green: 1,140/1,140 tests, 0 skipped; PostgreSQL 15.20/16/18 compatibility jobs green (94 tests each, 0 skipped). Run 44996266454 |
| 20 | NuGet packages build and pass validation | ✅ | `dotnet pack` → 3 packages + symbols; contents and dependency groups inspected; package validation enabled |
| 21 | No unresolved Critical/High findings | ✅ | Initial review: 1 High (CR-01). Follow-up audit found it partly fixed (FU-01, High) plus 1 Medium. Both fixed; FU-01 and FU-04 fixes mutation-checked. The FU fixes have not had an independent re-audit |
| 22 | Release checklist complete | ⏳ | This file; the open items below |

## Before publishing (owner actions)

| Item | Status |
|---|---|
| Repository created: https://github.com/Manish-1kumar/DbRelay (private); set as the default package RepositoryUrl/ProjectUrl | ✅ |
| Push to GitHub and confirm CI is green, including the PostgreSQL 15/16/18 matrix | ✅ |
| Reserve the `DbRelay.*` ID prefix on nuget.org | 🔒 |
| Manual trademark search (USPTO/EUIPO/WIPO, classes 9/42) for "DbRelay" (K5) | 🔒 |
| Fill the `TODO(maintainer)` contact placeholders in CODE_OF_CONDUCT.md and SECURITY.md; enable GitHub private vulnerability reporting | 🔒 |
| Package-specific README with absolute links (`src/PACKAGE_README.md`, packed as README.md; 0 relative links verified) | ✅ |
| Make the repository public (and enable private vulnerability reporting), tag `v0.1.0`, `dotnet pack -c Release -p:ContinuousIntegrationBuild=true`, publish | 🔒 |
| Create the GitHub release with CHANGELOG notes | 🔒 |

## Engineering items carried to 0.2

- **Storage SPI evolution (CR-24).** Decide between default interface members and an abstract base before adding members. Blocks the next two items.
- **Batched acknowledgements.** The measured throughput bottleneck (`docs/benchmarks.md`).
- **Batched release on shutdown (CR-08).**
- **Single-statement multi-row insert on PostgreSQL.** Measured opportunity.
- **Uniform `transaction: null` semantics (CR-11).**
- **Aggregate enqueue size limits (CR-18).**
- **`created_at` index for admin queries (CR-21).**
- **Automated PostgreSQL large-table plan assertion (T-03).**
- **Test fixtures that sweep orphaned `t_*` schemas left by killed test processes.**
- **SQL Server package release; Azure SQL and SQL Server 2019 testing.**
