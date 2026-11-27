# DbRelay soak test: MySql

**Result: PASS** (9/9 invariants)

**Environment:** GitHub Actions `ubuntu-latest`. MySQL 8.4 (`mysql:8.4`) in a service container on the same runner. Workflow run
45890528542, commit `7293232` (branch `mysql-provider`), **before** the MySQL review fixes MY-01..MY-16; see
[2026-11-27-mysql-320min.md](2026-11-27-mysql-320min.md) for the run on the reviewed code. The load phase is 5 h 20 min (6-hour job limit).

**Memory:** the long-lived worker w0 rose slowly but steadily after warm-up: 188 MB at 30 min, 192 MB at 2 h, 201 MB at 3 h 30 min,
202 MB at 5 h (about 3 MB an hour). Threads (16-18) and handles (184-192) stayed flat. Not conclusive for a leak (the GC heap can grow
lazily), so the trend is checked again in the run on the reviewed code.

| Setting | Value |
|---|---|
| Started (UTC) | 2026-11-26 20:22 |
| Produce duration | 05:20:00 |
| Worker processes | 3 (w0 is never killed and is the memory-trend probe) |
| Target rate | ~1.7 msg/s, with a 10x burst for 20 s every 5 min |
| Dispatcher | BatchSize 50, MaxConcurrency 8, lease 10 s, timeout 3 s, MaxAttempts 8, batched acknowledgements on |
| Handler | 1-30 ms latency; 5% Retry, 2% throws; 2% of messages are poison and dead-lettered; success writes to a sink table before acknowledging |
| Producer | 1-5 messages per transaction; 20% of transactions rolled back |

## Load and chaos

| Measure | Value |
|---|---|
| Committed transactions / messages | 13,052 / 39,218 |
| Rolled-back transactions / messages | 3,234 / 9,637 |
| Poison messages committed | 732 |
| Producer errors (transaction failed and rolled back) | 0 |
| Worker processes hard-killed | 311 |
| Workers stopped gracefully | 147 |
| Connection-kill rounds / sessions terminated | 284 / 2406 |
| Drain time after producing stopped | 0.0 s |

## Invariants

| Invariant | Result | Detail |
|---|---|---|
| Atomicity: every committed order has its outbox row | PASS | 0 missing of 39218 |
| Atomicity: no outbox row without a committed order | PASS | 0 orphans of 39218 |
| Atomicity: nothing from a rolled-back transaction was stored | PASS | 0 of 9637 rolled-back IDs found |
| Atomicity: nothing from a rolled-back transaction was delivered | PASS | 0 delivered |
| Completeness: every committed non-poison message was delivered | PASS | 38486 of 38486 (dead-lettered: 0) |
| Poison messages were dead-lettered, never delivered | PASS | 732 of 732 dead-lettered; 0 reached the sink |
| Nothing left Pending or Processing after the drain | PASS | 0 rows |
| Attempts bounded (<= MaxAttempts + 1 = 9) | PASS | max attempt_count 6 |
| Every Delivered row reached the external system (sink) | PASS | 0 rows |

## At-least-once observations (allowed, not failures)

- Duplicate deliveries to the sink: **1** (0.003% of delivered messages). They are expected after hard kills (a message reached the sink, but the worker died before the acknowledgement), and after connection kills that interrupt an acknowledgement.
- Messages that reached the sink but are not Delivered: 0. For example, a message delivered, its acknowledgement lost, then dead-lettered after its remaining attempts failed.
- Non-poison messages dead-lettered: 0.

## Delivery latency (created_at to delivered_at, database clock)

| p50 | p95 | p99 | max | n |
|---|---|---|---|---|
| 72 ms | 205 ms | 341 ms | 10457 ms | 38,486 |

## Long-lived worker (w0) resource trend

| | After warm-up | Max | Last |
|---|---|---|---|
| Working set | 123.1 MB | 202.4 MB | 202.3 MB |
| Threads | 17 | 18 | 16 |
| Handles | 184 | 192 | 189 |

Max backlog seen: pending 7; max oldest-due age 0.2 s.

## Worker logs

- `fail:` lines (dead-letter and persist-failure logs, expected under chaos): 736
- Unexpected errors (unhandled exceptions, event 1011 "unexpected error while processing"): none
