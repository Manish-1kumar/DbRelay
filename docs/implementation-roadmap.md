# Implementation Roadmap

Date: 2026-11-18. Every milestone ends with an implementation report, a test report with real output, an independent code review, an updated tracker, and an acceptance-criteria check. Owners refer to `.claude/agents/*.md`.

| M | Milestone | Owner(s) | Acceptance criteria |
|---|---|---|---|
| 1 | Environment, team, competitor research | Architect, Research | ✅ Environment inventoried; agent charters + CLAUDE.md exist; `competitor-analysis.md` with sourced claims; name assessed |
| 2 | Differentiation + architecture | Architect | ✅ `product-differentiation.md`, `architecture-proposal.md`, `risks.md`, this roadmap |
| 3 | **API + DB contract approval** | User + Architect | Decisions D1–D10 approved or amended; ADRs written in `docs/decisions/` |
| 4 | Core library | Core dev | Solution skeleton (CPM, `Directory.Build.props`, `global.json`, warnings-as-errors, analyzers); Abstractions + core types; options validation; serializer; type-name registry; identifier validator; storage SPI; unit tests green |
| 5 | PostgreSQL provider | PG engineer | Schema scripts + `EnsureCreatedAsync` (idempotent); enqueue (single/batch/raw/standalone) with transaction validation; claim/ack/retry/dead-letter/release/requeue/cleanup SQL; EXPLAIN shows partial-index use; provider tests green on real PG |
| 6 | Dispatcher + delivery pipeline | Dispatcher eng | Hosted service with bounded concurrency, per-delivery scope + timeout, local lease check, DB-outage backoff, graceful drain + release; traces/metrics; unit tests with fake store + `FakeTimeProvider`; integration test end-to-end on PG |
| 7 | Retry, dead-letter, crash recovery | Dispatcher eng, QA | Backoff/jitter math tests; exhaustion → dead-letter; crash-loop bound; admin requeue; **process-kill crash test** passes; stale-token tests pass |
| 8 | SQL Server provider | SQL Server eng | Same conformance suite passes on SQL Server 2022 with RCSI off **and** on; plan evidence for claim query; batch cap enforced |
| 9 | Integration, reliability, security | QA, Reviewer | Multi-worker (≥4 dispatchers) exactly-one-success-per-message check; DB interruption recovery; security review (SQL injection, identifiers, logging, limits, deserialization); `reliability.md` traceability table complete |
| 10 | Benchmarks + optimisation | QA, Dispatcher | BenchmarkDotNet enqueue benchmarks; throughput harness (batch × concurrency × workers) with recorded hardware/DB versions; optimisations only where data shows benefit |
| 11 | Docs + samples | Docs | README, getting-started, configuration, architecture, postgres, sqlserver, reliability, troubleshooting, hand-rolled-migration guide; 3 samples build and run; all snippets compile |
| 12 | CI + packaging | Docs, Architect | GitHub Actions: restore/build/format/test (Testcontainers)/pack/vulnerability audit; `dotnet pack` output inspected (README, XML docs, SourceLink, symbols, license, deterministic); package validation |
| 13 | Production-readiness audit | Reviewer | Fresh independent review of the full codebase; no open Critical/High findings |
| 14 | Release candidate | Architect | `RELEASE_CHECKLIST.md` complete; CHANGELOG; version 0.1.0 packages for Abstractions/core/PostgreSql built; **no publish/push without user approval** |

## Parallelisation plan (after approval)

1. M4 runs sequentially, owned by the architect and core dev. It defines the SPI everything else depends on.
2. Once the SPI is frozen, the following run in parallel with no file overlap:
   - M5 (PostgreSQL), in `src/DbRelay.PostgreSql`
   - M6 (dispatcher), in `src/DbRelay/Dispatching`
   - the conformance test suite skeleton (QA), in `tests/DbRelay.Storage.Conformance`, shared by both providers
3. Then M7.
4. M8 (SQL Server) can then run in parallel with M10 setup and M11 drafting.

## Environment blockers known now

- **Docker engine is not running.** Testcontainers can't start containers locally. Workarounds: start Docker Desktop, or use the local PostgreSQL 17 and SQL Server 2022 Express services.
- **PostgreSQL credentials are unknown.** Integration tests need `DBRELAY_TEST_POSTGRES` or Docker.
- The installed .NET SDK is 10.0.401 (runtime 10.0.12), behind the current 10.0.15 patch. Updating is recommended for security fixes.
- Process-kill crash tests and CI need either Docker or the connection strings above. CI on GitHub-hosted Linux runners has Docker.
