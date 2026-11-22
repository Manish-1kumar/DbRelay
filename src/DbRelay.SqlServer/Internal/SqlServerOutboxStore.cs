using System.Data;
using System.Data.Common;
using System.Runtime.InteropServices;
using DbRelay.Storage;
using Microsoft.Data.SqlClient;

namespace DbRelay.SqlServer.Internal;

/// <summary>The SQL Server <see cref="IOutboxStore"/>.</summary>
internal sealed class SqlServerOutboxStore : IOutboxStore, IOutboxBatchStore
{
    /// <summary>Upper bound for intervals so <c>seconds</c> always fits DATEADD's <c>int</c> (about 54 years).</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(20000);

    private readonly SqlServerConnectionSource _connections;
    private readonly SqlServerOutboxSql _sql;

    public SqlServerOutboxStore(SqlServerConnectionSource connections, SqlServerOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    // ---- Enqueue ---------------------------------------------------------------------------------------------------

    public void ValidateCallerResources(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not SqlConnection)
        {
            throw new ArgumentException(
                $"DbRelay is configured for SQL Server and requires a Microsoft.Data.SqlClient.SqlConnection; got '{connection.GetType().FullName}'.",
                nameof(connection));
        }

        if (transaction is not null and not SqlTransaction)
        {
            throw new ArgumentException(
                $"DbRelay is configured for SQL Server and requires a Microsoft.Data.SqlClient.SqlTransaction; got '{transaction.GetType().FullName}'.",
                nameof(transaction));
        }
    }

    public Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ValidateCallerResources(connection, transaction);
        ArgumentNullException.ThrowIfNull(records);
        return InsertCoreAsync((SqlConnection)connection, (SqlTransaction?)transaction, records, cancellationToken);
    }

    public Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        return _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                await InsertCoreAsync(connection, null, records, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Inserts in statements of at most <see cref="SqlServerOutboxSql.MaxInsertRowsPerStatement"/> rows (SQL Server accepts at most
    /// 2100 parameters per request). With the caller's transaction, every statement runs in it. Without a transaction, a single
    /// statement is atomic on its own; when several are needed, they run in a transaction DbRelay begins and commits itself so that
    /// the call stays all-or-nothing. A caller-owned transaction is never committed, rolled back or disposed.
    /// </summary>
    private async Task InsertCoreAsync(SqlConnection connection, SqlTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        if (records.Count <= SqlServerOutboxSql.MaxInsertRowsPerStatement)
        {
            await InsertChunkAsync(connection, transaction, records, 0, records.Count, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (transaction is not null)
        {
            await InsertChunksAsync(connection, transaction, records, cancellationToken).ConfigureAwait(false);
            return;
        }

        var own = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (own.ConfigureAwait(false))
        {
            await InsertChunksAsync(connection, own, records, cancellationToken).ConfigureAwait(false);
            await own.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertChunksAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        for (var start = 0; start < records.Count; start += SqlServerOutboxSql.MaxInsertRowsPerStatement)
        {
            var count = Math.Min(SqlServerOutboxSql.MaxInsertRowsPerStatement, records.Count - start);
            await InsertChunkAsync(connection, transaction, records, start, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task InsertChunkAsync(
        SqlConnection connection, SqlTransaction? transaction, IReadOnlyList<OutboxRecord> records, int start, int count, CancellationToken cancellationToken)
    {
        var command = _connections.CreateCommand(connection, _sql.GetInsert(count), transaction);
        await using (command.ConfigureAwait(false))
        {
            AddInsertParameters(command.Parameters, records, start, count);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds the parameters of <see cref="SqlServerOutboxSql.GetInsert"/> for <paramref name="count"/> records.</summary>
    internal static void AddInsertParameters(SqlParameterCollection parameters, IReadOnlyList<OutboxRecord> records, int start, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var record = records[start + i];
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Add(parameters, "@id" + suffix, SqlDbType.UniqueIdentifier, 0, record.Id);
            Add(parameters, "@mt" + suffix, SqlDbType.NVarChar, OutboxLimits.MaxMessageTypeLength, record.MessageType);
            Add(parameters, "@de" + suffix, SqlDbType.NVarChar, OutboxLimits.MaxDestinationLength, record.Destination);
            Add(parameters, "@pl" + suffix, SqlDbType.VarBinary, -1, ToArray(record.Payload));
            Add(parameters, "@ct" + suffix, SqlDbType.NVarChar, OutboxLimits.MaxContentTypeLength, record.ContentType);
            Add(parameters, "@hd" + suffix, SqlDbType.NVarChar, -1, record.HeadersJson);
            Add(parameters, "@co" + suffix, SqlDbType.NVarChar, OutboxLimits.MaxCorrelationIdLength, record.CorrelationId);
            Add(parameters, "@ca" + suffix, SqlDbType.NVarChar, OutboxLimits.MaxCorrelationIdLength, record.CausationId);
            AddDateTime(parameters, "@da" + suffix, record.DeliverAt is { } at ? TruncateToMilliseconds(at.UtcDateTime) : null, scale: 3);
            Add(parameters, "@dl" + suffix, SqlDbType.BigInt, 0, record.Delay is { } delay ? ToMilliseconds(delay) : null);
        }
    }

    // ---- Claim and transitions -------------------------------------------------------------------------------------

    public Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize < 1 || request.BatchSize > OutboxLimits.MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.BatchSize, $"ClaimRequest.BatchSize must be between 1 and {OutboxLimits.MaxClaimBatchSize}.");
        }

        return _connections.ExecuteAsync<IReadOnlyList<OutboxMessageDetails>>(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.Claim);
                await using (command.ConfigureAwait(false))
                {
                    Add(command.Parameters, "@batch_size", SqlDbType.Int, 0, request.BatchSize);
                    Add(command.Parameters, "@lock_token", SqlDbType.UniqueIdentifier, 0, request.LockToken);
                    Add(command.Parameters, "@worker_id", SqlDbType.NVarChar, OutboxLimits.MaxWorkerIdLength, Truncate(request.WorkerId, OutboxLimits.MaxWorkerIdLength));
                    Add(command.Parameters, "@lease_ms", SqlDbType.BigInt, 0, ToMilliseconds(request.LeaseDuration));

                    var claimed = new List<(DateTime PreviousAvailableAt, long Sequence, OutboxMessageDetails Message)>(request.BatchSize);
                    var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            claimed.Add((reader.GetDateTime(DetailColumnCount), reader.GetInt64(DetailColumnCount + 1), ReadDetails(reader)));
                        }
                    }

                    // OUTPUT does not guarantee row order; return the claim order (oldest available_at first).
                    claimed.Sort(static (a, b) =>
                    {
                        var byTime = a.PreviousAvailableAt.CompareTo(b.PreviousAvailableAt);
                        return byTime != 0 ? byTime : a.Sequence.CompareTo(b.Sequence);
                    });

                    var result = new OutboxMessageDetails[claimed.Count];
                    for (var i = 0; i < result.Length; i++)
                    {
                        result[i] = claimed[i].Message;
                    }

                    return result;
                }
            },
            cancellationToken);
    }

    public Task<bool> MarkDeliveredAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        GuardedAsync(_sql.MarkDelivered, messageId, lockToken, static _ => { }, cancellationToken);

    public Task<bool> ScheduleRetryAsync(Guid messageId, Guid lockToken, TimeSpan delay, string error, CancellationToken cancellationToken) =>
        GuardedAsync(
            _sql.ScheduleRetry,
            messageId,
            lockToken,
            parameters =>
            {
                Add(parameters, "@delay_ms", SqlDbType.BigInt, 0, ToMilliseconds(delay));
                AddError(parameters, error);
            },
            cancellationToken);

    public Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken) =>
        GuardedAsync(_sql.MarkDeadLettered, messageId, lockToken, parameters => AddError(parameters, error), cancellationToken);

    public Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        GuardedAsync(_sql.Release, messageId, lockToken, static _ => { }, cancellationToken);

    private async Task<bool> GuardedAsync(
        string sql, Guid messageId, Guid lockToken, Action<SqlParameterCollection> addParameters, CancellationToken cancellationToken)
    {
        var rows = await ExecuteCountAsync(
            sql,
            parameters =>
            {
                Add(parameters, "@id", SqlDbType.UniqueIdentifier, 0, messageId);
                Add(parameters, "@lock_token", SqlDbType.UniqueIdentifier, 0, lockToken);
                addParameters(parameters);
            },
            cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(items, _sql.GetMarkDeliveredBatch, (id, token, ct) => MarkDeliveredAsync(id, token, ct), cancellationToken);

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(items, _sql.GetReleaseBatch, (id, token, ct) => ReleaseAsync(id, token, ct), cancellationToken);

    private async Task<IReadOnlyCollection<OutboxAcknowledgement>> GuardedBatchAsync(
        IReadOnlyList<OutboxAcknowledgement> items,
        Func<int, string> statementForBucket,
        Func<Guid, Guid, CancellationToken, Task<bool>> single,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > OutboxLimits.MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(items), items.Count, $"At most {OutboxLimits.MaxClaimBatchSize} items per batch.");
        }

        var updated = new HashSet<OutboxAcknowledgement>();
        for (var offset = 0; offset < items.Count; offset += SqlServerOutboxSql.MaxBatchStatementItems)
        {
            var count = Math.Min(SqlServerOutboxSql.MaxBatchStatementItems, items.Count - offset);
            if (count == 1)
            {
                // A one-item chunk takes the cheaper single-row statement.
                if (await single(items[offset].MessageId, items[offset].LockToken, cancellationToken).ConfigureAwait(false))
                {
                    updated.Add(items[offset]);
                }

                continue;
            }

            var bucket = SqlServerOutboxSql.BucketFor(count);
            var start = offset;
            // Results are collected per attempt and merged only when the attempt succeeds: a deadlock-victim attempt is rolled back,
            // so nothing it output may count (BA-08).
            var chunkResult = await _connections.ExecuteAsync<List<OutboxAcknowledgement>>(
                async (connection, ct) =>
                {
                    var attemptResult = new List<OutboxAcknowledgement>(count);
                    var command = _connections.CreateCommand(connection, statementForBucket(bucket));
                    await using (command.ConfigureAwait(false))
                    {
                        for (var i = 0; i < bucket; i++)
                        {
                            var item = items[start + Math.Min(i, count - 1)]; // pad by repeating the last item
                            Add(command.Parameters, $"@i{i}", SqlDbType.UniqueIdentifier, 0, item.MessageId);
                            Add(command.Parameters, $"@t{i}", SqlDbType.UniqueIdentifier, 0, item.LockToken);
                        }

                        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                        await using (reader.ConfigureAwait(false))
                        {
                            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                            {
                                attemptResult.Add(new OutboxAcknowledgement(reader.GetGuid(0), reader.GetGuid(1)));
                            }
                        }

                        return attemptResult;
                    }
                },
                cancellationToken).ConfigureAwait(false);
            updated.UnionWith(chunkResult);
        }

        return updated;
    }

    // ---- Maintenance and administration ------------------------------------------------------------------------------

    public async Task<int> DeleteDeliveredAsync(TimeSpan olderThan, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        var olderThanMs = -ToMilliseconds(olderThan);
        var total = 0;
        while (total < maxCount)
        {
            var chunk = Math.Min(maxCount - total, SqlServerOutboxSql.MaxRowsPerBulkStatement);
            var deleted = await ExecuteCountAsync(
                _sql.DeleteDelivered,
                parameters =>
                {
                    Add(parameters, "@max_count", SqlDbType.Int, 0, chunk);
                    Add(parameters, "@older_than_ms", SqlDbType.BigInt, 0, olderThanMs);
                },
                cancellationToken).ConfigureAwait(false);

            total += deleted;
            if (deleted < chunk)
            {
                break;
            }
        }

        return total;
    }

    public Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.SelectById);
                await using (command.ConfigureAwait(false))
                {
                    Add(command.Parameters, "@id", SqlDbType.UniqueIdentifier, 0, messageId);
                    var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDetails(reader) : null;
                    }
                }
            },
            cancellationToken);

    public Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MessageType is { Length: > OutboxLimits.MaxMessageTypeLength })
        {
            // No stored type can be longer; do not let a sized parameter silently truncate the filter.
            return Task.FromResult<IReadOnlyList<OutboxMessageDetails>>([]);
        }

        var text = _sql.GetQuery(query.Status is not null, query.MessageType is not null, query.CreatedAfter is not null, query.CreatedBefore is not null);

        return _connections.ExecuteAsync<IReadOnlyList<OutboxMessageDetails>>(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, text);
                await using (command.ConfigureAwait(false))
                {
                    var parameters = command.Parameters;
                    if (query.Status is { } status)
                    {
                        Add(parameters, "@status", SqlDbType.TinyInt, 0, (byte)status);
                    }

                    if (query.MessageType is { } messageType)
                    {
                        Add(parameters, "@message_type", SqlDbType.NVarChar, OutboxLimits.MaxMessageTypeLength, messageType);
                    }

                    if (query.CreatedAfter is { } after)
                    {
                        AddDateTime(parameters, "@created_after", after.UtcDateTime, scale: 7);
                    }

                    if (query.CreatedBefore is { } before)
                    {
                        AddDateTime(parameters, "@created_before", before.UtcDateTime, scale: 7);
                    }

                    Add(parameters, "@skip", SqlDbType.Int, 0, query.Skip);
                    Add(parameters, "@take", SqlDbType.Int, 0, query.Take);

                    var results = new List<OutboxMessageDetails>(Math.Min(query.Take, 128));
                    var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            results.Add(ReadDetails(reader));
                        }
                    }

                    return results;
                }
            },
            cancellationToken);
    }

    public Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.Statistics);
                await using (command.ConfigureAwait(false))
                {
                    var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("The outbox statistics query returned no row.");
                        }

                        return new OutboxStatistics
                        {
                            Pending = reader.GetInt64(0),
                            Processing = reader.GetInt64(1),
                            Delivered = reader.GetInt64(2),
                            DeadLettered = reader.GetInt64(3),
                            OldestDueAge = reader.IsDBNull(4) ? null : TimeSpan.FromMilliseconds(Math.Max(0, reader.GetInt64(4))),
                        };
                    }
                }
            },
            cancellationToken);

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) =>
        await ExecuteCountAsync(
            _sql.Requeue,
            parameters => Add(parameters, "@id", SqlDbType.UniqueIdentifier, 0, messageId),
            cancellationToken).ConfigureAwait(false) == 1;

    public async Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (messageType is { Length: > OutboxLimits.MaxMessageTypeLength })
        {
            return 0;
        }

        var sql = messageType is null ? _sql.RequeueDeadLettered : _sql.RequeueDeadLetteredOfType;
        var total = 0;
        while (total < maxCount)
        {
            var chunk = Math.Min(maxCount - total, SqlServerOutboxSql.MaxRowsPerBulkStatement);
            var requeued = await ExecuteCountAsync(
                sql,
                parameters =>
                {
                    Add(parameters, "@max_count", SqlDbType.Int, 0, chunk);
                    if (messageType is not null)
                    {
                        Add(parameters, "@message_type", SqlDbType.NVarChar, OutboxLimits.MaxMessageTypeLength, messageType);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            total += requeued;
            if (requeued < chunk)
            {
                break;
            }
        }

        return total;
    }

    public Task CheckHealthAsync(CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.HealthCheck);
                await using (command.ConfigureAwait(false))
                {
                    await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    return true;
                }
            },
            cancellationToken);

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private const int DetailColumnCount = 18;

    private Task<int> ExecuteCountAsync(string sql, Action<SqlParameterCollection> addParameters, CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    addParameters(command.Parameters);
                    var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
                }
            },
            cancellationToken);

    /// <summary>
    /// Maps the current row. A row that cannot be mapped becomes an <see cref="UnreadableRow"/> placeholder instead of failing the
    /// whole result set. Otherwise one bad row would wedge its claim batch in Processing forever (CR-01 / FU-01).
    /// </summary>
    private static OutboxMessageDetails ReadDetails(SqlDataReader reader)
    {
        try
        {
            return ReadDetailsCore(reader);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UnreadableRow.Create(
                reader.GetGuid(0),
                TryRead(() => (OutboxMessageStatus)reader.GetByte(8)),
                TryRead(() => reader.GetInt32(9)),
                exception);
        }
    }

    private static T? TryRead<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OutboxMessageDetails ReadDetailsCore(SqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        MessageType = reader.GetString(1),
        Destination = GetNullableString(reader, 2),
        Payload = reader.GetFieldValue<byte[]>(3),
        ContentType = reader.GetString(4),
        Headers = OutboxHeaders.Deserialize(GetNullableString(reader, 5)),
        CorrelationId = GetNullableString(reader, 6),
        CausationId = GetNullableString(reader, 7),
        Status = (OutboxMessageStatus)reader.GetByte(8),
        AttemptCount = reader.GetInt32(9),
        CreatedAt = ToUtc(reader.GetDateTime(10)),
        ScheduledAt = ToUtc(reader.GetDateTime(11)),
        AvailableAt = ToUtc(reader.GetDateTime(12)),
        LastAttemptAt = GetNullableUtc(reader, 13),
        DeliveredAt = GetNullableUtc(reader, 14),
        DeadLetteredAt = GetNullableUtc(reader, 15),
        LastError = GetNullableString(reader, 16),
        LockedBy = GetNullableString(reader, 17),
    };

    private static string? GetNullableString(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableUtc(SqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ToUtc(reader.GetDateTime(ordinal));

    private static DateTimeOffset ToUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    private static DateTime TruncateToMilliseconds(DateTime utc) =>
        new(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);

    /// <summary>Whole milliseconds, clamped to [0, <see cref="MaxInterval"/>].</summary>
    private static long ToMilliseconds(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return 0;
        }

        return (interval > MaxInterval ? MaxInterval : interval).Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private static byte[] ToArray(ReadOnlyMemory<byte> payload) =>
        MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
            ? array
            : payload.ToArray();

    private static void AddError(SqlParameterCollection parameters, string? error) =>
        Add(parameters, "@error", SqlDbType.NVarChar, OutboxLimits.MaxErrorLength, Truncate(error, OutboxLimits.MaxErrorLength));

    private static void Add(SqlParameterCollection parameters, string name, SqlDbType type, int size, object? value)
    {
        var parameter = new SqlParameter(name, type) { Value = value ?? DBNull.Value };
        if (size != 0)
        {
            parameter.Size = size;
        }

        parameters.Add(parameter);
    }

    private static void AddDateTime(SqlParameterCollection parameters, string name, DateTime? value, byte scale) =>
        parameters.Add(new SqlParameter(name, SqlDbType.DateTime2) { Scale = scale, Value = value is { } v ? v : DBNull.Value });
}
