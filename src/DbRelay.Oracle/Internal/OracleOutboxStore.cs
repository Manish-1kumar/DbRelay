using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using DbRelay.Storage;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace DbRelay.Oracle.Internal;

/// <summary>
/// The Oracle <see cref="IOutboxStore"/>. Every operation on a DbRelay-owned connection is one autocommitted statement or PL/SQL
/// block, except multi-statement inserts, which run in a transaction. <see cref="InsertAsync"/> runs on the caller's connection
/// and transaction.
/// </summary>
internal sealed class OracleOutboxStore : IOutboxStore, IOutboxBatchStore
{
    /// <summary>Upper bound for intervals (about 54 years), far beyond any configurable delay or lease.</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(20000);

    private readonly OracleConnectionSource _connections;
    private readonly OracleOutboxSql _sql;

    public OracleOutboxStore(OracleConnectionSource connections, OracleOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    /// <summary>ORA-00060 victims retried by this store (see <see cref="OracleConnectionSource.ExecuteAsync{T}"/>).</summary>
    internal long DeadlockRetries => _connections.DeadlockRetries;

    /// <summary>
    /// Test seam (OR-01): how long the claim holds its cursor open between choosing candidate rows and locking them. Always zero in
    /// production; tests set it to change a row inside that gap.
    /// </summary>
    internal TimeSpan ClaimTestPause { get; set; }

    // ---- Enqueue ---------------------------------------------------------------------------------------------------

    public void ValidateCallerResources(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not OracleConnection)
        {
            throw new ArgumentException(
                $"DbRelay is configured for Oracle and requires an Oracle.ManagedDataAccess.Client.OracleConnection; got '{connection.GetType().FullName}'.",
                nameof(connection));
        }

        if (transaction is not null and not OracleTransaction)
        {
            throw new ArgumentException(
                $"DbRelay is configured for Oracle and requires an Oracle.ManagedDataAccess.Client.OracleTransaction; got '{transaction.GetType().FullName}'.",
                nameof(transaction));
        }
    }

    public Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ValidateCallerResources(connection, transaction);
        ArgumentNullException.ThrowIfNull(records);

        // Exactly the caller's connection; DbRelay never opens, closes, commits, rolls back or disposes it. ODP.NET runs every
        // command on a connection in that connection's transaction, if it has one, so the rows commit or roll back with it.
        return InsertOnCallerConnectionAsync((OracleConnection)connection, records, cancellationToken);
    }

    private async Task InsertOnCallerConnectionAsync(OracleConnection connection, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            await InsertCoreAsync(connection, records, cancellationToken).ConfigureAwait(false);
        }
        catch (OracleException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The outbox insert was cancelled.", ex, cancellationToken); // OR-09
        }
    }

    public Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        return _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                await InsertCoreAsync(connection, records, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// Inserts with one statement per <see cref="OutboxLimits.MaxEnqueueBatchSize"/> rows: an <c>INSERT ALL</c>, or a PL/SQL block of them
    /// (see <see cref="OracleOutboxSql.GetInsert"/>). Each statement is atomic on its own, so DbRelay never begins, commits or rolls
    /// back a transaction. DbRelay limits one enqueue call to <see cref="OutboxLimits.MaxEnqueueBatchSize"/> messages, so a call is one
    /// statement.
    /// </summary>
    private async Task InsertCoreAsync(OracleConnection connection, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        for (var start = 0; start < records.Count; start += OutboxLimits.MaxEnqueueBatchSize)
        {
            var count = Math.Min(OutboxLimits.MaxEnqueueBatchSize, records.Count - start);
            var command = _connections.CreateCommand(connection, _sql.GetInsert(count));
            await using (command.ConfigureAwait(false))
            {
                try
                {
                    await ExecuteInsertAsync(command, records, start, count, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    OracleConnectionSource.DisposeParameters(command);
                }
            }
        }
    }

    private static async Task ExecuteInsertAsync(OracleCommand command, IReadOnlyList<OutboxRecord> records, int start, int count, CancellationToken cancellationToken)
    {
        var parameters = command.Parameters;
        for (var i = 0; i < count; i++)
        {
            var record = records[start + i];
            var suffix = i.ToString(CultureInfo.InvariantCulture);
            parameters.Add(Raw("id" + suffix, record.Id));
            parameters.Add(Text("mt" + suffix, record.MessageType));
            parameters.Add(Text("de" + suffix, record.Destination));
            parameters.Add(new OracleParameter("pl" + suffix, OracleDbType.Blob) { Value = ToArray(record.Payload) });
            parameters.Add(Text("ct" + suffix, record.ContentType));
            parameters.Add(Clob("hd" + suffix, record.HeadersJson));
            parameters.Add(Text("co" + suffix, record.CorrelationId));
            parameters.Add(Text("ca" + suffix, record.CausationId));
            parameters.Add(Timestamp("da" + suffix, record.DeliverAt?.UtcDateTime));
            parameters.Add(Number("dl" + suffix, record.Delay is { } delay ? ToMilliseconds(delay) : null));
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---- Claim and transitions -------------------------------------------------------------------------------------

    public Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BatchSize < 1 || request.BatchSize > OutboxLimits.MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request), request.BatchSize, $"ClaimRequest.BatchSize must be between 1 and {OutboxLimits.MaxClaimBatchSize}.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxPayloadBytes, 1L, nameof(request));
        return _connections.ExecuteAsync<IReadOnlyList<OutboxMessageDetails>>(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.Claim);
                await using (command.ConfigureAwait(false))
                {
                    try
                    {
                        return await ExecuteClaimAsync(connection, command, request, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        OracleConnectionSource.DisposeParameters(command);
                    }
                }
            },
            cancellationToken);
    }

    private async Task<IReadOnlyList<OutboxMessageDetails>> ExecuteClaimAsync(
        OracleConnection connection, OracleCommand command, ClaimRequest request, CancellationToken ct)
    {
        var parameters = command.Parameters;
        parameters.Add(Number("lease_ms", ToMilliseconds(request.LeaseDuration)));
        parameters.Add(Number("max_payload_bytes", request.MaxPayloadBytes));
        parameters.Add(Raw("lock_token", request.LockToken));
        parameters.Add(Text("worker_id", Truncate(request.WorkerId, OutboxLimits.MaxWorkerIdLength)));
        parameters.Add(Number("batch_size", request.BatchSize));
        parameters.Add(Number("pause_ms", ToMilliseconds(ClaimTestPause)));
        var ids = parameters.Add(new OracleParameter("claimed_ids", OracleDbType.Varchar2, 32767) { Direction = ParameterDirection.Output });

        // The claim commits here (autocommit): the rows are leased to this token and their attempts are counted.
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        var order = ParseIds(ids.Value);
        if (order.Count == 0)
        {
            return [];
        }

        // OR-11: read the leased rows with a plain query by primary key, on the same connection. No other worker can change them
        // before the lease ends, and this command fetches the payloads and texts inline.
        var bucket = OracleOutboxSql.ClaimedRowBucketFor(order.Count);
        var rows = new Dictionary<Guid, OutboxMessageDetails>(order.Count);
        var select = _connections.CreateCommand(connection, _sql.GetClaimedRows(bucket));
        await using (select.ConfigureAwait(false))
        {
            try
            {
                for (var i = 0; i < bucket; i++)
                {
                    select.Parameters.Add(Raw("i" + i.ToString(CultureInfo.InvariantCulture), order[Math.Min(i, order.Count - 1)])); // pad by repeating the last ID
                }

                var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var details = ReadDetails((OracleDataReader)reader);
                        rows[details.Id] = details;
                    }
                }
            }
            finally
            {
                OracleConnectionSource.DisposeParameters(select);
            }
        }

        // Return the claim order (oldest AVAILABLE_AT first).
        var result = new List<OutboxMessageDetails>(order.Count);
        foreach (var id in order)
        {
            if (rows.TryGetValue(id, out var details))
            {
                result.Add(details);
            }
        }

        return result;
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
                parameters.Add(Number("delay_ms", ToMilliseconds(delay)));
                parameters.Add(Clob("error", Truncate(error, OutboxLimits.MaxErrorLength)));
            },
            cancellationToken);

    public Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken) =>
        GuardedAsync(
            _sql.MarkDeadLettered,
            messageId,
            lockToken,
            parameters => parameters.Add(Clob("error", Truncate(error, OutboxLimits.MaxErrorLength))),
            cancellationToken);

    public Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        GuardedAsync(_sql.Release, messageId, lockToken, static _ => { }, cancellationToken);

    /// <summary>Every post-claim transition is guarded by <c>WHERE ID = :id AND LOCK_TOKEN = :lock_token</c>: zero rows means the lease was lost.</summary>
    private async Task<bool> GuardedAsync(
        string sql, Guid messageId, Guid lockToken, Action<OracleParameterCollection> addParameters, CancellationToken cancellationToken)
    {
        var rows = await ExecuteNonQueryAsync(
            sql,
            parameters =>
            {
                parameters.Add(Raw("id", messageId));
                parameters.Add(Raw("lock_token", lockToken));
                addParameters(parameters);
            },
            cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(_sql.MarkDeliveredBatch, items, cancellationToken);

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(_sql.ReleaseBatch, items, cancellationToken);

    private async Task<IReadOnlyCollection<OutboxAcknowledgement>> GuardedBatchAsync(
        string sql, IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return [];
        }

        if (items.Count > OutboxLimits.MaxClaimBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(items), items.Count, $"At most {OutboxLimits.MaxClaimBatchSize} items per batch.");
        }

        // BA-07: a canonical order, the byte order of the RAW(16) primary key, so two batches touching the same rows lock them in the
        // same order and cannot deadlock on each other.
        var ordered = items
            .Select(item => (Item: item, Id: item.MessageId.ToByteArray(), Token: item.LockToken.ToByteArray()))
            .OrderBy(x => x.Id, ByteOrder.Instance)
            .ThenBy(x => x.Token, ByteOrder.Instance)
            .ToArray();
        var ids = string.Concat(ordered.Select(x => Convert.ToHexString(x.Id)));
        var tokens = string.Concat(ordered.Select(x => Convert.ToHexString(x.Token)));

        return await _connections.ExecuteAsync<IReadOnlyCollection<OutboxAcknowledgement>>(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    command.Parameters.Add(new OracleParameter("ids", OracleDbType.Varchar2, ids.Length) { Value = ids });
                    command.Parameters.Add(new OracleParameter("tokens", OracleDbType.Varchar2, tokens.Length) { Value = tokens });
                    var flags = command.Parameters.Add(
                        new OracleParameter("updated", OracleDbType.Varchar2, OutboxLimits.MaxClaimBatchSize) { Direction = ParameterDirection.Output });
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    var done = flags.Value is OracleString { IsNull: false } text ? text.Value : string.Empty;
                    var updated = new HashSet<OutboxAcknowledgement>();
                    for (var i = 0; i < ordered.Length && i < done.Length; i++)
                    {
                        if (done[i] == '1')
                        {
                            updated.Add(ordered[i].Item);
                        }
                    }

                    return updated;
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ---- Maintenance and administration ------------------------------------------------------------------------------

    public async Task<int> DeleteDeliveredAsync(TimeSpan olderThan, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

        var total = 0;
        while (total < maxCount)
        {
            var chunk = Math.Min(maxCount - total, OracleOutboxSql.MaxRowsPerBulkStatement);
            var deleted = await ExecuteAffectedAsync(
                _sql.DeleteDelivered,
                parameters =>
                {
                    parameters.Add(Number("older_than_ms", ToMilliseconds(olderThan)));
                    parameters.Add(Number("max_count", chunk));
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
                    command.Parameters.Add(Raw("id", messageId));
                    var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadDetails((OracleDataReader)reader) : null;
                    }
                }
            },
            cancellationToken);

    public Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.MessageType is { Length: > OutboxLimits.MaxMessageTypeLength })
        {
            // No stored type can be longer.
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
                        parameters.Add(Number("status", (int)status));
                    }

                    if (query.MessageType is { } messageType)
                    {
                        parameters.Add(Text("message_type", messageType));
                    }

                    if (query.CreatedAfter is { } after)
                    {
                        parameters.Add(Timestamp("created_after", after.UtcDateTime));
                    }

                    if (query.CreatedBefore is { } before)
                    {
                        parameters.Add(Timestamp("created_before", before.UtcDateTime));
                    }

                    parameters.Add(Number("skip", query.Skip));
                    parameters.Add(Number("take", query.Take));

                    var results = new List<OutboxMessageDetails>(Math.Min(query.Take, 128));
                    var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            results.Add(ReadDetails((OracleDataReader)reader));
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
                            Pending = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                            Processing = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                            Delivered = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                            DeadLettered = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                            OldestDueAge = reader.IsDBNull(4)
                                ? null
                                : TimeSpan.FromMilliseconds(Math.Max(0, Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture))),
                        };
                    }
                }
            },
            cancellationToken);

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) =>
        await ExecuteNonQueryAsync(_sql.Requeue, parameters => parameters.Add(Raw("id", messageId)), cancellationToken).ConfigureAwait(false) == 1;

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
            var chunk = Math.Min(maxCount - total, OracleOutboxSql.MaxRowsPerBulkStatement);
            var requeued = await ExecuteAffectedAsync(
                sql,
                parameters =>
                {
                    parameters.Add(Number("max_count", chunk));
                    if (messageType is not null)
                    {
                        parameters.Add(Text("message_type", messageType));
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

    /// <summary>
    /// Checks that the table answers and that its three function-based indexes exist and are usable (OR-12). Oracle ignores a hint
    /// whose index is missing or unusable, so without this check DbRelay would keep running on full table scans.
    /// </summary>
    public Task CheckHealthAsync(CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, _sql.HealthCheck);
                await using (command.ConfigureAwait(false))
                {
                    var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        await reader.ReadAsync(ct).ConfigureAwait(false);
                        var usableIndexes = Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
                        if (usableIndexes < 3)
                        {
                            throw new InvalidOperationException(
                                $"An outbox index of {_sql.Table} is missing or unusable ({usableIndexes} of {_sql.ClaimIndexName}, {_sql.CleanupIndexName} and {_sql.DeadIndexName} are valid). " +
                                "Run EnsureCreatedAsync to create a missing index, or ALTER INDEX ... REBUILD an unusable one; until then DbRelay scans the whole table.");
                        }
                    }

                    return true;
                }
            },
            cancellationToken);

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private Task<int> ExecuteNonQueryAsync(string sql, Action<OracleParameterCollection> addParameters, CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    try
                    {
                        addParameters(command.Parameters);
                        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        OracleConnectionSource.DisposeParameters(command);
                    }
                }
            },
            cancellationToken);

    /// <summary>Runs a PL/SQL block that reports its row count in an <c>:affected</c> output parameter.</summary>
    private Task<int> ExecuteAffectedAsync(string sql, Action<OracleParameterCollection> addParameters, CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    try
                    {
                        addParameters(command.Parameters);
                        var affected = command.Parameters.Add(new OracleParameter("affected", OracleDbType.Int32) { Direction = ParameterDirection.Output });
                        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        return affected.Value is OracleDecimal { IsNull: false } value ? value.ToInt32() : 0;
                    }
                    finally
                    {
                        OracleConnectionSource.DisposeParameters(command);
                    }
                }
            },
            cancellationToken);

    /// <summary>
    /// Maps the current row. A row that cannot be mapped becomes an <see cref="UnreadableRow"/> placeholder instead of failing the
    /// whole result set; otherwise one bad row would wedge its claim batch in Processing forever (CR-01 / FU-01).
    /// </summary>
    private static OutboxMessageDetails ReadDetails(OracleDataReader reader)
    {
        try
        {
            return ReadDetailsCore(reader);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UnreadableRow.Create(
                new Guid((byte[])reader.GetValue(0)),
                TryRead(() => (OutboxMessageStatus)reader.GetInt32(8)),
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

    private static OutboxMessageDetails ReadDetailsCore(OracleDataReader reader) => new()
    {
        Id = new Guid((byte[])reader.GetValue(0)),
        MessageType = reader.GetString(1),
        Destination = GetNullableString(reader, 2),
        Payload = reader.IsDBNull(3) ? [] : (byte[])reader.GetValue(3),
        ContentType = reader.GetString(4),
        Headers = OutboxHeaders.Deserialize(GetNullableString(reader, 5)),
        CorrelationId = GetNullableString(reader, 6),
        CausationId = GetNullableString(reader, 7),
        Status = (OutboxMessageStatus)reader.GetInt32(8),
        AttemptCount = reader.GetInt32(9),
        CreatedAt = ToUtc(reader.GetDateTime(10)),
        ScheduledAt = ToUtc(reader.GetDateTime(11)),
        AvailableAt = ToUtc(reader.GetDateTime(12)),
        LockedBy = GetNullableString(reader, 13),
        LastAttemptAt = GetNullableUtc(reader, 14),
        DeliveredAt = GetNullableUtc(reader, 15),
        DeadLetteredAt = GetNullableUtc(reader, 16),
        LastError = GetNullableString(reader, 17),
    };

    private static string? GetNullableString(OracleDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableUtc(OracleDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ToUtc(reader.GetDateTime(ordinal));

    private static DateTimeOffset ToUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>Parses the claim order: 32 hex digits per ID, as written by <c>RAWTOHEX</c>.</summary>
    private static List<Guid> ParseIds(object? value)
    {
        var text = value is OracleString { IsNull: false } s ? s.Value : null;
        var ids = new List<Guid>(text is null ? 0 : text.Length / OracleOutboxSql.HexPerId);
        for (var i = 0; text is not null && i + OracleOutboxSql.HexPerId <= text.Length; i += OracleOutboxSql.HexPerId)
        {
            ids.Add(new Guid(Convert.FromHexString(text.AsSpan(i, OracleOutboxSql.HexPerId))));
        }

        return ids;
    }

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

    private static OracleParameter Raw(string name, Guid value) =>
        new(name, OracleDbType.Raw, 16) { Value = value.ToByteArray() };

    private static OracleParameter Text(string name, string? value) =>
        new(name, OracleDbType.NVarchar2) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Clob(string name, string? value) =>
        new(name, OracleDbType.NClob) { Value = (object?)value ?? DBNull.Value };

    private static OracleParameter Number(string name, long? value) =>
        new(name, OracleDbType.Int64) { Value = value is { } v ? v : DBNull.Value };

    private static OracleParameter Timestamp(string name, DateTime? utc) =>
        new(name, OracleDbType.TimeStamp) { Value = utc is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Unspecified) : DBNull.Value };

    /// <summary>Unsigned lexicographic byte order, the order Oracle sorts RAW values in.</summary>
    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();

        public int Compare(byte[]? x, byte[]? y) => ((ReadOnlySpan<byte>)x).SequenceCompareTo(y);
    }
}
