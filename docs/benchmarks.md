# Benchmarks

These are measured numbers, not claims. Everything here was produced by `benchmarks/DbRelay.Benchmarks` on the machine described
below, and you can reproduce it. These are **single-machine baselines**: the application and the database share 2 CPU cores, so
the absolute numbers say little about production capacity. The **relative** numbers (overhead per message, effect of batch
size) are the useful part.

## Environment (2026-11-21)

| | |
|---|---|
| CPU | Intel Core i5-5350U @ 1.80 GHz (Broadwell), 2 physical / 4 logical cores |
| OS | Windows 10 22H2 (10.0.19045) |
| Runtime | .NET 10.0.15 (X64 RyuJIT, x86-64-v3), built with .NET SDK 11.0.100, Release |
| PostgreSQL | 17.5 on the same machine (localhost:55432, default configuration) |
| SQL Server | 2022 Express RTM 16.0.1000.6 on the same machine (READ_COMMITTED_SNAPSHOT off) |
| Other load | None. No tests or agents were running during the measured runs. |

## 1. Enqueue overhead (BenchmarkDotNet 0.15.8)

```powershell
dotnet run -c Release --project benchmarks/DbRelay.Benchmarks -- enqueue --filter "*"
```

Each operation runs a full business transaction: pooled connection, `BEGIN`, a Dapper insert into an `orders` table,
DbRelay's work, then `COMMIT`. The baseline is the same transaction without DbRelay.

| Operation | PostgreSQL | SQL Server |
|---|---|---|
| Business insert only (baseline) | 475.0 µs ± 9.4 | 734.1 µs ± 7.6 |
| + 1 outbox message | 767.9 µs ± 10.5 (**+0.29 ms**) | 1,034.9 µs ± 17.0 (**+0.30 ms**) |
| + 10 outbox messages (`EnqueueManyAsync`) | 2,128.5 µs ± 18.8 | 1,894.0 µs ± 16.6 |
| Standalone enqueue (DbRelay-owned connection, no business write) | 464.2 µs ± 10.1 | 684.1 µs ± 81.0 |
| Allocated per op (1 message) | 8.2 KB | 13.2 KB |

The "±" values are half of the 99.9% confidence interval.

**Reading:** adding one outbox message costs about one extra database round trip, around 0.3 ms locally. A
networked database adds its round-trip latency on top.

**Optimisation opportunity (measured, not yet done):** on PostgreSQL, 10 messages cost 1.36 ms more than 1 message.
`InsertAsync` sends an `NpgsqlBatch` with one command per row. A single multi-row `INSERT ... SELECT FROM unnest(...)`
should reduce this. Planned for 0.2 and to be re-measured.

An earlier run on a loaded machine had about 10× wider confidence intervals and was discarded.

## 2. End-to-end delivery throughput

```powershell
dotnet run -c Release --project benchmarks/DbRelay.Benchmarks -- throughput PostgreSql 5000
dotnet run -c Release --project benchmarks/DbRelay.Benchmarks -- throughput SqlServer 5000
```

**Method:**
- Pre-load 5,000 messages.
- Start N dispatcher hosts **in one process** with a no-op handler.
- Time from start until every message has been delivered.
- `PollingInterval` is 100 ms; cleanup and statistics are off.
- "Claims" counts claim round trips. "Duplicates" counts deliveries beyond the first per message.

### PostgreSQL 17.5

| Batch | Concurrency | Workers | Seconds | Msg/s | Claims | Duplicates |
|---|---|---|---|---|---|---|
| 10 | 1 | 1 | 5.35 | 935 | 501 | 0 |
| 10 | 8 | 1 | 2.38 | 2,102 | 501 | 0 |
| 10 | 32 | 1 | 2.32 | 2,153 | 501 | 0 |
| 100 | 1 | 1 | 3.89 | 1,287 | 51 | 0 |
| 100 | 8 | 1 | 1.49 | 3,345 | 51 | 0 |
| 100 | 32 | 1 | 2.12 | 2,358 | 51 | 0 |
| 500 | 1 | 1 | 3.88 | 1,288 | 11 | 0 |
| **500** | **8** | **1** | **1.45** | **3,460** | 11 | 0 |
| 500 | 32 | 1 | 2.10 | 2,376 | 11 | 0 |
| 10 | 1 | 4 | 2.63 | 1,903 | 504 | 0 |
| 10 | 8 | 4 | 3.24 | 1,544 | 504 | 0 |
| 10 | 32 | 4 | 4.17 | 1,199 | 504 | 0 |
| 100 | 1 | 4 | 2.30 | 2,172 | 54 | 0 |
| 100 | 8 | 4 | 1.96 | 2,550 | 54 | 0 |
| 100 | 32 | 4 | 3.87 | 1,291 | 54 | 0 |
| 500 | 1 | 4 | 1.99 | 2,515 | 20 | 0 |
| 500 | 8 | 4 | 2.18 | 2,294 | 18 | 0 |
| 500 | 32 | 4 | 4.29 | 1,165 | 20 | 0 |

### SQL Server 2022 Express

| Batch | Concurrency | Workers | Seconds | Msg/s | Claims | Duplicates |
|---|---|---|---|---|---|---|
| 10 | 1 | 1 | 7.49 | 667 | 501 | 0 |
| 10 | 8 | 1 | 3.18 | 1,570 | 501 | 0 |
| 10 | 32 | 1 | 2.85 | 1,756 | 501 | 0 |
| 100 | 1 | 1 | 4.93 | 1,014 | 51 | 0 |
| 100 | 8 | 1 | 1.42 | 3,526 | 51 | 0 |
| 100 | 32 | 1 | 1.62 | 3,078 | 51 | 0 |
| 500 | 1 | 1 | 4.78 | 1,047 | 11 | 0 |
| 500 | 8 | 1 | 1.32 | 3,782 | 11 | 0 |
| 500 | 32 | 1 | 1.33 | 3,764 | 11 | 0 |
| 10 | 1 | 4 | 2.48 | 2,014 | 503 | 0 |
| 10 | 8 | 4 | 2.23 | 2,240 | 503 | 0 |
| 10 | 32 | 4 | 2.32 | 2,155 | 504 | 0 |
| 100 | 1 | 4 | 1.60 | 3,123 | 54 | 0 |
| 100 | 8 | 4 | 1.42 | 3,523 | 54 | 0 |
| 100 | 32 | 4 | 1.78 | 2,811 | 54 | 0 |
| 500 | 1 | 4 | 1.62 | 3,084 | 21 | 0 |
| **500** | **8** | **4** | **1.27** | **3,948** | 16 | 0 |
| 500 | 32 | 4 | 1.52 | 3,281 | 18 | 0 |

### What the data shows

- **Batching claims works.** Going from batch 10 to batch 500 cuts claim round trips from about 500 to 11–21 for 5,000 messages.
- **Concurrency matters for latency-bound handlers.** Concurrency 1 → 8 roughly triples throughput.
- **Saturation on this machine.** Concurrency 32, and four hosts in one process, help little or make things slower. Two cores are
  shared by the app and the database. This shows local CPU saturation, **not** how DbRelay scales across machines, which these
  runs do not measure.
- **The remaining bottleneck is per-message acknowledgement.** Every delivered message costs one guarded `UPDATE` round trip
  (5,000 acknowledgements against 11 claims in the best PostgreSQL run). Batched acknowledgements are the next optimisation,
  backed by this data. They need a new storage SPI member, so they are planned together with the SPI-evolution decision
  (review finding CR-24).
- **Zero duplicates** in all 36 configurations, with up to four concurrent dispatchers. This is expected in a crash-free run.
  Delivery is still at-least-once in general; see [reliability.md](reliability.md).

Real handlers (a broker publish, an HTTP call) dominate these costs. With a 20 ms handler and the defaults (`MaxConcurrency`
8), one instance tops out at about 400 msg/s from handler latency alone, so increase `MaxConcurrency` or add instances.
