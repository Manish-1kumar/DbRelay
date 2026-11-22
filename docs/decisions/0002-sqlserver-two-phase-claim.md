# ADR 0002: SQL Server claim uses a two-phase statement batch

- **Status:** Accepted, 2026-11-21
- **Deciders:** sqlserver-engineer (proposal, with measurements), principal architect (approval)
- **Supersedes:** the claim SQL in `architecture-proposal.md` §8.2 (original draft)

## Context
The architecture proposal specified a single `UPDATE` over a `TOP (n) ... WITH (UPDLOCK, READPAST, ROWLOCK)` CTE. Actual execution plans on SQL Server 2022 (20,000 rows, 500 due, batch 100) showed:
- **Without an index hint:** a clustered index scan plus sort read all 20,000 rows.
- **With an index hint:** an Index Seek under an **eager spool** (Halloween protection, because the update changes `available_at`, the key of the claim index). Every due row (500) was read and U-locked before `TOP` applied.

The provider lock test confirmed the consequence. An in-flight claim of 1 row also locked the second due row, so a concurrent claimer got nothing. Under a large backlog this risks lock escalation to a table lock.

## Decision
Claim in one short transaction with two statements:
1. `INSERT @_claimed SELECT TOP (n) [sequence] ... WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(claim))` locks exactly the batch.
2. `UPDATE ... WITH (ROWLOCK, READPAST, FORCESEEK) JOIN @_claimed` re-checks the predicate and `OUTPUT`s the rows.

Results are re-sorted on the client by the pre-update `available_at`. Requeue of dead letters uses the same pattern.

## Consequences
- Actual plans show 100 rows read and locked to claim 100, no memory grant, and an index seek in phase 1. Concurrency conformance passes with RCSI on and off.
- The index hints make statements **fail** rather than degrade if a DBA drops a DbRelay index. This is documented in `docs/sqlserver.md`.
- `FORCESEEK` was required in phase 2. Without it, the optimizer scanned the outbox for the join and blocked behind uncommitted enqueues.
- **Parity:** PostgreSQL keeps its single statement (`WITH ... MATERIALIZED ... FOR UPDATE SKIP LOCKED`), which does not have this problem. Providers deliberately differ in SQL and behave identically, as the conformance suite verifies.
