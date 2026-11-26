using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Runtime.InteropServices;
using DbRelay.Storage;
using MySqlConnector;

namespace DbRelay.MySql.Internal;

/// <summary>
/// The MySQL / MariaDB <see cref="IOutboxStore"/>. Single-row transitions are autocommitted statements guarded by the lock token;
/// claims, batched acknowledgements, cleanup and bulk requeue run in short READ COMMITTED transactions on DbRelay's own
/// connections. <see cref="InsertAsync"/> runs on the caller's connection and transaction.
/// </summary>
internal sealed class MySqlOutboxStore : IOutboxStore, IOutboxBatchStore
{
    /// <summary>Upper bound for intervals (about 54 years), far beyond any configurable delay or lease.</summary>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(20000);

    /// <summary>Calls estimated below this size skip the packet check: every supported server allows at least 1 MiB (MY-02).</summary>
    private const long PacketCheckThreshold = 1024 * 1024;

    private static readonly TimeSpan PacketLimitCacheTime = TimeSpan.FromMinutes(5);

    private readonly MySqlConnectionSource _connections;
    private readonly MySqlOutboxSql _sql;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Limit, DateTime ReadAt)> _packetLimits = new(StringComparer.Ordinal);

    public MySqlOutboxStore(MySqlConnectionSource connections, MySqlOutboxSql sql)
    {
        _connections = connections;
        _sql = sql;
    }

    /// <summary>Error 1213/1205 retries by this store (see <see cref="MySqlConnectionSource"/>).</summary>
    internal long DeadlockRetries => _connections.DeadlockRetries;

    /// <summary>
    /// Test seam: how long a claim holds its locked candidates before leasing them. Always zero in production; tests use it to check
    /// that producers can insert while a claim holds its locks (READ COMMITTED: no gap locks).
    /// </summary>
    internal TimeSpan ClaimTestPause { get; set; }

    // ---- Enqueue ---------------------------------------------------------------------------------------------------

    public void ValidateCallerResources(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not MySqlConnection)
        {
            throw new ArgumentException(
                $"DbRelay is configured for MySQL and requires a MySqlConnector.MySqlConnection; got '{connection.GetType().FullName}'.",
                nameof(connection));
        }

        if (transaction is not null and not MySqlTransaction)
        {
            throw new ArgumentException(
                $"DbRelay is configured for MySQL and requires a MySqlConnector.MySqlTransaction; got '{transaction.GetType().FullName}'.",
                nameof(transaction));
        }
    }

    public async Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ValidateCallerResources(connection, transaction);
        ValidateBatch(records);
        var mySqlConnection = (MySqlConnection)connection;
        try
        {
            // MY-07: an unqualified outbox table resolves to the connection's current database. Inserting into another database than
            // the one DbRelay's dispatcher polls would store messages nobody delivers.
            if (_sql.Schema is null)
            {
                var database = await _connections.GetDatabaseAsync(cancellationToken).ConfigureAwait(false);
                if (!string.Equals(mySqlConnection.Database, database, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"The connection's current database ('{mySqlConnection.Database}') is not the database DbRelay's own connections use ('{database}'), " +
                        "and MySqlOutboxOptions.Schema is not set, so the message would be stored where the dispatcher does not look. " +
                        "Set MySqlOutboxOptions.Schema to the outbox database, or enqueue on a connection to that database.");
                }
            }

            // Exactly the caller's connection and transaction; DbRelay never opens, closes, commits, rolls back or disposes them.
            await CheckPacketSizeAsync(mySqlConnection, (MySqlTransaction?)transaction, records, cancellationToken).ConfigureAwait(false);
            await InsertCoreAsync(mySqlConnection, (MySqlTransaction?)transaction, records, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (transaction is null && IsMissingTransactionError(exception))
        {
            // As CR-11 on SQL Server: MySqlConnector rejects a command without the connection's active transaction. DbRelay cannot
            // adopt that transaction, so it explains the fix. Other InvalidOperationExceptions pass through unchanged (MY-10).
            throw new InvalidOperationException(
                "The outbox insert was rejected because no transaction was passed while the connection has a transaction in progress. " +
                "Pass that transaction to EnqueueAsync so the outbox row commits or rolls back with your business data. See the inner exception for MySqlConnector's message.",
                exception);
        }
        catch (MySqlException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The outbox insert was cancelled.", exception, cancellationToken);
        }
    }

    public Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ValidateBatch(records);
        return _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                await CheckPacketSizeAsync(connection, null, records, ct).ConfigureAwait(false);
                await InsertCoreAsync(connection, null, records, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// MY-14: one call is one atomic statement, so the storage interface accepts at most <see cref="OutboxLimits.MaxEnqueueBatchSize"/>
    /// records per call (DbRelay's own enqueue API never sends more).
    /// </summary>
    private static void ValidateBatch(IReadOnlyList<OutboxRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count > OutboxLimits.MaxEnqueueBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(records), records.Count, $"At most {OutboxLimits.MaxEnqueueBatchSize} records per insert.");
        }
    }

    /// <summary>
    /// MY-02: a statement larger than the server's <c>max_allowed_packet</c> makes the server close the connection, which on the
    /// caller's connection would end their transaction. A call that could come close is checked against the server's limit first,
    /// and rejected with an explanation instead, leaving the connection and transaction intact. The size is estimated
    /// conservatively: MySqlConnector sends binary values escaped (at most two bytes per byte) and text as UTF-8 (at most four
    /// bytes per character).
    /// </summary>
    private async Task CheckPacketSizeAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        long estimate = 0;
        foreach (var record in records)
        {
            estimate += 400 + (2L * record.Payload.Length)
                + (4L * ((record.HeadersJson?.Length ?? 0) + record.MessageType.Length + record.ContentType.Length
                    + (record.Destination?.Length ?? 0) + (record.CorrelationId?.Length ?? 0) + (record.CausationId?.Length ?? 0)));
        }

        if (estimate < PacketCheckThreshold)
        {
            return; // every supported server allows at least this much
        }

        var limit = await GetMaxAllowedPacketAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (estimate > limit)
        {
            throw new InvalidOperationException(
                $"This enqueue call is too large for the server: its statement may be up to {estimate:N0} bytes, and the server's max_allowed_packet is {limit:N0} bytes. " +
                "Enqueue the messages in several calls (in the same transaction if they must commit together), or raise max_allowed_packet. Nothing was sent.");
        }
    }

    /// <summary>The server's <c>max_allowed_packet</c>, read on the given connection and cached per server for a few minutes.</summary>
    private async Task<long> GetMaxAllowedPacketAsync(MySqlConnection connection, MySqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var key = connection.DataSource ?? string.Empty;
        if (_packetLimits.TryGetValue(key, out var cached) && cached.ReadAt > DateTime.UtcNow - PacketLimitCacheTime)
        {
            return cached.Limit;
        }

        var command = _connections.CreateCommand(connection, MySqlOutboxSql.MaxAllowedPacket, transaction);
        await using (command.ConfigureAwait(false))
        {
            var limit = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            _packetLimits[key] = (limit, DateTime.UtcNow);
            return limit;
        }
    }

    /// <summary>MySqlConnector's error for a command without the connection's active transaction (MY-10).</summary>
    private static bool IsMissingTransactionError(InvalidOperationException exception) =>
        exception.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase)
        && !exception.Message.Contains("max_allowed_packet", StringComparison.Ordinal)
        && !exception.Message.Contains("current database", StringComparison.Ordinal);

    /// <summary>
    /// One multi-row INSERT per <see cref="OutboxLimits.MaxEnqueueBatchSize"/> rows, each atomic on its own, so DbRelay never begins,
    /// commits or rolls back a transaction. DbRelay limits one enqueue call to that many messages, so a call is one statement.
    /// </summary>
    private async Task InsertCoreAsync(MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        for (var start = 0; start < records.Count; start += OutboxLimits.MaxEnqueueBatchSize)
        {
            var count = Math.Min(OutboxLimits.MaxEnqueueBatchSize, records.Count - start);
            var command = _connections.CreateCommand(connection, _sql.GetInsert(count), transaction);
            await using (command.ConfigureAwait(false))
            {
                var parameters = command.Parameters;
                for (var i = 0; i < count; i++)
                {
                    var record = records[start + i];
                    var n = i.ToString(CultureInfo.InvariantCulture);
                    parameters.Add(Hex("@id" + n, record.Id));
                    parameters.Add(Text("@mt" + n, record.MessageType));
                    parameters.Add(Text("@de" + n, record.Destination));
                    parameters.Add(new MySqlParameter("@pl" + n, MySqlDbType.LongBlob) { Value = ToArray(record.Payload) });
                    parameters.Add(Text("@ct" + n, record.ContentType));
                    parameters.Add(LongText("@hd" + n, record.HeadersJson));
                    parameters.Add(Text("@co" + n, record.CorrelationId));
                    parameters.Add(Text("@ca" + n, record.CausationId));
                    parameters.Add(Timestamp("@da" + n, record.DeliverAt?.UtcDateTime));
                    parameters.Add(Number("@dl" + n, record.Delay is { } delay ? ToMicroseconds(delay) : null));
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
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

        ArgumentOutOfRangeException.ThrowIfLessThan(request.MaxPayloadBytes, 1L, nameof(request));
        return _connections.ExecuteInTransactionAsync<IReadOnlyList<OutboxMessageDetails>>(
            async (connection, ct) =>
            {
                // 1. Lock due candidates in claim order, skipping rows other claimers hold.
                var chosen = new List<Guid>(request.BatchSize);
                var candidates = _connections.CreateCommand(connection, _sql.ClaimCandidates);
                await using (candidates.ConfigureAwait(false))
                {
                    candidates.Parameters.Add(Number("@batch_size", request.BatchSize));
                    var reader = await candidates.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        // 2. ADR 0004 payload budget: take a row only while the payloads taken before it total less than the budget,
                        // so the first row is always taken. Rows read but not taken stay locked until the commit, then are free.
                        long bytes = 0;
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            if (chosen.Count > 0 && bytes >= request.MaxPayloadBytes)
                            {
                                break;
                            }

                            chosen.Add(ParseId(reader.GetString(0)));
                            bytes += reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                        }
                    }
                }

                if (chosen.Count == 0)
                {
                    return [];
                }

                if (ClaimTestPause > TimeSpan.Zero)
                {
                    await Task.Delay(ClaimTestPause, ct).ConfigureAwait(false);
                }

                // 3. Lease them and count the attempt.
                var bucket = MySqlOutboxSql.BucketFor(chosen.Count);
                var lease = _connections.CreateCommand(connection, _sql.GetClaimUpdate(bucket));
                await using (lease.ConfigureAwait(false))
                {
                    AddIds(lease.Parameters, chosen, bucket);
                    lease.Parameters.Add(Hex("@lock_token", request.LockToken));
                    lease.Parameters.Add(Text("@worker_id", Truncate(request.WorkerId, OutboxLimits.MaxWorkerIdLength)));
                    lease.Parameters.Add(Number("@lease_us", ToMicroseconds(request.LeaseDuration)));
                    await lease.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // 4. Read the leased rows; only rows this claim changed carry its token.
                var rows = new Dictionary<Guid, OutboxMessageDetails>(chosen.Count);
                var select = _connections.CreateCommand(connection, _sql.GetClaimedRows(bucket));
                await using (select.ConfigureAwait(false))
                {
                    AddIds(select.Parameters, chosen, bucket);
                    select.Parameters.Add(Hex("@lock_token", request.LockToken));
                    var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            var details = ReadDetails(reader);
                            rows[details.Id] = details;
                        }
                    }
                }

                // Return the claim order (oldest available_at first).
                var result = new List<OutboxMessageDetails>(rows.Count);
                foreach (var id in chosen)
                {
                    if (rows.TryGetValue(id, out var details))
                    {
                        result.Add(details);
                    }
                }

                return result;
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
                parameters.Add(Number("@delay_us", ToMicroseconds(delay)));
                parameters.Add(LongText("@error", Truncate(error, OutboxLimits.MaxErrorLength)));
            },
            cancellationToken);

    public Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken) =>
        GuardedAsync(
            _sql.MarkDeadLettered,
            messageId,
            lockToken,
            parameters => parameters.Add(LongText("@error", Truncate(error, OutboxLimits.MaxErrorLength))),
            cancellationToken);

    public Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        GuardedAsync(_sql.Release, messageId, lockToken, static _ => { }, cancellationToken);

    /// <summary>Every post-claim transition is guarded by <c>WHERE id = ... AND lock_token = ...</c>: zero rows means the lease was lost.</summary>
    private async Task<bool> GuardedAsync(
        string sql, Guid messageId, Guid lockToken, Action<MySqlParameterCollection> addParameters, CancellationToken cancellationToken)
    {
        var rows = await ExecuteNonQueryAsync(
            sql,
            parameters =>
            {
                parameters.Add(Hex("@id", messageId));
                parameters.Add(Hex("@lock_token", lockToken));
                addParameters(parameters);
            },
            cancellationToken).ConfigureAwait(false);
        return rows == 1;
    }

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> MarkDeliveredBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(items, delivered: true, cancellationToken);

    public Task<IReadOnlyCollection<OutboxAcknowledgement>> ReleaseBatchAsync(IReadOnlyList<OutboxAcknowledgement> items, CancellationToken cancellationToken) =>
        GuardedBatchAsync(items, delivered: false, cancellationToken);

    /// <summary>
    /// ADR 0003 batch: neither engine has <c>UPDATE ... RETURNING</c>, so one READ COMMITTED transaction locks the rows in primary key
    /// order (BA-07), keeps the items whose token still matches, and changes exactly those rows.
    /// </summary>
    private async Task<IReadOnlyCollection<OutboxAcknowledgement>> GuardedBatchAsync(
        IReadOnlyList<OutboxAcknowledgement> items, bool delivered, CancellationToken cancellationToken)
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

        var ids = items.Select(i => i.MessageId).Distinct().OrderBy(HexOf, StringComparer.Ordinal).ToList();
        return await _connections.ExecuteInTransactionAsync<IReadOnlyCollection<OutboxAcknowledgement>>(
            async (connection, ct) =>
            {
                var bucket = MySqlOutboxSql.BucketFor(ids.Count);
                var current = new Dictionary<Guid, Guid?>(ids.Count);
                var lockCommand = _connections.CreateCommand(connection, _sql.GetLockForBatch(bucket));
                await using (lockCommand.ConfigureAwait(false))
                {
                    AddIds(lockCommand.Parameters, ids, bucket);
                    var reader = await lockCommand.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            current[ParseId(reader.GetString(0))] = reader.IsDBNull(1) ? null : ParseId(reader.GetString(1));
                        }
                    }
                }

                var matched = items.Where(i => current.TryGetValue(i.MessageId, out var token) && token == i.LockToken).ToHashSet();
                if (matched.Count == 0)
                {
                    return matched;
                }

                // A message ID appears at most once among the matches: a row has one current token.
                var pairs = matched.OrderBy(i => HexOf(i.MessageId), StringComparer.Ordinal).ToList();
                var updateBucket = MySqlOutboxSql.BucketFor(pairs.Count);
                var update = _connections.CreateCommand(connection, _sql.GetBatchUpdate(updateBucket, delivered));
                await using (update.ConfigureAwait(false))
                {
                    for (var i = 0; i < updateBucket; i++)
                    {
                        var pair = pairs[Math.Min(i, pairs.Count - 1)]; // pad by repeating the last pair
                        var n = i.ToString(CultureInfo.InvariantCulture);
                        update.Parameters.Add(Hex("@i" + n, pair.MessageId));
                        update.Parameters.Add(Hex("@t" + n, pair.LockToken));
                    }

                    // MY-06: each row is guarded by its own (id, lock_token) pair in SQL too, and every matched row must have changed. The
                    // rows are locked by the step above, so a difference means the database changed them anyway: roll back instead.
                    var changed = await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    if (changed != pairs.Count)
                    {
                        throw new InvalidOperationException(
                            $"A batched acknowledgement changed {changed} rows instead of the {pairs.Count} it had locked; nothing was changed.");
                    }
                }

                return matched;
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
            var chunk = Math.Min(maxCount - total, MySqlOutboxSql.MaxRowsPerBulkStatement);
            var deleted = await ExecuteInTransactionAsync(
                _sql.DeleteDelivered,
                parameters =>
                {
                    parameters.Add(Number("@older_than_us", ToMicroseconds(olderThan)));
                    parameters.Add(Number("@max_count", chunk));
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
                    command.Parameters.Add(Hex("@id", messageId));
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
                        parameters.Add(Number("@status", (int)status));
                    }

                    if (query.MessageType is { } messageType)
                    {
                        parameters.Add(Text("@message_type", messageType));
                    }

                    if (query.CreatedAfter is { } after)
                    {
                        parameters.Add(Timestamp("@created_after", after.UtcDateTime));
                    }

                    if (query.CreatedBefore is { } before)
                    {
                        parameters.Add(Timestamp("@created_before", before.UtcDateTime));
                    }

                    parameters.Add(Number("@skip", query.Skip));
                    parameters.Add(Number("@take", query.Take));

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
                            Pending = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                            Processing = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                            Delivered = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                            DeadLettered = Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
                            OldestDueAge = reader.IsDBNull(4)
                                ? null
                                : TimeSpan.FromMicroseconds(Math.Max(0, Convert.ToDouble(reader.GetValue(4), CultureInfo.InvariantCulture))),
                        };
                    }
                }
            },
            cancellationToken);

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken) =>
        await ExecuteNonQueryAsync(_sql.Requeue, parameters => parameters.Add(Hex("@id", messageId)), cancellationToken).ConfigureAwait(false) == 1;

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
            var chunk = Math.Min(maxCount - total, MySqlOutboxSql.MaxRowsPerBulkStatement);
            var requeued = await ExecuteInTransactionAsync(
                sql,
                parameters =>
                {
                    parameters.Add(Number("@max_count", chunk));
                    if (messageType is not null)
                    {
                        parameters.Add(Text("@message_type", messageType));
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

    /// <summary>The table must answer and have the indexes and columns this provider uses (MY-08).</summary>
    public Task CheckHealthAsync(CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                await MySqlOutboxSchemaManager.VerifyShapeAsync(_connections, connection, _sql, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private Task<int> ExecuteNonQueryAsync(string sql, Action<MySqlParameterCollection> addParameters, CancellationToken cancellationToken) =>
        _connections.ExecuteAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    addParameters(command.Parameters);
                    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            },
            cancellationToken);

    /// <summary>One statement in its own READ COMMITTED transaction (no gap locks on the scanned range).</summary>
    private Task<int> ExecuteInTransactionAsync(string sql, Action<MySqlParameterCollection> addParameters, CancellationToken cancellationToken) =>
        _connections.ExecuteInTransactionAsync(
            async (connection, ct) =>
            {
                var command = _connections.CreateCommand(connection, sql);
                await using (command.ConfigureAwait(false))
                {
                    addParameters(command.Parameters);
                    return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            },
            cancellationToken);

    private static void AddIds(MySqlParameterCollection parameters, List<Guid> ids, int bucket)
    {
        for (var i = 0; i < bucket; i++)
        {
            parameters.Add(Hex("@i" + i.ToString(CultureInfo.InvariantCulture), ids[Math.Min(i, ids.Count - 1)])); // pad by repeating the last ID
        }
    }

    /// <summary>
    /// Maps the current row. A row that cannot be mapped becomes an <see cref="UnreadableRow"/> placeholder instead of failing the
    /// whole result set; otherwise one bad row would wedge its claim batch in Processing forever (CR-01 / FU-01).
    /// </summary>
    private static OutboxMessageDetails ReadDetails(DbDataReader reader)
    {
        try
        {
            return ReadDetailsCore(reader);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UnreadableRow.Create(
                ParseId(reader.GetString(0)),
                TryRead(() => (OutboxMessageStatus)Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture)),
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

    private static OutboxMessageDetails ReadDetailsCore(DbDataReader reader) => new()
    {
        Id = ParseId(reader.GetString(0)),
        MessageType = reader.GetString(1),
        Destination = GetNullableString(reader, 2),
        Payload = reader.IsDBNull(3) ? [] : (byte[])reader.GetValue(3),
        ContentType = reader.GetString(4),
        Headers = OutboxHeaders.Deserialize(GetNullableString(reader, 5)),
        CorrelationId = GetNullableString(reader, 6),
        CausationId = GetNullableString(reader, 7),
        Status = (OutboxMessageStatus)Convert.ToInt32(reader.GetValue(8), CultureInfo.InvariantCulture),
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

    private static string? GetNullableString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableUtc(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ToUtc(reader.GetDateTime(ordinal));

    private static DateTimeOffset ToUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>The 32 hex digits stored for a GUID (its "N" format), which is also its canonical sort key (BA-07).</summary>
    private static string HexOf(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    private static Guid ParseId(string hex) => Guid.ParseExact(hex, "N");

    /// <summary>Whole microseconds, clamped to [0, <see cref="MaxInterval"/>].</summary>
    private static long ToMicroseconds(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return 0;
        }

        return (interval > MaxInterval ? MaxInterval : interval).Ticks / (TimeSpan.TicksPerMillisecond / 1000);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is not null && value.Length > maxLength ? value[..maxLength] : value;

    private static byte[] ToArray(ReadOnlyMemory<byte> payload) =>
        MemoryMarshal.TryGetArray(payload, out var segment) && segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length
            ? array
            : payload.ToArray();

    private static MySqlParameter Hex(string name, Guid value) =>
        new(name, MySqlDbType.VarChar, MySqlOutboxSql.HexPerId) { Value = HexOf(value) };

    private static MySqlParameter Text(string name, string? value) =>
        new(name, MySqlDbType.VarChar) { Value = (object?)value ?? DBNull.Value };

    private static MySqlParameter LongText(string name, string? value) =>
        new(name, MySqlDbType.MediumText) { Value = (object?)value ?? DBNull.Value };

    private static MySqlParameter Number(string name, long? value) =>
        new(name, MySqlDbType.Int64) { Value = value is { } v ? v : DBNull.Value };

    private static MySqlParameter Timestamp(string name, DateTime? utc) =>
        new(name, MySqlDbType.DateTime) { Value = utc is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Unspecified) : DBNull.Value };
}
