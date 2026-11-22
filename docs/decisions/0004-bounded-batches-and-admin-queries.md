# ADR 0004: Bound the payload size of enqueue calls and claims; bound admin query cost without a write-path index

- **Status:** Accepted, 2026-11-22
- **Deciders:** principal architect (owner delegated the 0.2 engineering backlog: "Continue 0.2 engineering")
- **Resolves:** review findings CR-18 (no aggregate size limits) and CR-21 (admin queries sort the whole table)

## CR-18: aggregate payload limits

### Context
- `EnqueueManyAsync` accepted up to 1,000 messages of up to `MaxPayloadBytes` each: 1 GiB per call at the defaults, and 256 GiB at the maximums.
- A claim loads up to `BatchSize` payloads at once: 100 MiB at the defaults, and about 256 GiB at the maximums.
- One unusual call, or one backlog of unusually large messages, could exhaust memory on a producer or on every dispatcher.

### Decision
- **One budget for both paths:** `OutboxLimits.BatchPayloadBudgetBytes` = 64 MiB.
- **Enqueue:** the serialized payloads of one `EnqueueManyAsync` call may total at most the larger of 64 MiB and `OutboxOptions.MaxPayloadBytes`. The check runs while serializing, so an oversized call fails before anything is sent. Callers split the work into several calls in the same transaction.
- **Claim:** `ClaimRequest` gains an init-only `MaxPayloadBytes`, defaulting to the budget.
  - In claim order, a message is claimed only if the payloads before it in the batch total less than the budget.
  - The first message is always claimed, however large it is.
  - Messages cut by the budget are left untouched and are claimed by a later poll.
  - Existing `IOutboxStore` implementations still compile, and the conformance suite enforces the new rule (`Claim_stops_at_the_payload_budget_but_always_takes_the_first_message`).
- **No new options.** The defaults are safe for every supported configuration, and a knob can be added later if someone needs one.

### Implementation notes (measured 2026-11-22, local PostgreSQL 17.5 and SQL Server 2022 Express)
- **PostgreSQL:** the claim CTE selects `octet_length(payload)`, which reads the stored length without detoasting. A second materialized CTE keeps rows whose running total before them is under `$5`.
  - Cost: pgbench, claim of 100 rows rolled back, 4 alternating rounds of 400: 4.0 → 4.3 ms (about +3 µs per message).
  - Rows locked by the first CTE but cut by the budget are not updated, and their locks end with the statement.
- **SQL Server:** after phase 1 of the two-phase claim (ADR 0002), the picked rows' payload sizes are summed through clustered seeks. A windowed `DELETE` trims the table variable only when that sum reaches the budget.
  - An ungated trim cost +2.2 ms per claim of 100 (10.8 → 13.1 ms); the gated form costs +0.2–0.8 ms.
  - Trimmed rows stay U-locked until the claim's short transaction commits, and are not updated.

## CR-21: admin query cost

### Context
- `IOutboxAdmin.QueryAsync` orders by `(created_at, id)`, which has no index.
- Measured on a 1M-row PostgreSQL table (300-byte payloads, 95% delivered):
  - An unfiltered first page took 430 ms (parallel seq scan, top-N heapsort).
  - `Skip = 10,000` sorted whole 613-byte rows and spilled about 400 MB to disk: 1.6 s. `Skip = 500,000` took 1.8 s.
- A `(created_at, id)` index made 100,000 single-row inserts 25–28% slower (2.79 → 3.52 s and 2.82 → 3.62 s, two A/B rounds). Every enqueue, inside the caller's business transaction, would pay that.

### Decision
- **No `created_at` index.** The enqueue path matters more than admin paging.
- **PostgreSQL pages sort only the narrow keys:** `(created_at, id)`, then the page's full rows are fetched by primary key. The SQL Server provider already works this way. `Skip = 10,000` now takes 0.5 s with a 2 MB in-memory heap.
- **`OutboxQuery.Skip` is limited to 0–10,000,** so the key sort always stays a bounded in-memory heap.
- **Documentation** (`docs/troubleshooting.md`): Pending, Processing and DeadLettered filters use partial indexes; unfiltered and Delivered queries read every matching row (about 0.5 s per million rows on a laptop).

## Consequences
- **Behaviour changes (pre-1.0, recorded in CHANGELOG):**
  - `EnqueueManyAsync` rejects calls above the payload budget.
  - `OutboxQuery.Skip` above 10,000 throws.
  - A claim may return fewer than `BatchSize` messages when payloads are large.
- **Public API:** adds one constant (`OutboxLimits.BatchPayloadBudgetBytes`) and one init-only SPI property (`ClaimRequest.MaxPayloadBytes`).
- **Dispatcher memory per claim** is bounded by 64 MiB plus one message.
