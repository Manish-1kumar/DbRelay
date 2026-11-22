using System.Data;
using System.Data.Common;
using System.Diagnostics;
using DbRelay.Configuration;
using DbRelay.Diagnostics;
using DbRelay.Internal;
using DbRelay.Serialization;
using DbRelay.Storage;
using Microsoft.Extensions.Options;

namespace DbRelay;

/// <summary>
/// The <see cref="IOutbox"/> implementation: validates input, serializes, builds records, and hands them to the storage provider.
/// It never takes ownership of caller connections or transactions.
/// </summary>
internal sealed class Outbox : IOutbox
{
    internal const string ReservedHeaderPrefix = "dbrelay-";
    internal const string TraceParentHeader = "traceparent";
    internal const string TraceStateHeader = "tracestate";
    private static readonly TimeSpan MaxDelay = TimeSpan.FromDays(3650);

    private readonly IOutboxStore _store;
    private readonly IMessageSerializer _serializer;
    private readonly MessageTypeRegistry _types;
    private readonly DbRelayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly int _maxPayloadBytes;

    public Outbox(
        IOutboxStore store,
        IMessageSerializer serializer,
        MessageTypeRegistry types,
        DbRelayMetrics metrics,
        TimeProvider time,
        IOptions<OutboxOptions> options)
    {
        _store = store;
        _serializer = serializer;
        _types = types;
        _metrics = metrics;
        _time = time;
        _maxPayloadBytes = options.Value.MaxPayloadBytes;

        // A custom serializer's content type is stored in a bounded column; reject bad values up front rather than letting a
        // provider truncate them silently (CR-12).
        var contentType = serializer.ContentType;
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > OutboxLimits.MaxContentTypeLength || contentType.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"IMessageSerializer.ContentType must be 1 to {OutboxLimits.MaxContentTypeLength} characters without NUL characters; " +
                $"'{serializer.GetType().FullName}' returned an invalid value.");
        }
    }

    public async Task<Guid> EnqueueAsync<TMessage>(
        IDbConnection connection,
        IDbTransaction? transaction,
        TMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (dbConnection, dbTransaction) = ValidateCallerResources(connection, transaction);
        var record = CreateRecord(message, options, allowMessageId: true);

        using var activity = StartEnqueueActivity(record.MessageType, 1);
        await _store.InsertAsync(dbConnection, dbTransaction, [record], cancellationToken).ConfigureAwait(false);
        _metrics.Enqueued(record.MessageType, 1);
        return record.Id;
    }

    public async Task<IReadOnlyList<Guid>> EnqueueManyAsync<TMessage>(
        IDbConnection connection,
        IDbTransaction? transaction,
        IEnumerable<TMessage> messages,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var (dbConnection, dbTransaction) = ValidateCallerResources(connection, transaction);
        if (options?.MessageId is not null)
        {
            throw new ArgumentException("EnqueueOptions.MessageId cannot be used with EnqueueManyAsync; every message needs its own ID.", nameof(options));
        }

        var records = new List<OutboxRecord>();
        foreach (var message in messages)
        {
            if (records.Count == OutboxLimits.MaxEnqueueBatchSize)
            {
                throw new ArgumentException($"At most {OutboxLimits.MaxEnqueueBatchSize} messages can be enqueued in one call.", nameof(messages));
            }

            records.Add(CreateRecord(message, options, allowMessageId: false));
        }

        if (records.Count == 0)
        {
            return [];
        }

        using var activity = StartEnqueueActivity(records[0].MessageType, records.Count);
        await _store.InsertAsync(dbConnection, dbTransaction, records, cancellationToken).ConfigureAwait(false);

        var ids = new Guid[records.Count];
        for (var i = 0; i < records.Count; i++)
        {
            ids[i] = records[i].Id;
            _metrics.Enqueued(records[i].MessageType, 1);
        }

        return ids;
    }

    public async Task<Guid> EnqueueRawAsync(
        IDbConnection connection,
        IDbTransaction? transaction,
        RawOutboxMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var (dbConnection, dbTransaction) = ValidateCallerResources(connection, transaction);
        MessageTypeRegistry.ValidateName(message.MessageType, nameof(message));
        ValidateLength(message.ContentType, OutboxLimits.MaxContentTypeLength, "RawOutboxMessage.ContentType");
        var record = BuildRecord(message.MessageType, message.Payload, message.ContentType, options, allowMessageId: true);

        using var activity = StartEnqueueActivity(record.MessageType, 1);
        await _store.InsertAsync(dbConnection, dbTransaction, [record], cancellationToken).ConfigureAwait(false);
        _metrics.Enqueued(record.MessageType, 1);
        return record.Id;
    }

    public async Task<Guid> EnqueueStandaloneAsync<TMessage>(
        TMessage message,
        EnqueueOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var record = CreateRecord(message, options, allowMessageId: true);

        using var activity = StartEnqueueActivity(record.MessageType, 1);
        await _store.InsertStandaloneAsync([record], cancellationToken).ConfigureAwait(false);
        _metrics.Enqueued(record.MessageType, 1);
        return record.Id;
    }

    private (DbConnection Connection, DbTransaction? Transaction) ValidateCallerResources(IDbConnection connection, IDbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection is not DbConnection dbConnection)
        {
            throw new ArgumentException(
                $"The connection must derive from System.Data.Common.DbConnection; got '{connection.GetType().FullName}'.", nameof(connection));
        }

        if (dbConnection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                $"The connection must be open (current state: {dbConnection.State}). DbRelay does not open or close caller-owned connections.");
        }

        DbTransaction? dbTransaction = null;
        if (transaction is not null)
        {
            dbTransaction = transaction as DbTransaction
                ?? throw new ArgumentException(
                    $"The transaction must derive from System.Data.Common.DbTransaction; got '{transaction.GetType().FullName}'.", nameof(transaction));

            var owner = dbTransaction.Connection
                ?? throw new InvalidOperationException(
                    "The transaction has already been committed or rolled back. Enqueue outbox messages before committing the transaction.");

            if (!ReferenceEquals(owner, dbConnection))
            {
                throw new ArgumentException(
                    "The transaction belongs to a different connection than the one supplied. Pass the connection that started the transaction.",
                    nameof(transaction));
            }
        }

        _store.ValidateCallerResources(dbConnection, dbTransaction);
        return (dbConnection, dbTransaction);
    }

    private OutboxRecord CreateRecord<TMessage>(TMessage message, EnqueueOptions? options, bool allowMessageId)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (message is CancellationToken)
        {
            // outbox.EnqueueAsync(transaction, cancellationToken) compiles with TMessage = CancellationToken (CR-22).
            throw new ArgumentException(
                "A CancellationToken was passed as the message. The message argument appears to be missing: use EnqueueAsync(transaction, message, cancellationToken).",
                nameof(message));
        }

        var messageType = _types.GetName(message.GetType());
        var payload = _serializer.Serialize(message);
        return BuildRecord(messageType, payload, _serializer.ContentType, options, allowMessageId);
    }

    private OutboxRecord BuildRecord(string messageType, ReadOnlyMemory<byte> payload, string contentType, EnqueueOptions? options, bool allowMessageId)
    {
        if (payload.Length > _maxPayloadBytes)
        {
            throw new ArgumentException(
                $"The serialized payload is {payload.Length} bytes, which exceeds OutboxOptions.MaxPayloadBytes ({_maxPayloadBytes}).");
        }

        Guid id;
        if (allowMessageId && options?.MessageId is { } supplied)
        {
            if (supplied == Guid.Empty)
            {
                throw new ArgumentException("EnqueueOptions.MessageId must not be Guid.Empty.", nameof(options));
            }

            id = supplied;
        }
        else
        {
            id = Uuid7.NewGuid(_time.GetUtcNow());
        }

        if (options is not null)
        {
            ValidateLength(options.Destination, OutboxLimits.MaxDestinationLength, "EnqueueOptions.Destination");
            ValidateLength(options.CorrelationId, OutboxLimits.MaxCorrelationIdLength, "EnqueueOptions.CorrelationId");
            ValidateLength(options.CausationId, OutboxLimits.MaxCorrelationIdLength, "EnqueueOptions.CausationId");

            if (options.DeliverAt is not null && options.Delay is not null)
            {
                throw new ArgumentException("Set either EnqueueOptions.DeliverAt or EnqueueOptions.Delay, not both.", nameof(options));
            }

            if (options.Delay is { } delay && (delay < TimeSpan.Zero || delay > MaxDelay))
            {
                throw new ArgumentOutOfRangeException(nameof(options), delay, "EnqueueOptions.Delay must be between zero and 3650 days.");
            }
        }

        return new OutboxRecord
        {
            Id = id,
            MessageType = messageType,
            Destination = options?.Destination,
            Payload = payload,
            ContentType = contentType,
            HeadersJson = OutboxHeaders.Serialize(BuildHeaders(options?.Headers)),
            CorrelationId = options?.CorrelationId,
            CausationId = options?.CausationId,
            DeliverAt = options?.DeliverAt,
            Delay = options?.Delay,
        };
    }

    private static Dictionary<string, string>? BuildHeaders(IReadOnlyDictionary<string, string>? supplied)
    {
        Dictionary<string, string>? headers = null;
        if (supplied is not null)
        {
            if (supplied.Count > OutboxLimits.MaxHeaderCount)
            {
                throw new ArgumentException($"At most {OutboxLimits.MaxHeaderCount} headers are allowed.", nameof(supplied));
            }

            headers = new Dictionary<string, string>(supplied.Count + 2, StringComparer.Ordinal);
            foreach (var (key, value) in supplied)
            {
                if (string.IsNullOrWhiteSpace(key) || key.Length > OutboxLimits.MaxHeaderKeyLength)
                {
                    throw new ArgumentException($"Header keys must be 1 to {OutboxLimits.MaxHeaderKeyLength} characters.", nameof(supplied));
                }

                if (key.StartsWith(ReservedHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"Header keys starting with '{ReservedHeaderPrefix}' are reserved.", nameof(supplied));
                }

                ArgumentNullException.ThrowIfNull(value, nameof(supplied));
                if (value.Length > OutboxLimits.MaxHeaderValueLength)
                {
                    throw new ArgumentException($"Header values must be at most {OutboxLimits.MaxHeaderValueLength} characters.", nameof(supplied));
                }

                RejectNul(key, "Header keys");
                RejectNul(value, "Header values");

                headers[key] = value;
            }
        }

        var current = Activity.Current;
        if (current is { IdFormat: ActivityIdFormat.W3C, Id: { } traceParent }
            && (headers is null || !headers.ContainsKey(TraceParentHeader)))
        {
            headers ??= new Dictionary<string, string>(2, StringComparer.Ordinal);
            headers[TraceParentHeader] = traceParent;
            if (!string.IsNullOrEmpty(current.TraceStateString))
            {
                headers[TraceStateHeader] = current.TraceStateString;
            }
        }

        return headers;
    }

    private static void ValidateLength(string? value, int maxLength, string name)
    {
        if (value is not null && (value.Length == 0 || value.Length > maxLength))
        {
            throw new ArgumentException($"{name} must be 1 to {maxLength} characters when set.", name);
        }

        RejectNul(value, name);
    }

    /// <summary>PostgreSQL text columns reject U+0000; reject it everywhere so every provider behaves the same.</summary>
    private static void RejectNul(string? value, string name)
    {
        if (value is not null && value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must not contain NUL (U+0000) characters.", name);
        }
    }

    private static Activity? StartEnqueueActivity(string messageType, int count)
    {
        var activity = DbRelayDiagnostics.ActivitySource.StartActivity("DbRelay enqueue", ActivityKind.Producer);
        if (activity is not null)
        {
            activity.SetTag("messaging.system", "dbrelay");
            activity.SetTag("messaging.operation.type", "send");
            activity.SetTag("dbrelay.message.type", messageType);
            activity.SetTag("messaging.batch.message_count", count);
        }

        return activity;
    }
}
