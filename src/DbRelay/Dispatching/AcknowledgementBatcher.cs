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
/// Leadership is bounded (BA-05). A leader writes at most <see cref="MaxLeaderRounds"/> round trips, then releases its leader slot
/// and promotes the oldest queued follower, which leads in turn. Without the bound, a leader under sustained load kept flushing
/// until it saw an empty queue, holding its own delivery slot for the whole burst (measured: 4.3 s of a 6.8 s burst).
/// </remarks>
internal sealed class AcknowledgementBatcher : IDisposable
{
    /// <summary>
    /// Upper bound per round trip. In practice the queue never holds more than MaxConcurrency (at most 256) items, so this is a safety
    /// limit, not a tuning knob.
    /// </summary>
    internal const int MaxBatchSize = 500;

    /// <summary>Round trips a leader writes before handing leadership to a waiting follower (BA-05).</summary>
    internal const int MaxLeaderRounds = 4;
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
        var pending = new Pending(details, lockToken);
        _queue.Enqueue(pending);
        await LeadAsync().ConfigureAwait(false);

        // A follower waits for its result, or for a leader to hand leadership over to it (BA-05).
        while (!pending.Completion.Task.IsCompleted)
        {
            var promotion = pending.Promotion;
            if (await Task.WhenAny(pending.Completion.Task, promotion).ConfigureAwait(false) == promotion)
            {
                // Consume the promotion BEFORE trying to lead, so a later promotion is never mistaken for this one.
                pending.ConsumePromotion();
                await LeadAsync().ConfigureAwait(false);
            }
        }

        return await pending.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Leads while a leader slot is free and work is queued, for at most <see cref="MaxLeaderRounds"/> round trips.
    /// </summary>
    /// <remarks>
    /// No-lost-wake-up protocol:
    /// <list type="bullet">
    /// <item>Callers enqueue first, then try to lead. A failed <c>Wait(0)</c> means every leader slot is held, and each holder
    /// re-checks the queue after releasing; the item was enqueued before that release, so a holder will see it.</item>
    /// <item>A releasing leader re-checks the queue even when its own item is done. Exiting because "my item is complete" would
    /// strand a follower that enqueued after the last drain but before the release.</item>
    /// <item>A leader that has used its rounds releases its slot FIRST and then promotes the oldest queued item's owner. That owner
    /// tries <c>Wait(0)</c> only after consuming the promotion, so after the release: it either leads, or every slot is held by a
    /// leader that will re-check. A promotion that misses (the item was dequeued meanwhile) is harmless: a slot holder dequeued it
    /// and will re-check; and the owner of an item still queued has not returned, so it observes its promotion.</item>
    /// <item><see cref="DrainAsync"/> never throws (BA-01), so a leader always reaches the release and the re-check.</item>
    /// </list>
    /// </remarks>
    private async Task LeadAsync()
    {
        var rounds = 0;
        while (_leader.Wait(0))
        {
            try
            {
                rounds += await DrainAsync(MaxLeaderRounds - rounds).ConfigureAwait(false);
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
                return;
            }

            if (rounds >= MaxLeaderRounds)
            {
                if (_queue.TryPeek(out var next))
                {
                    next.Promote();
                }

                return;
            }
        }
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
            await DrainAsync(int.MaxValue).ConfigureAwait(false);
        }
        finally
        {
            _leader.Release(_maxLeaders);
        }
    }

    public void Dispose() => _leader.Dispose();

    /// <summary>
    /// Flushes until the queue is empty or <paramref name="maxRounds"/> round trips were written, and returns the number written.
    /// Never throws: every dequeued item is always completed (BA-01).
    /// </summary>
    private async Task<int> DrainAsync(int maxRounds)
    {
        var batch = new List<Pending>(Math.Min(MaxBatchSize, Math.Max(1, _queue.Count)));
        var rounds = 0;
        while (rounds < maxRounds)
        {
            while (batch.Count < MaxBatchSize && _queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            if (batch.Count == 0)
            {
                return rounds;
            }

            rounds++;

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

        return rounds;
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

    /// <summary>A queued acknowledgement and its owner's signals.</summary>
    private sealed class Pending(OutboxMessageDetails details, Guid lockToken)
    {
        private TaskCompletionSource _promotion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public OutboxMessageDetails Details { get; } = details;

        public Guid LockToken { get; } = lockToken;

        /// <summary>RunContinuationsAsynchronously: a leader must never run a follower's continuation inline while still leading.</summary>
        public TaskCompletionSource<bool?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes when a leader hands leadership to this item's owner.</summary>
        public Task Promotion => Volatile.Read(ref _promotion).Task;

        public void Promote() => Volatile.Read(ref _promotion).TrySetResult();

        public void ConsumePromotion() =>
            Interlocked.Exchange(ref _promotion, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
