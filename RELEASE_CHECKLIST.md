# Release Checklist: 0.1.0 (published 2026-11-26)

_Last updated: 2026-11-26 · Packages in this release: `DbRelay.Abstractions`, `DbRelay`, `DbRelay.PostgreSql`. `DbRelay.SqlServer` is built and
tested but not packed until 0.2.0 (project brief §15)._

Legend: ✅ done and verified · ⏳ done locally, awaiting an external step · ❌ not done · 🔒 requires owner approval

## Definition of Done (project brief §20)

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | Architecture documented | ✅ | `docs/architecture.md`, `docs/architecture-proposal.md`, ADR 0001–0004 |
| 2 | Competitor analysis complete | ✅ | `docs/competitor-analysis.md` (sourced; key claims re-verified) |
| 3 | Public APIs stable and documented | ✅ (pre-1.0) | XML docs on all public members (build enforces). SPI evolution decided (ADR 0003: optional capability interfaces) |
| 4 | Core library builds | ✅ | `dotnet build DbRelay.slnx -c Release`: 0 warnings, 0 errors, 4 TFMs |
| 5 | PostgreSQL persistence works | ✅ | 300/300 provider tests |
| 6 | Transaction integration verified | ✅ | Conformance transaction tests (commit/rollback/ownership/validation) + integration atomicity (200 random commit/rollback) |
| 7 | Background delivery works | ✅ | Dispatcher unit tests, integration, sample run |
| 8 | Custom handlers work | ✅ | `CustomHandler.Sample` (webhook 200/429/422) run against PostgreSQL |
| 9 | Retry behaviour works | ✅ | Retry schedule measured on the DB clock within jitter bounds |
| 10 | Dead-letter handling works | ✅ | Exhaustion, permanent failure, crash loop, requeue tests |
| 11 | Multiple worker instances work safely | ✅ | 4 hosts × 2,000 messages, 0 duplicates; 8 concurrent claimers × 300, no double claim (both providers) |
| 12 | Lease recovery works | ✅ | Conformance reclaim tests + process-kill test |
| 13 | Crash scenarios tested | ✅ | Real child-process kill (redelivery after lease) and crash-loop → dead-letter. Soak/chaos runs (`docs/soak/`): 3.5 h in total on both providers, 187 hard kills, 5,083 terminated sessions, 0 lost messages, PASS 9/9 each |
| 14 | At-least-once documented | ✅ | README, `docs/reliability.md` (guarantee → test table) |
| 15 | Security review complete | ✅ | `CODE_REVIEW_REPORT.md`: initial review, follow-up audit, and both resolution logs |
| 16 | Performance benchmarks exist | ✅ | `docs/benchmarks.md` (BenchmarkDotNet + throughput sweep, environment recorded) |
| 17 | Documentation accurate | ✅ | Snippets copied from compiling samples or compiled in a scratch project; docs updated after review fixes |
| 18 | Sample applications run | ✅ | All 3 run against real DBs (docs engineer); PostgreSQL sample re-run after the fixes |
| 19 | CI passes | ✅ | Latest GitHub Actions run (ubuntu, service containers) green: 1,280/1,280 tests, 0 skipped; PostgreSQL 15/16/18 compatibility jobs green. Run 45025481068 (first green run: 44996266454, 1,140 tests) |
| 20 | NuGet packages build and pass validation | ✅ | `dotnet pack` → 3 packages + symbols; contents and dependency groups inspected; package validation enabled |
| 21 | No unresolved Critical/High findings | ✅ | Initial review: 1 High (CR-01). Follow-up audit found it partly fixed (FU-01, High) plus 1 Medium. Both fixed; FU-01 and FU-04 fixes mutation-checked. The FU fixes have not had an independent re-audit |
| 22 | Release checklist complete | ✅ | This file. Only the optional owner items below remain (prefix reservation, trademark search) |

## Before publishing (owner actions)

| Item | Status |
|---|---|
| Repository created: https://github.com/Manish-1kumar/DbRelay (private); set as the default package RepositoryUrl/ProjectUrl | ✅ |
| Push to GitHub and confirm CI is green, including the PostgreSQL 15/16/18 matrix | ✅ |
| Reserve the `DbRelay.*` ID prefix on nuget.org | 🔒 |
| Manual trademark search (USPTO/EUIPO/WIPO, classes 9/42) for "DbRelay" (K5) | 🔒 |
| Contacts in CODE_OF_CONDUCT.md and SECURITY.md: GitHub private reporting only, no email (owner decision, 2026-11-22) | ✅ |
| Enable GitHub private vulnerability reporting | ✅ (2026-11-22) |
| Package-specific README with absolute links (`src/PACKAGE_README.md`, packed as README.md; 0 relative links verified) | ✅ |
| Make the repository public | ✅ (2026-11-22). Re-created from a history rewritten to the GitHub no-reply address; the original private repository is kept as `DbRelay-archive` |
| Tag `v0.1.0` and publish to NuGet (owner approved 2026-11-26) via `.github/workflows/publish.yml`: nuget.org Trusted Publishing, `nuget` environment with required reviewer | ✅ (2026-11-26). Run 45692836402: 3 packages + 3 symbol packages pushed; installed from nuget.org in a fresh app |
| Create the GitHub release with CHANGELOG notes | ✅ (2026-11-26) https://github.com/Manish-1kumar/DbRelay/releases/tag/v0.1.0 |

## Engineering items carried to 0.2

- ~~**Storage SPI evolution (CR-24).**~~ Done: optional capability interfaces (ADR 0003).
- ~~**Batched acknowledgements.**~~ Done (ADR 0003, `docs/benchmarks.md` §3). It is in `[Unreleased]`, so 0.1.0 ships it if
  it is tagged from the current `main`.
- ~~**Batched release on shutdown (CR-08).**~~ Done.
- ~~**Batched-acknowledgement follow-ups:** a bounded leader hand-off (BA-05).~~ Done: at most 4 rounds, then hand-off (deterministic and load tests; mutation-checked). ~~SQL Server lock-behaviour stress test (BA-07)~~ done: `Concurrent_claims_batches_stale_acknowledgements_and_cleanup_never_deadlock` (0 deadlock retries in 24 runs).
- ~~**Single-statement multi-row insert on PostgreSQL.**~~ Done: `unnest` over parallel arrays; re-measured in `docs/benchmarks.md` (about 0.4 ms less per 10 messages, modest).
- ~~**Uniform `transaction: null` semantics (CR-11).**~~ Resolved as documented provider-specific behaviour: neither driver exposes the active transaction, so DbRelay cannot reject or adopt it uniformly. Both behaviours are now pinned by tests (T-05), and SQL Server explains the fix.
- ~~**Aggregate enqueue size limits (CR-18).**~~ Done (ADR 0004): 64 MiB per enqueue call and per claim.
- ~~**`created_at` index for admin queries (CR-21).**~~ Resolved without an index (it measured +25-28% insert cost). PostgreSQL admin paging now sorts only the narrow keys, as SQL Server does, and `Skip` is capped at 10,000. See `docs/troubleshooting.md`.
- ~~**Automated PostgreSQL large-table plan assertion (T-03).**~~ Done: `Planner_chooses_the_partial_indexes_on_a_realistic_table` (100k rows, no `enable_seqscan` override; mutation-checked against the pre-CR-03 statistics query).
- ~~**Test fixtures that sweep orphaned `t_*` schemas left by killed test processes.**~~ Done (K13).
- **SQL Server package release; Azure SQL and SQL Server 2019 testing.**

## How to release

1. **Prepare the release commit.** Set `VersionPrefix` in `Directory.Build.props` and date the version in `CHANGELOG.md`. Then wait for CI to be green on `main`.
2. **Push the tag.** Run `git tag -a vX.Y.Z -m "DbRelay X.Y.Z"` and `git push origin vX.Y.Z`. This starts **Publish to NuGet**:
   - The `build` job has no publishing rights. It checks that the tag matches the project version, runs the unit tests on all target frameworks, and packs.
   - The `publish` job then waits for approval.
3. **Approve the publish.** In GitHub, open Actions > the run > **Review deployments**, and approve the `nuget` environment.
   - The job exchanges a GitHub OIDC token for a nuget.org API key that is valid for 1 hour. No key is stored anywhere.
   - It then pushes the `.nupkg` and `.snupkg` files.
4. **One-time nuget.org setup:** a Trusted Publishing policy with these values:
   - **Repository Owner** `Manish-1kumar`;
   - **Repository** `DbRelay`;
   - **Workflow File** `publish.yml`;
   - **Environment** `nuget`;
   - **Scope** "Push new packages and package versions";
   - **Glob** `DbRelay*`.

