# Project Status

_Last updated: 2026-11-22_

**Phase:** Milestone 14, release candidate ready **pending owner actions** (see RELEASE_CHECKLIST.md).
Nothing has been committed, pushed or published.

| Milestone | Status | Notes |
|---|---|---|
| M1 Environment, team, research | COMPLETED | |
| M2 Differentiation + architecture | COMPLETED | |
| M3 API + DB contract approval | COMPLETED | D1 amended by owner (net8/9/10/11); rest delegated to architect; ADR 0001 |
| M4 Core library | COMPLETED | Unit 384/384 (4 TFMs) |
| M5 PostgreSQL provider | COMPLETED | 300/300; reviewed |
| M6 Dispatcher | COMPLETED | Reviewed; 4 behaviour fixes from review (CR-05/06/09/10) |
| M7 Retry, dead-letter, crash recovery | COMPLETED | Process-kill and crash-loop tests pass |
| M8 SQL Server provider | COMPLETED | 380/380 (RCSI off/on); ADR 0002; package deferred to 0.2.0 per brief |
| M9 Integration, reliability, security | COMPLETED | Integration 76/76; two review passes: 0 Critical, 2 High (fixed), 6 Medium (fixed) |
| M10 Benchmarks | COMPLETED | `docs/benchmarks.md` |
| M11 Docs + samples | COMPLETED | README, 9 guides, 3 samples (run against real DBs) |
| M12 CI + packaging | IMPLEMENTED - NOT VERIFIED | Workflow written; steps verified locally; needs a push (owner approval) to run on GitHub |
| M13 Production-readiness audit | COMPLETED | Follow-up audit refuted "no open High" (FU-01); FU-01..FU-10 fixed and re-verified (1,140 test runs, 0 failed); FU-01/FU-04 mutation-checked. No independent re-audit of the FU fixes yet |
| M14 Release candidate | BLOCKED (owner) | Engineering done; repo URL, push/CI, NuGet prefix, contacts, trademark check need the owner |

## Environment

| Item | Finding |
|---|---|
| .NET | User-local SDK 11.0.100 + runtimes 8.0.34 / 9.0.23 / 10.0.15 / 11.0.0 in `%USERPROFILE%\.dotnet` (system install untouched) |
| PostgreSQL | Private test cluster from the installed PG 17.5 binaries: `%LOCALAPPDATA%\DbRelay\pgdata`, localhost:55432, trust auth (the user's own PG service untouched). Stop with `pg_ctl -D "%LOCALAPPDATA%\DbRelay\pgdata" stop` |
| SQL Server | Local 2022 Express; test databases `dbrelay_tests` (RCSI off) and `dbrelay_tests_rcsi` (RCSI on) |
| Docker | Installed, not running. Local database tests use connection strings; CI uses service containers |
| Claude Code | Project agents in `.claude/agents/` available as named agent types since 2026-11-19 |

## Agent activity log (summary)

| Role | Executed as | Work | Verified by architect |
|---|---|---|---|
| Competitor research | 3 general-purpose subagents (charters not yet loaded) | Brighter; MassTransit/Wolverine/CAP; small libraries + naming | Key claims re-checked against nuspecs, source and GitHub API |
| PostgreSQL engineer | general-purpose subagent with charter | Provider + tests | Re-ran 296/296; read ownership and guard SQL |
| SQL Server engineer | general-purpose subagent with charter (resumed once after session end) | Provider + tests; two-phase claim (ADR 0002) | Re-ran 364/364 (after one unreproduced overload failure, K6) |
| QA and reliability | `qa-reliability` agent (resumed once) | Integration and crash tests; found BUG-1 | Re-ran; fixed BUG-1; 68/68 |
| Docs and DevEx | `docs-devex` agent (resumed once after a watchdog stall) | README, guides, samples, OSS files | Spot-checked config tables against validators; updated docs for later fixes; re-ran a sample |
| Code reviewer | `code-reviewer` agent | Independent review: 36 findings | All Critical/High/Medium fixed; resolution log appended |
| Principal architect | main session | Architecture, core, dispatcher, conformance suite, CI, benchmarks, fixes | n/a |
