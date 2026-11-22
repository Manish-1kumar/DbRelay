using System.Data.Common;
using System.Text;
using DbRelay.Storage;
using Npgsql;
using NpgsqlTypes;

namespace DbRelay.PostgreSql.Internal;

/// <summary>
/// The PostgreSQL <see cref="IOutboxStore"/>. Every statement is a single, short, autocommitted command except
/// <see cref="InsertAsync"/>, which runs on the caller's connection and transaction. All times come from the database clock.
/// </summary>
internal sealed class PostgreSqlOutboxStore : IOutboxStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlSettings _settings;

    public PostgreSqlOutboxStore(NpgsqlDataSource dataSource, PostgreSqlSettings settings)
    {
        _dataSource = dataSource;
        _settings = settings;
    }

    public void ValidateCallerResources(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection is not NpgsqlConnection)
        {
            throw new ArgumentException(
                $"DbRelay is configured with the PostgreSQL provider, so the connection must be an Npgsql.NpgsqlConnection; got '{connection.GetType().FullName}'. " +
                "Enqueue with the NpgsqlConnection (or NpgsqlTransaction) that writes your business data.",
                nameof(connection));
        }

        if (transaction is not null and not NpgsqlTransaction)
        {
            throw new ArgumentException(
                $"DbRelay is configured with the PostgreSQL provider, so the transaction must be an Npgsql.NpgsqlTransaction; got '{transaction.GetType().FullName}'.",
                nameof(transaction));
        }
    }

    public async Task InsertAsync(DbConnection connection, DbTransaction? transaction, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        ValidateCallerResources(connection, transaction);

        // Exactly the caller's connection and transaction; DbRelay never opens, closes, commits, rolls back or disposes them.
        var batch = new NpgsqlBatch((NpgsqlConnection)connection, (NpgsqlTransaction?)transaction);
        await using (batch.ConfigureAwait(false))
        {
            await ExecuteInsertBatchAsync(batch, records, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task InsertStandaloneAsync(IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);

        // A data-source batch rents a pooled connection for the call. Without an explicit transaction, PostgreSQL runs the
        // whole batch as one implicit transaction, so the rows are committed together or not at all.
        var batch = _dataSource.CreateBatch();
        await using (batch.ConfigureAwait(false))
        {
            await ExecuteInsertBatchAsync(batch, records, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<OutboxMessageDetails>> ClaimAsync(ClaimRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var command = CreateCommand(_settings.ClaimSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = request.BatchSize, NpgsqlDbType = NpgsqlDbType.Integer });
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = request.LockToken, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = request.WorkerId, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(Interval(request.LeaseDuration));
            return await ReadDetailsListAsync(command, request.BatchSize, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<bool> MarkDeliveredAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(_settings.MarkDeliveredSql, messageId, lockToken, null, null, cancellationToken);

    public Task<bool> ScheduleRetryAsync(Guid messageId, Guid lockToken, TimeSpan delay, string error, CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(_settings.ScheduleRetrySql, messageId, lockToken, Interval(delay), Text(error), cancellationToken);

    public Task<bool> MarkDeadLetteredAsync(Guid messageId, Guid lockToken, string error, CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(_settings.MarkDeadLetteredSql, messageId, lockToken, Text(error), null, cancellationToken);

    public Task<bool> ReleaseAsync(Guid messageId, Guid lockToken, CancellationToken cancellationToken) =>
        ExecuteGuardedAsync(_settings.ReleaseSql, messageId, lockToken, null, null, cancellationToken);

    public async Task<int> DeleteDeliveredAsync(TimeSpan olderThan, int maxCount, CancellationToken cancellationToken)
    {
        var command = CreateCommand(_settings.DeleteDeliveredSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(Interval(olderThan));
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = maxCount, NpgsqlDbType = NpgsqlDbType.Integer });
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<OutboxMessageDetails?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var command = CreateCommand(_settings.GetSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(Uuid(messageId));
            var rows = await ReadDetailsListAsync(command, 1, cancellationToken).ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }
    }

    public async Task<IReadOnlyList<OutboxMessageDetails>> QueryAsync(OutboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var command = CreateCommand(string.Empty);
        await using (command.ConfigureAwait(false))
        {
            var sql = new StringBuilder(_settings.QuerySqlPrefix);
            var separator = " WHERE ";

            void AddFilter(string condition, NpgsqlParameter parameter)
            {
                command.Parameters.Add(parameter);
                sql.Append(separator).Append(condition).Append(" $").Append(command.Parameters.Count);
                separator = " AND ";
            }

            if (query.Status is { } status)
            {
                AddFilter("status =", new NpgsqlParameter<short> { TypedValue = (short)status, NpgsqlDbType = NpgsqlDbType.Smallint });
            }

            if (query.MessageType is { } messageType)
            {
                AddFilter("message_type =", Text(messageType));
            }

            if (query.CreatedAfter is { } createdAfter)
            {
                AddFilter("created_at >=", Timestamp(createdAfter));
            }

            if (query.CreatedBefore is { } createdBefore)
            {
                AddFilter("created_at <", Timestamp(createdBefore));
            }

            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = query.Skip, NpgsqlDbType = NpgsqlDbType.Integer });
            sql.Append(" ORDER BY created_at, id OFFSET $").Append(command.Parameters.Count);
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = query.Take, NpgsqlDbType = NpgsqlDbType.Integer });
            sql.Append(" LIMIT $").Append(command.Parameters.Count);

            // Only fixed SQL fragments and the validated, quoted table name are concatenated; all values are parameters.
            command.CommandText = sql.ToString();
            return await ReadDetailsListAsync(command, query.Take, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<OutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        var command = CreateCommand(_settings.StatisticsSql);
        await using (command.ConfigureAwait(false))
        {
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("The statistics query returned no rows.");
                }

                return new OutboxStatistics
                {
                    Pending = reader.GetInt64(0),
                    Processing = reader.GetInt64(1),
                    Delivered = reader.GetInt64(2),
                    DeadLettered = reader.GetInt64(3),
                    OldestDueAge = reader.IsDBNull(4) ? null : TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(4))),
                };
            }
        }
    }

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var command = CreateCommand(_settings.RequeueSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(Uuid(messageId));
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }

    public async Task<int> RequeueDeadLetteredAsync(string? messageType, int maxCount, CancellationToken cancellationToken)
    {
        var command = CreateCommand(messageType is null ? _settings.RequeueDeadLetteredSql : _settings.RequeueDeadLetteredByTypeSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = maxCount, NpgsqlDbType = NpgsqlDbType.Integer });
            if (messageType is not null)
            {
                command.Parameters.Add(Text(messageType));
            }

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CheckHealthAsync(CancellationToken cancellationToken)
    {
        var command = CreateCommand(_settings.HealthSql);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static NpgsqlParameter<TimeSpan> Interval(TimeSpan value) => new NpgsqlParameter<TimeSpan> { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Interval };

    private static NpgsqlParameter<Guid> Uuid(Guid value) => new NpgsqlParameter<Guid> { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Uuid };

    private static NpgsqlParameter<string> Text(string value) => new NpgsqlParameter<string> { TypedValue = value, NpgsqlDbType = NpgsqlDbType.Text };

    // Npgsql only writes UTC DateTimeOffset values (offset 0) to timestamptz.
    private static NpgsqlParameter<DateTimeOffset> Timestamp(DateTimeOffset value) =>
        new NpgsqlParameter<DateTimeOffset> { TypedValue = value.ToUniversalTime(), NpgsqlDbType = NpgsqlDbType.TimestampTz };

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    /// <summary>
    /// Maps the current row. A row that cannot be mapped becomes an <see cref="UnreadableRow"/> placeholder instead of failing the
    /// whole result set. A failing claim batch would otherwise stay in Processing forever (CR-01 / FU-01).
    /// </summary>
    private static OutboxMessageDetails ReadDetails(NpgsqlDataReader reader)
    {
        try
        {
            return ReadDetailsCore(reader);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return UnreadableRow.Create(
                reader.GetGuid(0),
                TryRead(() => (OutboxMessageStatus)reader.GetInt16(8)),
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

    private static OutboxMessageDetails ReadDetailsCore(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        MessageType = reader.GetString(1),
        Destination = reader.IsDBNull(2) ? null : reader.GetString(2),
        Payload = reader.GetFieldValue<byte[]>(3),
        ContentType = reader.GetString(4),
        Headers = OutboxHeaders.Deserialize(reader.IsDBNull(5) ? null : reader.GetString(5)),
        CorrelationId = reader.IsDBNull(6) ? null : reader.GetString(6),
        CausationId = reader.IsDBNull(7) ? null : reader.GetString(7),
        Status = (OutboxMessageStatus)reader.GetInt16(8),
        AttemptCount = reader.GetInt32(9),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
        ScheduledAt = reader.GetFieldValue<DateTimeOffset>(11),
        AvailableAt = reader.GetFieldValue<DateTimeOffset>(12),
        LockedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
        LastAttemptAt = reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
        DeliveredAt = reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        DeadLetteredAt = reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
        LastError = reader.IsDBNull(17) ? null : reader.GetString(17),
    };

    private static async Task<IReadOnlyList<OutboxMessageDetails>> ReadDetailsListAsync(
        NpgsqlCommand command,
        int capacityHint,
        CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var results = new List<OutboxMessageDetails>(Math.Min(capacityHint, 128));
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(ReadDetails(reader));
            }

            return results;
        }
    }

    private async Task ExecuteInsertBatchAsync(NpgsqlBatch batch, IReadOnlyList<OutboxRecord> records, CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return;
        }

        batch.Timeout = _settings.CommandTimeoutSeconds;
        foreach (var record in records)
        {
            var command = new NpgsqlBatchCommand(_settings.InsertSql);
            var parameters = command.Parameters;
            parameters.Add(Uuid(record.Id));
            parameters.Add(Text(record.MessageType));
            parameters.Add(Nullable(record.Destination, NpgsqlDbType.Text));
            parameters.Add(new NpgsqlParameter<ReadOnlyMemory<byte>> { TypedValue = record.Payload, NpgsqlDbType = NpgsqlDbType.Bytea });
            parameters.Add(Text(record.ContentType));
            parameters.Add(Nullable(record.HeadersJson, NpgsqlDbType.Jsonb));
            parameters.Add(Nullable(record.CorrelationId, NpgsqlDbType.Text));
            parameters.Add(Nullable(record.CausationId, NpgsqlDbType.Text));
            parameters.Add(Nullable(record.DeliverAt?.ToUniversalTime(), NpgsqlDbType.TimestampTz));
            parameters.Add(Nullable(record.Delay, NpgsqlDbType.Interval));
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand CreateCommand(string sql)
    {
        var command = _dataSource.CreateCommand(sql);
        command.CommandTimeout = _settings.CommandTimeoutSeconds;
        return command;
    }

    private async Task<bool> ExecuteGuardedAsync(
        string sql,
        Guid messageId,
        Guid lockToken,
        NpgsqlParameter? third,
        NpgsqlParameter? fourth,
        CancellationToken cancellationToken)
    {
        var command = CreateCommand(sql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.Add(Uuid(messageId));
            command.Parameters.Add(Uuid(lockToken));
            if (third is not null)
            {
                command.Parameters.Add(third);
            }

            if (fourth is not null)
            {
                command.Parameters.Add(fourth);
            }

            // Every post-claim transition is guarded by WHERE id = $1 AND lock_token = $2: zero rows means the lease was lost.
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
    }
}
