# DbRelay soak test: PostgreSql

**Result: PASS** (9/9 invariants)

| Setting | Value |
|---|---|
| Started (UTC) | 2026-11-22 08:57 |
| Produce duration | 02:00:00 |
| Worker processes | 3 (w0 is never killed and is the memory-trend probe) |
| Target rate | ~40 msg/s, with a 10x burst for 20 s every 5 min |
| Dispatcher | BatchSize 50, MaxConcurrency 8, lease 10 s, timeout 3 s, MaxAttempts 8, batched acknowledgements on |
| Handler | 1-30 ms latency; 5% Retry, 2% throws; 2% of messages are poison and dead-lettered; success writes to a sink table before acknowledging |
| Producer | 1-5 messages per transaction; 20% of transactions rolled back |

## Load and chaos

| Measure | Value |
|---|---|
| Committed transactions / messages | 99,952 / 300,378 |
| Rolled-back transactions / messages | 25,388 / 76,374 |
| Poison messages committed | 6,089 |
| Producer errors (transaction failed and rolled back) | 0 |
| Worker processes hard-killed | 112 |
| Workers stopped gracefully | 53 |
| Connection-kill rounds / sessions terminated | 107 / 3175 |
| Drain time after producing stopped | 0.2 s |

## Invariants

| Invariant | Result | Detail |
|---|---|---|
| Atomicity: every committed order has its outbox row | PASS | 0 missing of 300378 |
| Atomicity: no outbox row without a committed order | PASS | 0 orphans of 300378 |
| Atomicity: nothing from a rolled-back transaction was stored | PASS | 0 of 76374 rolled-back IDs found |
| Atomicity: nothing from a rolled-back transaction was delivered | PASS | 0 delivered |
| Completeness: every committed non-poison message was delivered | PASS | 294289 of 294289 (dead-lettered: 0) |
| Poison messages were dead-lettered, never delivered | PASS | 6089 of 6089 dead-lettered; 0 reached the sink |
| Nothing left Pending or Processing after the drain | PASS | 0 rows |
| Attempts bounded (<= MaxAttempts + 1 = 9) | PASS | max attempt_count 5 |
| Every Delivered row reached the external system (sink) | PASS | 0 rows |

## At-least-once observations (allowed, not failures)

- Duplicate deliveries to the sink: **1** (0.000% of delivered messages). They are expected after hard kills (a message reached the sink, but the worker died before the acknowledgement), and after connection kills that interrupt an acknowledgement.
- Messages that reached the sink but are not Delivered: 0. For example, a message delivered, its acknowledgement lost, then dead-lettered after its remaining attempts failed.
- Non-poison messages dead-lettered: 0.

## Delivery latency (created_at to delivered_at, database clock)

| p50 | p95 | p99 | max | n |
|---|---|---|---|---|
| 113 ms | 357 ms | 859 ms | 10539 ms | 294,289 |

## Long-lived worker (w0) resource trend

| | After warm-up | Max | Last |
|---|---|---|---|
| Working set | 62.9 MB | 64.8 MB | 58.5 MB |
| Threads | 19 | 24 | 18 |
| Handles | 483 | 524 | 501 |

Max backlog seen: pending 50; max oldest-due age 2.9 s.

## Worker logs

- `fail:` lines (dead-letter and persist-failure logs, expected under chaos): 6,372
- Unexpected errors (unhandled exceptions, event 1011 "unexpected error while processing"): none
