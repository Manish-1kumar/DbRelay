using DbRelay.Storage;

namespace DbRelay;

/// <summary>Validates operator requests and delegates them to the storage provider.</summary>
internal sealed class OutboxAdmin : IOutboxAdmin
{
    private readonly IOutboxStore _store;

    public OutboxAdmin(IOutboxStore store)
    {
        _store = store;
    }

    public Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _store.GetAsync(messageId, cancellationToken);

    public Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip, nameof(query));
        if (query.Take is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), query.Take, "OutboxQuery.Take must be between 1 and 1000.");
        }

        if (query.Status is { } status && !Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(query), status, "OutboxQuery.Status is not a valid status.");
        }

        return _store.QueryAsync(query, cancellationToken);
    }

    public Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default) =>
        _store.GetStatisticsAsync(cancellationToken);

    public Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken = default) =>
        _store.RequeueAsync(messageId, cancellationToken);

    public Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken = default)
    {
        if (maxCount is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount), maxCount, "maxCount must be between 1 and 10000.");
        }

        return _store.RequeueDeadLetteredAsync(messageType, maxCount, cancellationToken);
    }

    public async Task<int> PurgeDeliveredAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(olderThan, TimeSpan.FromDays(3650)); // same bound for every provider (CR-13)
        const int BatchSize = 5000;
        var total = 0;
        int deleted;
        do
        {
            deleted = await _store.DeleteDeliveredAsync(olderThan, BatchSize, cancellationToken).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == BatchSize);

        return total;
    }
}
