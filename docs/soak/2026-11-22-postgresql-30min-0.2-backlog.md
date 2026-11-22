# DbRelay soak test: PostgreSql

**Result: PASS** (9/9 invariants)

| Setting | Value |
|---|---|
| Started (UTC) | 2026-11-22 18:17 |
| Produce duration | 00:30:00 |
| Worker processes | 3 (w0 is never killed and is the memory-trend probe) |
| Target rate | ~40 msg/s, with a 10x burst for 20 s every 5 min |
| Dispatcher | BatchSize 50, MaxConcurrency 8, lease 10 s, timeout 3 s, MaxAttempts 8, batched acknowledgements on |
| Handler | 1-30 ms latency; 5% Retry, 2% throws; 2% of messages are poison and dead-lettered; success writes to a sink table before acknowledging |
| Producer | 1-5 messages per transaction; 20% of transactions rolled back |

## Load and chaos

| Measure | Value |
|---|---|
| Committed transactions / messages | 23,791 / 71,522 |
| Rolled-back transactions / messages | 5,947 / 17,961 |
| Poison messages committed | 1,470 |
| Producer errors (transaction failed and rolled back) | 0 |
| Worker processes hard-killed | 29 |
| Workers stopped gracefully | 15 |
| Connection-kill rounds / sessions terminated | 28 / 831 |
| Drain time after producing stopped | 0.1 s |

## Invariants

| Invariant | Result | Detail |
|---|---|---|
| Atomicity: every committed order has its outbox row | PASS | 0 missing of 71522 |
| Atomicity: no outbox row without a committed order | PASS | 0 orphans of 71522 |
| Atomicity: nothing from a rolled-back transaction was stored | PASS | 0 of 17961 rolled-back IDs found |
| Atomicity: nothing from a rolled-back transaction was delivered | PASS | 0 delivered |
| Completeness: every committed non-poison message was delivered | PASS | 70052 of 70052 (dead-lettered: 0) |
| Poison messages were dead-lettered, never delivered | PASS | 1470 of 1470 dead-lettered; 0 reached the sink |
| Nothing left Pending or Processing after the drain | PASS | 0 rows |
| Attempts bounded (<= MaxAttempts + 1 = 9) | PASS | max attempt_count 5 |
| Every Delivered row reached the external system (sink) | PASS | 0 rows |

## At-least-once observations (allowed, not failures)

- Duplicate deliveries to the sink: **0** (0.000% of delivered messages). They are expected after hard kills (a message reached the sink, but the worker died before the acknowledgement), and after connection kills that interrupt an acknowledgement.
- Messages that reached the sink but are not Delivered: 0. For example, a message delivered, its acknowledgement lost, then dead-lettered after its remaining attempts failed.
- Non-poison messages dead-lettered: 0.

## Delivery latency (created_at to delivered_at, database clock)

| p50 | p95 | p99 | max | n |
|---|---|---|---|---|
| 117 ms | 400 ms | 1397 ms | 10839 ms | 70,052 |

## Long-lived worker (w0) resource trend

| | After warm-up | Max | Last |
|---|---|---|---|
| Working set | 61.9 MB | 63.6 MB | 59.2 MB |
| Threads | 18 | 23 | 18 |
| Handles | 490 | 528 | 473 |

Max backlog seen: pending 186; max oldest-due age 0.8 s.

## Worker logs

- `fail:` lines (dead-letter and persist-failure logs, expected under chaos): 1,546
- Unexpected errors (unhandled exceptions, event 1011 "unexpected error while processing"): none
