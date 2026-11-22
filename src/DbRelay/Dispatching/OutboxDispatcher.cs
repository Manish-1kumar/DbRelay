using System.Diagnostics;
using DbRelay.Configuration;
using DbRelay.Diagnostics;
using DbRelay.Internal;
using DbRelay.Retry;
using DbRelay.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbRelay.Dispatching;

/// <summary>
/// The background delivery loop.
/// </summary>
/// <remarks>
/// <para>Each iteration:</para>
/// <list type="number">
/// <item><description>Claims up to <c>BatchSize</c> due messages in one short, auto-committed statement. Each claim gets a fresh lock token and a database-clock lease.</description></item>
/// <item><description>Starts each claimed message when a concurrency slot is free. Messages whose remaining lease is too short to finish a delivery are released instead.</description></item>
/// <item><description>Runs the handler in its own DI scope with a timeout. No database transaction is open during delivery.</description></item>
/// <item><description>Records the outcome with a lock-token-guarded update. If the lease was lost, the update affects no rows and the other worker's state is left alone.</description></item>
/// </list>
/// <para>
/// Once every claimed message has started, it claims again: immediately if the batch was full, otherwise after the polling interval.
/// On shutdown it stops claiming, releases unstarted messages and lets in-flight deliveries finish. If the host's shutdown timeout
/// elapses first, in-flight deliveries are cancelled and released.
/// </para>
/// </remarks>
internal sealed class OutboxDispatcher : BackgroundService
{
    private static readonly TimeSpan PersistTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReleaseGracePeriod = TimeSpan.FromSeconds(5);
    private const int PersistAttempts = 3;
    internal const string InterruptedByShutdownReason = "Delivery was interrupted by host shutdown.";
    private static readonly TimeSpan ClaimSafetyTimeout = TimeSpan.FromMinutes(5);
    private int _liveAbandonedHandlers;

    private readonly IOutboxStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRetryPolicy _retryPolicy;
    private readonly DispatcherState _state;
    private readonly DbRelayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly ILogger<OutboxDispatcher> _logger;
    private readonly DispatcherOptions _options;
    private readonly RetryOptions _retry;
    private readonly string _workerId;
    private readonly TimeSpan _leaseSafetyMargin;
    private readonly TimeSpan _abandonGrace;

    // FU-02: at most this many abandoned handlers may be alive before the dispatcher stops starting new deliveries. Headroom above
    // MaxConcurrency means one hung handler doesn't stall a MaxConcurrency=1 dispatcher (that would re-create CR-05), while the
    // total stays bounded: live orphans never exceed this limit plus MaxConcurrency (handlers in flight when the limit is hit).
    private readonly int _abandonedHandlerLimit;
    private readonly CancellationTokenSource _abort = new();
    private readonly HashSet<Task> _inFlight = [];
    private readonly object _inFlightGate = new();

    public OutboxDispatcher(
        IOutboxStore store,
        IServiceScopeFactory scopeFactory,
        IRetryPolicy retryPolicy,
        DispatcherState state,
        DbRelayMetrics metrics,
        TimeProvider time,
        IOptions<DispatcherOptions> options,
        IOptions<RetryOptions> retryOptions,
        ILogger<OutboxDispatcher> logger)
    {
        _store = store;
        _scopeFactory = scopeFactory;
        _retryPolicy = retryPolicy;
        _state = state;
        _metrics = metrics;
        _time = time;
        _logger = logger;
        _options = options.Value;
        _retry = retryOptions.Value;
        _workerId = _options.WorkerId ?? CreateWorkerId();
        _leaseSafetyMargin = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks, _options.LeaseDuration.Ticks / 10));

        // Extra time a handler gets to react to cancellation before it is abandoned. Lease >= 2 x DeliveryTimeout (validated), so
        // DeliveryTimeout + grace <= 0.75 x lease leaves room to record the timeout before the lease can expire.
        _abandonGrace = TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(5).Ticks, _options.DeliveryTimeout.Ticks / 2));
        _abandonedHandlerLimit = Math.Max(2, _options.MaxConcurrency * 2);
    }

    internal string WorkerId => _workerId;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Signals the stopping token and waits for ExecuteAsync, or until the host's shutdown timeout elapses.
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The shutdown timeout elapsed; handled below.
        }

        if (ExecuteTask is { IsCompleted: false } running)
        {
            // Out of time: cancel in-flight deliveries so they are released, and give the releases a short grace period.
            await _abort.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(running, Task.Delay(ReleaseGracePeriod, _time, CancellationToken.None)).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _abort.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        _state.MarkRunning(true);
        Log.DispatcherStarted(_logger, _workerId, _options.BatchSize, _options.MaxConcurrency, _options.LeaseDuration);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var batch = await ClaimBatchAsync(slots, stoppingToken).ConfigureAwait(false);
                if (batch is null)
                {
                    continue; // claim failed; backoff already applied
                }

                await DispatchBatchAsync(batch.Value, slots, stoppingToken).ConfigureAwait(false);

                if (batch.Value.Messages.Count < _options.BatchSize)
                {
                    await DelayAsync(Jitter(_options.PollingInterval), stoppingToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Task[] remaining;
            lock (_inFlightGate)
            {
                remaining = [.. _inFlight];
            }

            await Task.WhenAll(remaining).ConfigureAwait(false);
            _state.MarkRunning(false);
            Log.DispatcherStopped(_logger, _workerId);
        }
    }

    private async Task<ClaimedBatch?> ClaimBatchAsync(SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        try
        {
            // Do not claim while every slot is busy: claimed-but-waiting messages would burn their lease.
            await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            slots.Release();

            // FU-02: abandoned handlers still hold a thread and a DI scope. Once the limit is reached, stop claiming
            // (the health check reports Degraded) instead of starting ever more doomed handlers.
            if (Volatile.Read(ref _liveAbandonedHandlers) >= _abandonedHandlerLimit)
            {
                await DelayAsync(_options.PollingInterval, stoppingToken).ConfigureAwait(false);
                return null;
            }

            var lockToken = Guid.NewGuid();
            var started = _time.GetTimestamp();

            // The claim is NOT cancelled by the stopping token. On PostgreSQL the claim commits before its rows are read, so
            // cancelling mid-read would leave claimed rows in Processing for a full lease (CR-09). A claim that finishes during
            // shutdown is released unstarted by DispatchBatchAsync, with its attempt given back. It IS cancelled by the shutdown
            // timeout (abort) so a hung database cannot keep the dispatcher running after StopAsync returns (FU-04). The safety
            // timeout is generous so the provider's own CommandTimeout governs normal operation (FU-06).
            using var claimCancellation = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
            claimCancellation.CancelAfter(ClaimSafetyTimeout);
            var messages = await _store.ClaimAsync(
                new ClaimRequest(_options.BatchSize, _options.LeaseDuration, lockToken, _workerId),
                claimCancellation.Token).ConfigureAwait(false);

            var elapsed = _time.GetElapsedTime(started);
            _state.PollSucceeded(_time.GetUtcNow());
            _metrics.RecordClaim(messages.Count, elapsed);
            if (messages.Count > 0)
            {
                Log.Claimed(_logger, messages.Count);
            }

            // The database set available_at = db_now + lease while executing the claim, which is no earlier than 'started',
            // so this local deadline is conservative.
            return new ClaimedBatch(messages, lockToken, started);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            var failures = _state.PollFailed(exception);
            _metrics.PollFailed();
            var delay = ErrorBackoff(failures);
            Log.ClaimFailed(_logger, exception, failures, delay);
            await DelayAsync(delay, stoppingToken).ConfigureAwait(false);
            return null;
        }
    }

    private async Task DispatchBatchAsync(ClaimedBatch batch, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        var messages = batch.Messages;
        for (var i = 0; i < messages.Count; i++)
        {
            var message = messages[i];
            try
            {
                await slots.WaitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReleaseAsync(messages, i, batch.LockToken, "dispatcher stopping").ConfigureAwait(false);
                return;
            }

            if (Volatile.Read(ref _liveAbandonedHandlers) >= _abandonedHandlerLimit)
            {
                // FU-02: the abandoned-handler limit is reached. Don't start more from this batch either.
                slots.Release();
                await ReleaseAsync(messages, i, batch.LockToken, "too many abandoned handlers still running").ConfigureAwait(false);
                return;
            }

            var remainingLease = _options.LeaseDuration - _time.GetElapsedTime(batch.ClaimStartedTimestamp);
            if (remainingLease < _options.DeliveryTimeout + _abandonGrace + _leaseSafetyMargin)
            {
                // Not enough lease left to guarantee that the outcome update, or the abandonment of a hung handler, lands before
                // another worker could reclaim the message. Measured from the delivery start, not the claim (FU-03). Validation
                // (lease >= 2 x timeout) guarantees this holds at claim time: T + T/2 + lease/10 <= 1.7T.
                slots.Release();
                await ReleaseAsync(messages, i, batch.LockToken, "insufficient lease time remaining").ConfigureAwait(false);
                return;
            }

            Track(ProcessAsync(message, batch.LockToken, slots));
        }
    }

    private void Track(Task task)
    {
        lock (_inFlightGate)
        {
            _inFlight.Add(task);
        }

        _ = task.ContinueWith(
            static (completed, state) =>
            {
                var self = (OutboxDispatcher)state!;
                lock (self._inFlightGate)
                {
                    self._inFlight.Remove(completed);
                }
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ProcessAsync(OutboxMessageDetails details, Guid lockToken, SemaphoreSlim slots)
    {
        try
        {
            // Leave the claim loop immediately even if the handler does synchronous work before its first await.
            await Task.Yield();

            if (UnreadableRow.IsUnreadable(details))
            {
                // The provider could not map this row (CR-01 / FU-01). Never hand a placeholder to the handler. Dead-letter it under
                // its lock token so the rest of the batch proceeds; an operator can inspect it and fix or requeue it.
                await RecordDeadLetterAsync(details, lockToken, details.Headers[UnreadableRow.ErrorHeader], null).ConfigureAwait(false);
                return;
            }

            if (details.AttemptCount > _retry.MaxAttempts)
            {
                // Attempts are counted at claim time. Reaching this point means earlier attempts never recorded an outcome,
                // typically because the worker crashed or was killed while delivering this message, or (FU-10) the final attempt
                // was interrupted by a host shutdown.
                var reason = string.Equals(details.LastError, InterruptedByShutdownReason, StringComparison.Ordinal)
                    ? $"Exceeded the maximum of {_retry.MaxAttempts} attempts; the last attempt was interrupted by host shutdown."
                    : $"Exceeded the maximum of {_retry.MaxAttempts} attempts without a recorded outcome (possible worker crash loop).";
                await RecordDeadLetterAsync(details, lockToken, reason, null).ConfigureAwait(false);
                return;
            }

            await DeliverAsync(details, lockToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.ProcessingFailed(_logger, exception, details.Id);
        }
        finally
        {
            _state.RecordActivity(_time.GetUtcNow());
            slots.Release();
        }
    }

    private async Task DeliverAsync(OutboxMessageDetails details, Guid lockToken)
    {
        var message = ToMessage(details);
        using var activity = StartDeliveryActivity(message);
        using var timeout = new CancellationTokenSource(_options.DeliveryTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, _abort.Token);

        DeliveryResult? result = null;
        Exception? error = null;
        var started = _time.GetTimestamp();
        try
        {
            // Task.Run detaches the handler from this thread so that even a handler that blocks synchronously can be abandoned.
            var handlerToken = linked.Token;
            var handlerTask = Task.Run(() => InvokeHandlerAsync(message, handlerToken), CancellationToken.None);
            using (var giveUp = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token))
            {
                var deadline = Task.Delay(_options.DeliveryTimeout + _abandonGrace, _time, giveUp.Token);
                if (await Task.WhenAny(handlerTask, deadline).ConfigureAwait(false) != handlerTask)
                {
                    // The handler ignored its cancellation token (CR-05). Stop waiting so it cannot hold a concurrency slot forever.
                    // Its eventual result is never recorded. Cancel its token explicitly first, so the orphan never holds a token whose
                    // sources are about to be disposed in a non-cancelled state (FU-09).
                    await linked.CancelAsync().ConfigureAwait(false);
                    ObserveAbandoned(handlerTask, details.Id);
                    if (_abort.IsCancellationRequested)
                    {
                        activity.SetError("abandoned at shutdown");
                        await ReturnInterruptedAsync(details, lockToken).ConfigureAwait(false);
                        return;
                    }

                    _metrics.HandlerAbandoned();
                    Log.HandlerAbandoned(_logger, details.Id, details.MessageType, _options.DeliveryTimeout);
                    throw new TimeoutException(
                        $"Delivery did not complete within {_options.DeliveryTimeout} and the handler did not honour cancellation; it was abandoned.");
                }

                await giveUp.CancelAsync().ConfigureAwait(false);
            }

            result = await handlerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            // Host shutdown timeout: we do not know whether the delivery happened. Release it; it will be delivered again.
            activity.SetError("cancelled by shutdown");
            await ReturnInterruptedAsync(details, lockToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            error = new TimeoutException($"Delivery did not complete within {_options.DeliveryTimeout}.", exception);
        }
        catch (Exception exception)
        {
            error = exception;
        }

        var elapsed = _time.GetElapsedTime(started);

        if (error is null && result is null)
        {
            error = new InvalidOperationException("The delivery handler returned null instead of a DeliveryResult.");
        }

        if (error is null && result!.Outcome == DeliveryOutcome.Delivered)
        {
            _metrics.RecordDelivery(details.MessageType, DeliveryOutcome.Delivered, elapsed);
            var recorded = await PersistAsync(
                details, "delivered", ct => _store.MarkDeliveredAsync(details.Id, lockToken, ct)).ConfigureAwait(false);
            if (recorded == true)
            {
                _metrics.Delivered(details.MessageType);
                Log.Delivered(_logger, details.Id, details.MessageType, details.AttemptCount);
            }

            return;
        }

        var permanent = result?.Outcome == DeliveryOutcome.DeadLetter || (error is not null && IsPermanent(error));
        var reason = error is not null ? ErrorText.FromException(error) : ErrorText.Truncate(result!.Reason ?? result.Outcome.ToString());
        activity.SetError(reason);

        if (permanent)
        {
            _metrics.RecordDelivery(details.MessageType, DeliveryOutcome.DeadLetter, elapsed);
            await RecordDeadLetterAsync(details, lockToken, reason, error).ConfigureAwait(false);
            return;
        }

        _metrics.RecordDelivery(details.MessageType, DeliveryOutcome.Retry, elapsed);
        if (details.AttemptCount >= _retry.MaxAttempts)
        {
            var exhausted = ErrorText.Truncate($"Max attempts ({_retry.MaxAttempts}) reached. Last error: {reason}");
            await RecordDeadLetterAsync(details, lockToken, exhausted, error).ConfigureAwait(false);
            return;
        }

        var delay = result?.RetryAfter ?? _retryPolicy.GetDelay(details.AttemptCount);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }
        else if (delay > _retry.MaxDelay)
        {
            delay = _retry.MaxDelay;
        }

        var scheduled = await PersistWithFallbackAsync(
            details, "retry", reason, (text, ct) => _store.ScheduleRetryAsync(details.Id, lockToken, delay, text, ct)).ConfigureAwait(false);
        if (scheduled == true)
        {
            _metrics.Retried(details.MessageType);
            Log.RetryScheduled(_logger, error, details.Id, details.MessageType, details.AttemptCount, _retry.MaxAttempts, reason, delay);
        }
    }

    private async Task<DeliveryResult> InvokeHandlerAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        // The scope is disposed when the handler finishes, even if the dispatcher has stopped waiting for it.
        var scope = _scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = scope.ServiceProvider.GetRequiredService<IMessageDeliveryHandler>();
            return await handler.DeliverAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    private void ObserveAbandoned(Task<DeliveryResult> handlerTask, Guid messageId)
    {
        _state.SetAbandonedHandlers(Interlocked.Increment(ref _liveAbandonedHandlers));
        _ = handlerTask.ContinueWith(
            static (task, state) =>
            {
                var (self, id) = ((OutboxDispatcher, Guid))state!;
                self._state.SetAbandonedHandlers(Interlocked.Decrement(ref self._liveAbandonedHandlers));
                if (task.Exception is { } exception)
                {
                    try
                    {
                        Log.AbandonedHandlerFaulted(self._logger, exception.GetBaseException(), id);
                    }
                    catch (ObjectDisposedException)
                    {
                        // The host (and its logger) may already be gone when an orphan finally fails (FU-09).
                    }
                }
            },
            (this, messageId),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RecordDeadLetterAsync(OutboxMessageDetails details, Guid lockToken, string reason, Exception? error)
    {
        var recorded = await PersistWithFallbackAsync(
            details, "dead-lettered", reason, (text, ct) => _store.MarkDeadLetteredAsync(details.Id, lockToken, text, ct)).ConfigureAwait(false);
        if (recorded == true)
        {
            _metrics.DeadLettered(details.MessageType);
            Log.DeadLettered(_logger, error, details.Id, details.MessageType, details.AttemptCount, reason);
        }
    }

    /// <summary>
    /// Persists an outcome that carries error text. If that keeps failing (for example because the database rejects the text),
    /// the outcome is recorded once more with a fixed reason. A rejected reason must never leave a message stuck in Processing and
    /// redelivered every lease. The original reason is always logged.
    /// </summary>
    private async Task<bool?> PersistWithFallbackAsync(
        OutboxMessageDetails details, string outcome, string reason, Func<string, CancellationToken, Task<bool>> operation)
    {
        var result = await PersistAsync(details, outcome, ct => operation(reason, ct)).ConfigureAwait(false);
        if (result is not null)
        {
            return result;
        }

        // The first round failed, so it may have committed; let a zero-row result re-read the row (FU-05).
        return await PersistAsync(details, outcome, ct => operation(ErrorText.Fallback(outcome), ct), priorFailure: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Records an outcome with a few quick retries. It deliberately does not use the stopping token, so outcomes of deliveries that
    /// finished during shutdown are still recorded.
    /// </summary>
    /// <returns><see langword="true"/> if recorded, <see langword="false"/> if the lease was lost, <see langword="null"/> if the database failed.</returns>
    private async Task<bool?> PersistAsync(
        OutboxMessageDetails details, string outcome, Func<CancellationToken, Task<bool>> operation, bool priorFailure = false)
    {
        var previousAttemptFailed = priorFailure;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(PersistTimeout, _time);
                var updated = await operation(timeout.Token).ConfigureAwait(false);
                if (!updated && previousAttemptFailed && await WasRecordedByThisClaimAsync(details, outcome).ConfigureAwait(false))
                {
                    // An earlier attempt committed but its acknowledgement was lost (timeout or dropped connection), so this retry
                    // matched no rows. That is not a lost lease (CR-10).
                    return true;
                }

                if (!updated)
                {
                    _metrics.LeaseLost();
                    Log.LeaseLost(_logger, details.Id, details.MessageType, outcome);
                }

                return updated;
            }
            catch (Exception exception) when (attempt < PersistAttempts)
            {
                previousAttemptFailed = true;
                Log.OutcomePersistRetry(_logger, exception, details.Id, outcome, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), _time, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Log.OutcomePersistFailed(_logger, exception, details.Id, outcome);
                return null;
            }
        }
    }

    /// <summary>
    /// After an ambiguous failure, checks whether the row already carries the outcome that <b>this claim</b> tried to record.
    /// <c>locked_by</c> (kept on completion) identifies the worker, and the attempt count pins it to this claim: any reclaim,
    /// including one by the same worker or a replica sharing a configured WorkerId, increments it (FU-05). Release clears
    /// <c>locked_by</c>.
    /// </summary>
    private async Task<bool> WasRecordedByThisClaimAsync(OutboxMessageDetails details, string outcome)
    {
        var messageId = details.Id;
        var expected = outcome switch
        {
            "delivered" => OutboxMessageStatus.Delivered,
            "dead-lettered" => OutboxMessageStatus.DeadLettered,
            _ => OutboxMessageStatus.Pending, // retry, or returned after shutdown
        };

        try
        {
            using var timeout = new CancellationTokenSource(PersistTimeout, _time);
            var row = await _store.GetAsync(messageId, timeout.Token).ConfigureAwait(false);
            return row is not null
                && row.Status == expected
                && row.AttemptCount == details.AttemptCount
                && string.Equals(row.LockedBy, _workerId, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false; // cannot tell; report conservatively as lease lost
        }
    }

    /// <summary>
    /// Returns a message whose handler was interrupted by the host shutdown timeout. The handler did run (and may have had its side
    /// effect), so the attempt stays counted: a message whose handler always outlasts the shutdown window must still reach
    /// dead-letter eventually (invariant 10, CR-06). Implemented as a zero-delay retry, so it is claimable immediately.
    /// </summary>
    private async Task ReturnInterruptedAsync(OutboxMessageDetails details, Guid lockToken)
    {
        var returned = await PersistAsync(
            details,
            "returned after shutdown",
            ct => _store.ScheduleRetryAsync(details.Id, lockToken, TimeSpan.Zero, InterruptedByShutdownReason, ct))
            .ConfigureAwait(false);
        if (returned == true)
        {
            _metrics.Released(1);
            Log.Released(_logger, 1, "interrupted by shutdown; attempt kept");
        }
    }

    private async Task ReleaseAsync(IReadOnlyList<OutboxMessageDetails> messages, int startIndex, Guid lockToken, string reason)
    {
        var released = 0;
        for (var i = startIndex; i < messages.Count; i++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(PersistTimeout, _time);
                if (await _store.ReleaseAsync(messages[i].Id, lockToken, timeout.Token).ConfigureAwait(false))
                {
                    released++;
                }
            }
            catch (Exception exception)
            {
                Log.ReleaseFailed(_logger, exception, messages[i].Id);
            }
        }

        if (released > 0)
        {
            _metrics.Released(released);
            Log.Released(_logger, released, reason);
        }
    }

    private bool IsPermanent(Exception exception)
    {
        if (_retry.IsPermanentFailure is not { } predicate)
        {
            return false;
        }

        try
        {
            return predicate(exception);
        }
        catch (Exception)
        {
            return false; // a faulty classifier must not break the pipeline; treat as transient
        }
    }

    private static OutboxMessage ToMessage(OutboxMessageDetails details) => new()
    {
        Id = details.Id,
        MessageType = details.MessageType,
        Destination = details.Destination,
        Payload = details.Payload,
        ContentType = details.ContentType,
        Headers = details.Headers,
        CorrelationId = details.CorrelationId,
        CausationId = details.CausationId,
        CreatedAt = details.CreatedAt,
        ScheduledAt = details.ScheduledAt,
        Attempt = details.AttemptCount,
    };

    private static Activity? StartDeliveryActivity(OutboxMessage message)
    {
        ActivityContext parent = default;
        if (message.Headers.TryGetValue(Outbox.TraceParentHeader, out var traceParent))
        {
            message.Headers.TryGetValue(Outbox.TraceStateHeader, out var traceState);
            ActivityContext.TryParse(traceParent, traceState, isRemote: true, out parent);
        }

        var activity = DbRelayDiagnostics.ActivitySource.StartActivity("DbRelay deliver", ActivityKind.Consumer, parent);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", "dbrelay");
            activity.SetTag("messaging.operation.type", "process");
            activity.SetTag("messaging.message.id", message.Id.ToString());
            activity.SetTag("dbrelay.message.type", message.MessageType);
            activity.SetTag("dbrelay.attempt", message.Attempt);
            if (message.Destination is not null)
            {
                activity.SetTag("messaging.destination.name", message.Destination);
            }

            if (message.CorrelationId is not null)
            {
                activity.SetTag("messaging.message.conversation_id", message.CorrelationId);
            }
        }

        return activity;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private TimeSpan ErrorBackoff(int consecutiveFailures)
    {
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var ms = Math.Min(_options.MaxErrorBackoff.TotalMilliseconds, 1000 * Math.Pow(2, exponent));
        return Jitter(TimeSpan.FromMilliseconds(ms));
    }

#pragma warning disable CA5394 // Jitter does not need cryptographic randomness.
    private static TimeSpan Jitter(TimeSpan value) => value * (0.9 + (Random.Shared.NextDouble() * 0.2));
#pragma warning restore CA5394

    private static string CreateWorkerId()
    {
        var id = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
        return id.Length <= OutboxLimits.MaxWorkerIdLength ? id : id[..OutboxLimits.MaxWorkerIdLength];
    }

    private readonly record struct ClaimedBatch(IReadOnlyList<OutboxMessageDetails> Messages, Guid LockToken, long ClaimStartedTimestamp);
}
