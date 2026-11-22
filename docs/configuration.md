# Configuration

Options are set in code through `DbRelayBuilder` inside `AddDbRelay`. The engine options (`OutboxOptions`,
`DispatcherOptions`, `RetryOptions`, `MaintenanceOptions`) are validated **when the host starts** (`ValidateOnStart`).
An invalid value stops startup with an `OptionsValidationException` whose message names the option and its valid range.
Provider options are validated earlier, when `UsePostgreSql` or `UseSqlServer` is called.

The ranges below are copied from `src/DbRelay/Configuration/OptionsValidators.cs` and the provider registration code.

> DbRelay 0.1 has no `IConfiguration` binding overload. To drive options from `appsettings.json`, read your own section and
> assign the values inside the callbacks, for example
> `relay.ConfigureDispatcher(d => d.BatchSize = configuration.GetValue("Outbox:BatchSize", 100))`.

## Builder methods

| Method | Purpose |
|---|---|
| `UsePostgreSql(...)` / `UseSqlServer(...)` | Selects the storage provider. Exactly one is required. |
| `UseDeliveryHandler<THandler>()` | Registers the handler type, resolved from a new DI scope for every delivery. Required unless the dispatcher is disabled. Only one handler can be registered. |
| `UseDeliveryHandler(Func<OutboxMessage, CancellationToken, Task<DeliveryResult>>)` | Registers a delegate as the handler (singleton). |
| `DisableDispatcher()` | This application only enqueues; no handler needed. Maintenance still runs. |
| `MapMessageType<T>(string name)` | Logical type name for `T` (1 to 256 characters, unique). Overrides `[OutboxMessageType]`. |
| `UseJsonSerializerOptions(JsonSerializerOptions)` | Options for the default System.Text.Json serializer. The default is camelCase. |
| `UseSerializer(IMessageSerializer)` | Replaces the serializer (for example MessagePack). |
| `UseRetryPolicy<TPolicy>()` | Replaces the backoff calculation (`IRetryPolicy`). The result is still capped at `MaxDelay`. |
| `Configure(Action<OutboxOptions>)` | Enqueue options. |
| `ConfigureDispatcher(Action<DispatcherOptions>)` | Dispatcher options. |
| `ConfigureRetry(Action<RetryOptions>)` | Retry options. |
| `ConfigureMaintenance(Action<MaintenanceOptions>)` | Cleanup and statistics options. |

## `OutboxOptions`

| Option | Default | Valid range | Notes |
|---|---|---|---|
| `MaxPayloadBytes` | 1,048,576 (1 MiB) | 1 to 268,435,456 (256 MiB) | Larger serialized payloads are rejected at enqueue with `ArgumentException`. |

## `DispatcherOptions`

| Option | Default | Valid range | Notes |
|---|---|---|---|
| `BatchSize` | 100 | 1 to 1000 | Messages claimed per round trip. Claimed messages wait for a free slot, and any whose remaining lease is too short to finish are released, so keep `BatchSize` within a small multiple of `MaxConcurrency × (LeaseDuration ÷ typical delivery time)`. With fast handlers (milliseconds) the default is fine. With slow handlers, lower it to avoid claim-and-release churn. |
| `MaxConcurrency` | 8 | 1 to 256 | Deliveries running at once **in this process**. The dispatcher does not claim while every slot is busy, so it never hoards leases. |
| `PollingInterval` | 1 s | 10 ms to 1 h | Wait after a claim that returned fewer than `BatchSize` messages. ±10% jitter. After a full batch it claims again immediately. |
| `LeaseDuration` | 2 min | 1 s to 1 h, **and at least 2 × `DeliveryTimeout`** | How long a claim is valid, measured on the database clock. A crashed worker's messages become claimable again after this time. |
| `DeliveryTimeout` | 30 s | more than 0, at most 30 min | The handler's cancellation token fires after this time, and the attempt is then retried. If the handler **ignores** the token, the dispatcher stops waiting after a further grace period of min(5 s, `DeliveryTimeout` ÷ 2). It frees the concurrency slot, schedules a retry, logs event 1013 and increments `dbrelay.delivery.abandoned`. Any late result from the abandoned handler is never recorded. While max(2, 2 × `MaxConcurrency`) abandoned handlers are still running, the dispatcher starts no new deliveries, and the health check reports Degraded (gauge `dbrelay.delivery.abandoned.active`). |
| `MaxErrorBackoff` | 30 s | 1 s to 10 min | Longest wait between claim attempts while the database is failing (exponential from 1 s, ±10% jitter). |
| `WorkerId` | `machine:pid:random` | 1 to 256 characters, or `null` | Stored in `locked_by` for diagnostics only. Correctness never depends on it. |

How leases and timeouts interact:

- Before starting each claimed message, the dispatcher checks the lease time left. If less than `DeliveryTimeout` plus a safety
  margin (10% of the lease, at most 5 s) remains, the message is **released** unattempted instead of started. This shows up in
  the `dbrelay.messages.released` metric. Keep `MaxConcurrency` high enough, or `BatchSize` low enough, that a batch starts well
  within the lease.
- A handler that ignores its cancellation token can outlive the lease. Another worker may then claim and deliver the message
  again, and the slow worker's outcome is rejected (`dbrelay.lease.lost`, log event 1007). Always pass the token on.

## `RetryOptions`

| Option | Default | Valid range | Notes |
|---|---|---|---|
| `MaxAttempts` | 10 | 1 to 1000 | Includes the first attempt. Attempts are counted at **claim** time, so a crash loop also ends in dead-letter. |
| `InitialDelay` | 1 s | more than 0, at most 1 day | Base delay before the first retry. |
| `MaxDelay` | 5 min | at least `InitialDelay`, at most 7 days | Caps computed delays and handler-supplied `RetryAfter`. |
| `BackoffMultiplier` | 2.0 | 1 to 10 (not NaN) | Growth per attempt. 1 gives a constant delay. |
| `IsPermanentFailure` | `null` | any `Func<Exception, bool>` | Returns `true` for exceptions to dead-letter immediately. If the predicate itself throws, the exception is treated as transient. |

The delay after failed attempt *n* is `d = min(MaxDelay, InitialDelay × BackoffMultiplier^(n-1))`, then a uniformly random
value in `[d/2, d]`. With the defaults: about 0.5 to 1 s, 1 to 2 s, 2 to 4 s, and so on, capped at 2.5 to 5 min.
`DeliveryResult.Retry(reason, retryAfter)` replaces the computed delay, still capped at `MaxDelay`. A delivery timeout counts
as a transient failure (`TimeoutException`).

When attempt `MaxAttempts` fails transiently, the message is dead-lettered with
`Max attempts (N) reached. Last error: ...`. If a claim finds `attempt_count > MaxAttempts` (earlier attempts never recorded an
outcome, typically because the worker crashed), the message is dead-lettered **without** calling the handler, with
`Exceeded the maximum of N attempts without a recorded outcome (possible worker crash loop).`.

## `MaintenanceOptions`

| Option | Default | Valid range | Notes |
|---|---|---|---|
| `CleanupEnabled` | `true` | - | Deletes delivered messages older than `DeliveredRetention`. Dead letters are never deleted. |
| `DeliveredRetention` | 1 day | 0 to 3650 days | Age since delivery, measured on the database clock. |
| `CleanupInterval` | 5 min | 1 s to 1 day | |
| `CleanupBatchSize` | 1000 | 1 to 10,000 | Rows per delete statement. Each run deletes at most 100 batches. If a run stops at that cap, the next run starts after 1 s instead of a full `CleanupInterval`, and event 1102 ("cleanup is behind") is logged. |
| `StatisticsInterval` | 1 min | 1 s to 1 day, or `null` | How often statistics are queried for the `dbrelay.messages.*` gauges. `null` disables the query and the gauges. The query runs only on instances that run the dispatcher; enqueue-only instances skip it. The pending, processing and dead-letter counts use the partial indexes. The exact Delivered count is an index-only scan proportional to retained delivered rows: on PostgreSQL about 0.2–0.3 s per 2M retained rows, measured on a laptop. |

Maintenance runs in every application that calls `AddDbRelay`, including those with `DisableDispatcher()`. Running it on
several instances is safe: deletes skip rows locked by another instance.

## PostgreSQL provider (`PostgreSqlOutboxOptions`)

Validated when `UsePostgreSql` is called; invalid values throw `ArgumentException` or `ArgumentOutOfRangeException`.

| Option | Default | Valid values |
|---|---|---|
| `Schema` | `dbrelay` | 1 to 63 characters matching `^[A-Za-z_][A-Za-z0-9_]{0,62}$`. Quoted in SQL, so case-sensitive. |
| `TableName` | `outbox_messages` | Same rules. `schema_version` is reserved. Several outbox tables can share a schema. |
| `CommandTimeout` | 30 s | Positive. Applies to every command DbRelay runs. |

A malformed connection string is rejected without echoing it, because it may contain a password.

## SQL Server provider (`SqlServerOutboxOptions`)

| Option | Default | Valid values |
|---|---|---|
| `Schema` | `dbrelay` | 1 to 63 characters matching `^[A-Za-z_][A-Za-z0-9_]{0,62}$`. Quoted with `[...]`. |
| `TableName` | `outbox_messages` | Same rules. `schema_version` is reserved (case-insensitive). |
| `CommandTimeout` | 30 s | 1 s to 1 h. Applies to every statement, **including the insert on your connection**. |

## Health check (`DbRelayHealthCheckOptions`)

`services.AddHealthChecks().AddDbRelay(name: "dbrelay", configure: ..., failureStatus: null, tags: null)`

| Option | Default | Notes |
|---|---|---|
| `MaxOldestDueAge` | `null` (off) | Degraded when the oldest due message has waited longer than this. |
| `MaxDeadLettered` | `null` (off) | Degraded when more than this many messages are dead-lettered. |

Setting either threshold adds a statistics query to every health probe. `failureStatus` (default Unhealthy) is reported when
the database or outbox table cannot be reached. See [troubleshooting.md](troubleshooting.md#health-check-states).

## Enqueue limits (`EnqueueOptions` and messages)

| Field | Limit |
|---|---|
| `MessageId` | Not `Guid.Empty`. Must be unique: a duplicate fails with the provider's unique-constraint error. Not allowed with `EnqueueManyAsync`. |
| `Destination` | 1 to 512 characters |
| `CorrelationId`, `CausationId` | 1 to 128 characters |
| `DeliverAt` / `Delay` | Mutually exclusive. `Delay` is 0 to 3650 days. Both are evaluated on the database clock. |
| `Headers` | At most 64. Keys 1 to 128 characters; values at most 4096. Keys starting with `dbrelay-` are reserved. `traceparent` and `tracestate` are added from `Activity.Current` unless you set them. |
| Message type name | 1 to 256 characters, not whitespace only, no control characters |
| Content type (raw messages) | 1 to 128 characters |
| `EnqueueManyAsync` | 1 to 1000 messages per call |
| Any string field | Must not contain NUL (U+0000) |
