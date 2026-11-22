using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbRelay.IntegrationTests;

/// <summary>Scenario 9: payloads and connection strings never reach logs; stored error text is bounded.</summary>
public sealed class SecurityTests(PostgreSqlFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Payloads_and_connection_strings_never_appear_in_logs_while_delivering_retrying_and_dead_lettering()
    {
        var secret = "PAYLOAD-SECRET-" + Guid.NewGuid().ToString("N");
        var appName = "dbrelay-it-sec-" + Guid.NewGuid().ToString("N")[..12];
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();

        var ids = new Dictionary<string, Guid>();
        foreach (var kind in new[] { "deliver", "dead-letter", "throw-permanent", "retry-then-exhaust" })
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            ids[kind] = await outbox.EnqueueAsync(transaction, new OrderPlaced(Guid.NewGuid(), 1, kind + ":" + secret), new EnqueueOptions { Destination = kind }, Ct);
            await transaction.CommitAsync(Ct);
        }

        // A raw (non-JSON) payload carrying the marker in its bytes as well.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync(Ct))
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            ids["raw"] = await outbox.EnqueueRawAsync(
                transaction,
                new RawOutboxMessage("raw.secret", Encoding.UTF8.GetBytes("raw:" + secret), "application/octet-stream"),
                Ct);
            await transaction.CommitAsync(Ct);
        }

        var logs = new CapturingLoggerProvider();
        using var host = RelayHost.Build(
            schema,
            appName,
            (message, _) =>
            {
                // The handler sees the payload (it must), but only reports non-sensitive reasons.
                Assert.Contains(secret, Encoding.UTF8.GetString(message.Payload.Span), StringComparison.Ordinal);
                return message.Destination switch
                {
                    "dead-letter" => Task.FromResult(DeliveryResult.DeadLetter("rejected by broker")),
                    "throw-permanent" => throw new FormatException("poison message"),
                    "retry-then-exhaust" => throw new TimeoutException("broker timeout"),
                    _ => Task.FromResult(DeliveryResult.Delivered()),
                };
            },
            retry: r =>
            {
                r.MaxAttempts = 2;
                r.InitialDelay = TimeSpan.FromMilliseconds(20);
                r.MaxDelay = TimeSpan.FromMilliseconds(50);
                r.IsPermanentFailure = e => e is FormatException;
            },
            logs: logs);
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRowsAsync()).All(r => r.State is OutboxMessageStatus.Delivered or OutboxMessageStatus.DeadLettered),
            TimeSpan.FromSeconds(30),
            "every message to reach a final state");
        await host.StopAsync(Ct);

        Assert.Equal(OutboxMessageStatus.Delivered, (await schema.GetRequiredRowAsync(ids["deliver"])).State);
        Assert.Equal(OutboxMessageStatus.Delivered, (await schema.GetRequiredRowAsync(ids["raw"])).State);
        Assert.Equal(OutboxMessageStatus.DeadLettered, (await schema.GetRequiredRowAsync(ids["dead-letter"])).State);
        Assert.Equal(OutboxMessageStatus.DeadLettered, (await schema.GetRequiredRowAsync(ids["throw-permanent"])).State);
        Assert.Equal(OutboxMessageStatus.DeadLettered, (await schema.GetRequiredRowAsync(ids["retry-then-exhaust"])).State);

        var text = logs.AllText();

        // The capture works (not a vacuous pass): delivery, retry and dead-letter events with message IDs were logged.
        Assert.True(logs.Count(1004) >= 2, "Expected Delivered (1004) debug logs.");
        Assert.True(logs.Count(1005) >= 1, "Expected RetryScheduled (1005) logs.");
        Assert.True(logs.Count(1006) >= 3, "Expected DeadLettered (1006) logs.");
        Assert.Contains(ids["dead-letter"].ToString(), text, StringComparison.Ordinal);

        Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes("raw:" + secret)), text, StringComparison.Ordinal);
        Assert.DoesNotContain(appName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Long_error_text_is_truncated_to_2000_characters_in_the_database()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var outbox = producer.GetRequiredService<IOutbox>();
        var ids = new Dictionary<string, Guid>();
        foreach (var kind in new[] { "exception", "dead-letter-reason", "retry-reason" })
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            ids[kind] = await outbox.EnqueueAsync(transaction, new OrderPlaced(Guid.NewGuid(), 1, kind), new EnqueueOptions { Destination = kind }, Ct);
            await transaction.CommitAsync(Ct);
        }

        var longText = string.Concat(Enumerable.Repeat("0123456789", 1000)); // 10,000 characters
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-trunc",
            (message, _) => message.Destination switch
            {
                "exception" => throw new InvalidOperationException(longText),
                "dead-letter-reason" => Task.FromResult(DeliveryResult.DeadLetter(longText)),
                _ => Task.FromResult(DeliveryResult.Retry(longText, TimeSpan.FromHours(1))),
            },
            retry: r =>
            {
                r.MaxAttempts = 1;
                r.MaxDelay = TimeSpan.FromHours(2);
            });

        // retry-reason needs a second attempt allowed to stay Pending: run it with MaxAttempts = 1 would dead-letter it, so the
        // exhausted path ("Max attempts (1) reached. Last error: ...") is what is checked for it and for the exception.
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRowsAsync()).All(r => r.State == OutboxMessageStatus.DeadLettered),
            TimeSpan.FromSeconds(30),
            "every message to be dead-lettered");
        await host.StopAsync(Ct);

        foreach (var (kind, id) in ids)
        {
            var row = await schema.GetRequiredRowAsync(id);
            Assert.Equal(2000, row.LastErrorLength);
            Assert.EndsWith("…", row.LastError, StringComparison.Ordinal);
            Assert.DoesNotContain(" at ", row.LastError, StringComparison.Ordinal); // no stack trace
            _ = kind;
        }

        Assert.StartsWith("Max attempts (1) reached. Last error: InvalidOperationException: 0123", (await schema.GetRequiredRowAsync(ids["exception"])).LastError, StringComparison.Ordinal);
        Assert.StartsWith("0123456789", (await schema.GetRequiredRowAsync(ids["dead-letter-reason"])).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_retry_reason_is_truncated_while_the_message_waits_for_its_retry()
    {
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);
        var longText = new string('r', 5000);
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-trunc-retry",
            (_, _) => Task.FromResult(DeliveryResult.Retry(longText, TimeSpan.FromHours(1))),
            retry: r =>
            {
                r.MaxAttempts = 5;
                r.MaxDelay = TimeSpan.FromHours(2);
            });
        await host.StartAsync(Ct);
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)) is { State: OutboxMessageStatus.Pending, AttemptCount: 1 },
            TimeSpan.FromSeconds(30),
            "the retry to be scheduled");
        await host.StopAsync(Ct);

        var row = await schema.GetRequiredRowAsync(id);
        Assert.Equal(2000, row.LastErrorLength);
        Assert.True(row.AvailableAt > row.DbNow.AddMinutes(59), "retryAfter (1h) was not applied by the database.");
    }

    // ---- Error text the database cannot store ------------------------------------------------------------------------
    // PostgreSQL text cannot hold U+0000, and Npgsql refuses to encode an unpaired UTF-16 surrogate. Both can come from
    // ordinary exception messages or handler reasons. The outcome must still be recorded; otherwise the message is redelivered
    // on every lease expiry until the crash-loop guard dead-letters it, and the real error is lost.

    public static TheoryData<string, string> UnstorableErrors => new()
    {
        { "nul-in-exception", "exception" },
        { "nul-in-dead-letter-reason", "reason" },
        { "surrogate-split-by-truncation", "reason" },
    };

    [Theory]
    [MemberData(nameof(UnstorableErrors))]
    public async Task Error_text_that_postgresql_cannot_store_still_records_the_outcome(string probe, string via)
    {
        const int MaxAttempts = 3;
        await using var schema = await TestSchema.CreateAsync(fixture);
        await using var producer = schema.CreateProducer();
        var id = await schema.EnqueueOrderAsync(producer.GetRequiredService<IOutbox>(), 1, cancellationToken: Ct);

        var error = probe switch
        {
            "surrogate-split-by-truncation" => new string('x', 1998) + "\U0001F600" + new string('y', 100), // emoji straddles the cut
            _ => "bad byte \0 in error from a binary protocol",
        };

        var logs = new CapturingLoggerProvider();
        var handlerCalls = 0;
        using var host = RelayHost.Build(
            schema,
            "dbrelay-it-unstorable",
            (_, _) =>
            {
                Interlocked.Increment(ref handlerCalls);
                return via == "exception"
                    ? throw new InvalidOperationException(error)
                    : Task.FromResult(DeliveryResult.DeadLetter(error)); // a permanent failure: must never be redelivered
            },
            d =>
            {
                d.LeaseDuration = TimeSpan.FromSeconds(2);
                d.DeliveryTimeout = TimeSpan.FromSeconds(1);
            },
            r =>
            {
                r.MaxAttempts = MaxAttempts;
                r.IsPermanentFailure = _ => true; // the exception variant is permanent too
            },
            logs: logs);
        await host.StartAsync(Ct);

        // Long enough for the expected immediate dead-letter, and for the buggy path (persist fails, lease lapses, redelivery
        // until the crash-loop guard) to finish, so the report shows the real consequence.
        await Wait.UntilAsync(
            async () => (await schema.GetRequiredRowAsync(id)).State == OutboxMessageStatus.DeadLettered,
            TimeSpan.FromSeconds(30),
            "the message to reach DeadLettered");
        await host.StopAsync(Ct);

        var row = await schema.GetRequiredRowAsync(id);
        var persistError = logs.Entries.FirstOrDefault(e => e.EventId.Id == 1009)?.Exception?.Split('\n')[0].Trim();
        Metrics.Report(
            $"{nameof(Error_text_that_postgresql_cannot_store_still_records_the_outcome)}({probe})",
            $"status={row.State} attemptCount={row.AttemptCount} handlerCalls={handlerCalls} persistRetries={logs.Count(1008)} " +
            $"persistFailed={logs.Count(1009)} lastError='{row.LastError?[..Math.Min(90, row.LastError.Length)]}' persistException='{persistError}'");

        Assert.Equal(1, handlerCalls); // a permanent failure is delivered once, not once per lease
        Assert.Equal(0, logs.Count(1009)); // "Recording outcome '...' failed"
        Assert.Equal(1, row.AttemptCount);
        Assert.NotNull(row.LastError);
        Assert.DoesNotContain("possible worker crash loop", row.LastError, StringComparison.Ordinal); // the real reason survives
        Assert.InRange(row.LastErrorLength ?? 0, 1, 2000);
    }
}
