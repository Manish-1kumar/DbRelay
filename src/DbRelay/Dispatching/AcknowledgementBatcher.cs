using System.Collections.Concurrent;
using DbRelay.Diagnostics;
using DbRelay.Storage;
using Microsoft.Extensions.Logging;

namespace DbRelay.Dispatching;

/// <summary>
/// Group-commit batching of "delivered" acknowledgements (ADR 0003): a leader/follower scheme with no background thread and up to
/// four concurrent leaders (scaled with MaxConcurrency).
/// A caller that finds a free leader slot becomes a leader. It writes everything queued (its own item included) on its own task and
/// keeps going until it sees an empty queue. Callers that arrive while every slot is busy queue their item and wait, and a leader
/// picks them up in its next round trip. A lone acknowledgement is therefore written immediately with no thread hop, and concurrent
/// acknowledgements share round trips. Each row stays guarded by its own lock token.
/// </summary>
/// <remarks>
/// A leader under sustained load keeps flushing while acknowledgements arrive, so its delivery slot (and its own "delivered" log and
/// activity) stays occupied until it sees an empty queue. Up to <c>clamp(MaxConcurrency / 4, 1, 4)</c> slots can be held this way;
/// the queue never holds more than MaxConcurrency items, and it drains when deliveries stop (BA-05).
/// </remarks>
internal sealed class AcknowledgementBatcher : IDisposable
{
    /// <summary>
    /// Upper bound per round trip. In practice the queue never holds more than MaxConcurrency (at most 256) items, so this is a safety
    /// limit, not a tuning knob.
    /// </summary>
    internal const int MaxBatchSize = 500;
    private const int Attempts = 3;
    private const int MaxParallelRereads = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentQueue<Pending> _queue = new();
    private readonly SemaphoreSlim _leader;
    private readonly IOutboxBatchStore _store;
    private readonly IOutboxStore _singleStore;
    private readonly int _maxLeaders;
    private readonly Func<OutboxMessageDetails, string, Task<bool>> _wasRecordedByThisClaim;
    private readonly DbRelayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public AcknowledgementBatcher(
        IOutboxBatchStore store,
        IOutboxStore singleStore,
        int maxConcurrency,
        Func<OutboxMessageDetails, string, Task<bool>> wasRecordedByThisClaim,
        DbRelayMetrics metrics,
        TimeProvider time,
        ILogger logger)
    {
        _store = store;
        _singleStore = singleStore;

        // Several leaders may flush concurrently. A single leader serialises acknowledgements to one round trip at a time, which
        // measured 11-40% slower than unbatched parallel single-row updates on SQL Server at concurrency 8 (88 us per single-row
        // UPDATE). Scaling leaders with concurrency keeps round trips parallel while still batching: 1 leader at concurrency < 8.
        _maxLeaders = Math.Clamp(maxConcurrency / 4, 1, 4);
        _leader = new SemaphoreSlim(_maxLeaders, _maxLeaders);
        _wasRecordedByThisClaim = wasRecordedByThisClaim;
        _metrics = metrics;
        _time = time;
        _logger = logger;
    }

    /// <summary>Test seam: runs after a leader's last drain and before it releases, the window in which a lost wake-up could occur.</summary>
    internal Func<Task>? BeforeLeaderReleaseForTesting { get; set; }

    internal int QueuedCount => _queue.Count;

    /// <summary>Records a guarded "delivered" update, possibly together with concurrent ones.</summary>
    /// <returns>
    /// <see langword="true"/> if recorded, <see langword="false"/> if the lease was lost, <see langword="null"/> if the database
    /// kept failing (the message is then redelivered after its lease expires).
    /// </returns>
    public async Task<bool?> MarkDeliveredAsync(OutboxMessageDetails details, Guid lockToken)
    {
        // RunContinuationsAsynchronously: a leader must never run a follower's continuation inline while still leading.
        var pending = new Pending(details, lockToken, new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously));
        _queue.Enqueue(pending);

        // No-lost-wake-up protocol:
        //  - Enqueue first, then try to lead.
        //  - A failed Wait(0) means every leader slot is held. Each holder re-checks the queue after releasing, and our item was
        //    enqueued before that release, so a holder will see it.
        //  - A releasing leader must re-check the queue and try to lead again even when its OWN item is already done. Exiting
        //    because "my item is complete" would strand a follower that enqueued after the last drain but before the release.
        //  - DrainAsync never throws (BA-01), so a leader always reaches the release and the re-check.
        while (_leader.Wait(0))
        {
            try
            {
                await DrainAsync().ConfigureAwait(false);
                if (BeforeLeaderReleaseForTesting is { } hook)
                {
                    await hook().ConfigureAwait(false);
                }
            }
            finally
            {
                _leader.Release();
            }

            if (_queue.IsEmpty)
            {
                break;
            }
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Waits for any in-progress flush and drains what is left. Precondition: no concurrent <see cref="MarkDeliveredAsync"/> calls.
    /// The dispatcher calls this only after every delivery, and therefore every acknowledgement, has finished (BA-09).
    /// </summary>
    public async Task CompleteAsync()
    {
        for (var i = 0; i < _maxLeaders; i++)
        {
            await _leader.WaitAsync().ConfigureAwait(false);
        }

        try
        {
            await DrainAsync().ConfigureAwait(false);
        }
        finally
        {
            _leader.Release(_maxLeaders);
        }
    }

    public void Dispose() => _leader.Dispose();

    /// <summary>Flushes until the queue is empty. Never throws: every dequeued item is always completed (BA-01).</summary>
    private async Task DrainAsync()
    {
        var batch = new List<Pending>(Math.Min(MaxBatchSize, Math.Max(1, _queue.Count)));
        while (true)
        {
            while (batch.Count < MaxBatchSize && _queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            if (batch.Count == 0)
            {
                return;
            }

            try
            {
                await FlushAsync(batch).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                SafeLog(() => Log.ProcessingFailed(_logger, exception, batch[0].Details.Id));
            }
            finally
            {
                // Whatever happened (a store failure, a throwing logger, a bug), nobody may be left waiting.
                foreach (var pending in batch)
                {
                    pending.Completion.TrySetResult(null);
                }
            }

            batch.Clear();
        }
    }

    private async Task FlushAsync(List<Pending> batch)
    {
        var items = new OutboxAcknowledgement[batch.Count];
        for (var i = 0; i < batch.Count; i++)
        {
            items[i] = new OutboxAcknowledgement(batch[i].Details.Id, batch[i].LockToken);
        }

        IReadOnlyCollection<OutboxAcknowledgement>? updated = null;
        var previousAttemptFailed = false;
        for (var attempt = 1; updated is null; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(Timeout, _time);

                // A batch of one uses the cheaper single-row statement: measured on PostgreSQL, the array-join form costs about 0.5 ms
                // more, which would make low-concurrency dispatchers slower than without batching.
                updated = items.Length == 1
                    ? (await _singleStore.MarkDeliveredAsync(items[0].MessageId, items[0].LockToken, timeout.Token).ConfigureAwait(false) ? [items[0]] : [])
                    : await _store.MarkDeliveredBatchAsync(items, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (attempt < Attempts)
            {
                previousAttemptFailed = true;
                var size = batch.Count;
                SafeLog(() => Log.AcknowledgementBatchRetry(_logger, exception, size, attempt));
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // One log line per message, with the same Outcome value as the unbatched path, so per-message searches and filters
                // keep working (BA-06). The batch summary is logged separately.
                var size = batch.Count;
                SafeLog(() => Log.AcknowledgementBatchFailed(_logger, exception, size));
                foreach (var pending in batch)
                {
                    pending.Completion.TrySetResult(null);
                    SafeLog(() => Log.OutcomePersistFailed(_logger, exception, pending.Details.Id, "delivered"));
                }

                return;
            }
        }

        _metrics.RecordAcknowledgementBatch(batch.Count);

        // Match on (id, token) pairs, not IDs: a stale and a current acknowledgement for the same message can share a batch, and only
        // the one whose token matched was recorded (BA-03).
        var recorded = updated as ISet<OutboxAcknowledgement> ?? new HashSet<OutboxAcknowledgement>(updated);
        var unmatched = new List<Pending>();
        foreach (var pending in batch)
        {
            if (recorded.Contains(new OutboxAcknowledgement(pending.Details.Id, pending.LockToken)))
            {
                pending.Completion.TrySetResult(true);
            }
            else if (previousAttemptFailed)
            {
                unmatched.Add(pending);
            }
            else
            {
                pending.Completion.TrySetResult(false);
            }
        }

        if (unmatched.Count > 0)
        {
            // An earlier attempt may have committed but lost its result, so this retry matched no rows (CR-10). Re-read the rows in
            // parallel with a bound, not one at a time (BA-04).
            using var gate = new SemaphoreSlim(MaxParallelRereads, MaxParallelRereads);
            await Task.WhenAll(unmatched.Select(async pending =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var wasRecorded = await _wasRecordedByThisClaim(pending.Details, "delivered").ConfigureAwait(false);
                    pending.Completion.TrySetResult(wasRecorded);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);
        }
    }

    /// <summary>A logging failure must never strand waiters or break a flush (BA-01).</summary>
    private static void SafeLog(Action log)
    {
        try
        {
            log();
        }
        catch (Exception)
        {
            // deliberately ignored
        }
    }

    private readonly record struct Pending(OutboxMessageDetails Details, Guid LockToken, TaskCompletionSource<bool?> Completion);
}
