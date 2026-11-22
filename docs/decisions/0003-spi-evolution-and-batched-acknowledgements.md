# ADR 0003: Evolve the storage SPI through optional capability interfaces; batch acknowledgements

- **Status:** Accepted, 2026-11-22
- **Deciders:** project owner ("go ahead with … batched acknowledgements", which accepts the architect's CR-24 recommendation), principal architect
- **Resolves:** review finding CR-24 / known issue K14; enables K15 (throughput) and CR-08 (batched release on shutdown)

## Context
- `IOutboxStore` is a public interface implemented by provider packages, including future third-party providers. Adding a member to it breaks every implementer.
- The benchmarks (`docs/benchmarks.md`) show one guarded `UPDATE` round trip per delivered message is the throughput bottleneck: 5,000 acknowledgements against 11 claims.

## Options considered
1. **Default interface members on `IOutboxStore`.** Non-breaking at the binary level. But a default implementation must either throw or loop over the single-item method, which hides the cost and silently changes performance characteristics. They are also awkward for test doubles.
2. **An abstract `OutboxStoreBase` class.** It would have to become the primary extension point, which is a breaking change for anyone who implemented the interface directly. Providers also lose the freedom to have their own base class.
3. **Optional capability interfaces** (chosen). New operations live in separate interfaces that a store *may* implement. The dispatcher checks `store is IOutboxBatchStore` and falls back to the existing per-message path otherwise. Nothing breaks, the capability is explicit, and each capability gets its own conformance tests.

## Decision
- **New public SPI interface `DbRelay.Storage.IOutboxBatchStore`**, implemented by both built-in providers:
  - `MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement>, ct)` → the **(id, token) pairs** actually updated;
  - `ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement>, ct)` → the pairs actually released.
  - Pairs rather than IDs, so a stale and a current acknowledgement for the same message can be told apart (BA-03).
  - Every row is still guarded by **its own** lock token. A row whose token no longer matches is simply absent from the result, and the dispatcher reports it as a lost lease, exactly as before.
- **The dispatcher batches only "Delivered" outcomes** (the hot path) and shutdown releases.
  - Retry and dead-letter outcomes stay per-message: they are rare, and they carry per-message error text and delays.
  - Batching is **group commit without linger**. A leader (up to `clamp(MaxConcurrency / 4, 1, 4)` at once) takes everything queued when it starts a round trip, so a lone acknowledgement is written immediately and acknowledgements arriving during a round trip share the next one. No latency is added on idle systems.
  - A failed batch is retried like a single outcome (3 attempts). Rows a retried batch did not update are re-read individually, so an earlier ambiguous commit is not misreported as a lost lease (CR-10).
- **`DispatcherOptions.BatchAcknowledgements`** (default `true`) can turn it off, for example while diagnosing.
- Future capabilities (LISTEN/NOTIFY wake-ups, a batch claim-and-lease extension, inbox storage) follow the same pattern.

## Implementation notes (measured during implementation, 2026-11-22)
- **Leader/follower group commit, no background thread.** A first version used a background flusher. At concurrency 1 the thread hop
  made dispatching about 30% slower. In the replacement, the first caller to arrive becomes the leader and flushes what is queued
  on its own task.
- **Leadership is bounded (BA-05, implemented in the 0.2 cycle).** A leader writes at most 4 round trips. Then, if work is still
  queued, it releases its leader slot and promotes the oldest queued follower, which leads in turn.
  - Without the bound, a leader under sustained load kept flushing until it saw an empty queue. It held its own delivery slot
    for the whole burst, and its "delivered" log and activity were delayed with it (measured: one acknowledgement took 4.3 s
    of a 6.8 s burst). With the bound, every acknowledgement in the same test completed in under 500 ms.
  - The hand-off order keeps the no-lost-wake-up guarantee. The leader releases first and promotes second, and the promoted
    follower consumes the promotion before it tries to lead. A stranded follower is caught by a deterministic test that stops
    all arrivals after the leader's last round.
  Up to four leaders flush concurrently: a single leader serialised round trips and measured 11–40% slower than unbatched
  parallel single-row updates on SQL Server at concurrency 8.
- **A batch of one uses the provider's single-row statement.** On PostgreSQL, the `unnest` join costs about 0.5 ms more than the
  plain `UPDATE` for one row (the plan is a good nested loop over the primary key; this is fixed overhead).
- **SQL Server statement shape was chosen by measurement** (20,000-row table, per call):

  | Statement | Items | Cost |
  |---|---|---|
  | single-row guarded `UPDATE` | 1 | 88 µs |
  | XML parameter shredded into a table variable | 1 / 8 / 64 | 2,268 / 2,628 / 3,512 µs |
  | parameterised `VALUES` join | 8 / 64 | 339 / 13,523 µs (scan at 64) |
  | parameterised `VALUES` join + `FORCESEEK` | 8 / 64 | 318 / 1,117 µs |

  The XML form's fixed overhead made batching 54–65% *slower* than parallel single-row updates at concurrency 8, so it was
  replaced. The provider now uses the `VALUES` join with `FORCESEEK`, in power-of-two buckets up to 64 items (6 cached plans per
  operation). Larger batches are split into 64-item chunks, and a one-item chunk uses the single-row statement.
- **PostgreSQL** uses `UPDATE ... FROM unnest($1::uuid[], $2::uuid[])`: one statement text, any size up to 1,000.

## Consequences
- Throughput is re-measured in `docs/benchmarks.md`.
- **Unchanged guarantees:** at-least-once, token-guarded transitions, and the crash-loop bound. A crash after delivery but before a batch flush loses at most the acknowledgements in that batch; those messages are redelivered after their lease expires, as before.
- A delivery's slot is held until its acknowledgement is recorded. A leader may hold its slot for up to 4 more round trips while it flushes others' acknowledgements, then hands off (BA-05).
- The public API grows by one SPI interface, one record and one option. That is acceptable pre-1.0 and recorded in CHANGELOG.
