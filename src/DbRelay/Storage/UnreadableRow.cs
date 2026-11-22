namespace DbRelay.Storage;

/// <summary>
/// Defence in depth for storage providers. When a claimed or queried row cannot be mapped to <see cref="OutboxMessageDetails"/>
/// (for example, values written by a non-DbRelay producer that the provider can't read), the provider returns a placeholder
/// created here instead of failing the whole result set. The dispatcher dead-letters such placeholders under their lock token
/// without calling the delivery handler, so one bad row can never stall a claim batch (review findings CR-01 / FU-01).
/// </summary>
public static class UnreadableRow
{
    /// <summary>The reserved header that marks a placeholder. User headers with the <c>dbrelay-</c> prefix are rejected at enqueue.</summary>
    public const string ErrorHeader = "dbrelay-row-error";

    /// <summary>The message type given to placeholders.</summary>
    public const string MessageType = "dbrelay.unreadable-row";

    /// <summary>Creates a placeholder for a row that could not be read.</summary>
    /// <param name="id">The row's message ID. Providers must at least be able to read this.</param>
    /// <param name="status">The row's status, if it could be read; otherwise <see cref="OutboxMessageStatus.Processing"/>.</param>
    /// <param name="attemptCount">The row's attempt count, if it could be read; otherwise <c>1</c>.</param>
    /// <param name="error">The mapping failure. Only its type name and message are kept (truncated).</param>
    /// <returns>A placeholder that <see cref="IsUnreadable"/> recognises.</returns>
    public static OutboxMessageDetails Create(Guid id, OutboxMessageStatus? status, int? attemptCount, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var reason = Internal.ErrorText.Truncate($"The stored row could not be read ({error.GetType().Name}: {error.Message}).");
        var unknown = DateTimeOffset.UnixEpoch;
        return new OutboxMessageDetails
        {
            Id = id,
            MessageType = MessageType,
            Payload = ReadOnlyMemory<byte>.Empty,
            ContentType = "application/octet-stream",
            Headers = new Dictionary<string, string>(1, StringComparer.Ordinal) { [ErrorHeader] = reason },
            Status = status ?? OutboxMessageStatus.Processing,
            AttemptCount = attemptCount ?? 1,
            CreatedAt = unknown,
            ScheduledAt = unknown,
            AvailableAt = unknown,
            LastError = reason,
        };
    }

    /// <summary>Returns <see langword="true"/> if <paramref name="details"/> is a placeholder for an unreadable row.</summary>
    /// <param name="details">The row.</param>
    /// <returns>Whether the row is a placeholder.</returns>
    public static bool IsUnreadable(OutboxMessageDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return details.Headers.ContainsKey(ErrorHeader);
    }
}
