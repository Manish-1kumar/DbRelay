using System.Diagnostics;
using System.Text;
using DbRelay.Configuration;
using DbRelay.Internal;
using DbRelay.Retry;
using DbRelay.Serialization;
using DbRelay.Storage;
using DbRelay.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace DbRelay.Tests;

[OutboxMessageType("orders.created")]
public sealed record OrderCreated(Guid OrderId, decimal Total);

public sealed record Unattributed(string Value);

public sealed record Mapped(int Value);

public sealed class GenericOuter<T>
{
    public sealed class Inner;

    public sealed class Inner2<TInner>;
}

public class RetryPolicyTests
{
    private static ExponentialBackoffRetryPolicy Create(double random, RetryOptions? options = null) =>
        new(options ?? new RetryOptions { InitialDelay = TimeSpan.FromSeconds(1), MaxDelay = TimeSpan.FromMinutes(5), BackoffMultiplier = 2 }, () => random);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    public void Delay_grows_exponentially_with_equal_jitter_bounds(int attempt, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds / 2), Create(0).GetDelay(attempt));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Create(1).GetDelay(attempt));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds * 0.75), Create(0.5).GetDelay(attempt));
    }

    [Fact]
    public void Delay_is_capped_at_max_delay_and_never_overflows()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), Create(1).GetDelay(20));
        Assert.Equal(TimeSpan.FromMinutes(5), Create(1).GetDelay(1000));
        Assert.Equal(TimeSpan.FromMinutes(2.5), Create(0).GetDelay(int.MaxValue));
    }

    [Fact]
    public void Real_random_jitter_spreads_retries_within_bounds()
    {
        var policy = new ExponentialBackoffRetryPolicy(Options.Create(new RetryOptions()));
        var delays = Enumerable.Range(0, 200).Select(_ => policy.GetDelay(4)).ToList();
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)));
        Assert.True(delays.Distinct().Count() > 150, "jitter should de-synchronize retries");
    }

    [Fact]
    public void Attempt_must_be_positive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(0).GetDelay(0));
}

public class OptionsValidationTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(new DispatcherOptionsValidator().Validate(null, new DispatcherOptions()).Succeeded);
        Assert.True(new RetryOptionsValidator().Validate(null, new RetryOptions()).Succeeded);
        Assert.True(new MaintenanceOptionsValidator().Validate(null, new MaintenanceOptions()).Succeeded);
        Assert.True(new OutboxOptionsValidator().Validate(null, new OutboxOptions()).Succeeded);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1001, 8)]
    [InlineData(100, 0)]
    [InlineData(100, 257)]
    public void Dispatcher_rejects_out_of_range_sizes(int batch, int concurrency)
    {
        var result = new DispatcherOptionsValidator().Validate(null, new DispatcherOptions { BatchSize = batch, MaxConcurrency = concurrency });
        Assert.True(result.Failed);
    }

    [Fact]
    public void Lease_must_be_at_least_twice_the_delivery_timeout()
    {
        var result = new DispatcherOptionsValidator().Validate(null, new DispatcherOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(50),
            DeliveryTimeout = TimeSpan.FromSeconds(30),
        });
        Assert.True(result.Failed);
        Assert.Contains("twice", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Retry_rejects_invalid_values()
    {
        var validator = new RetryOptionsValidator();
        Assert.True(validator.Validate(null, new RetryOptions { MaxAttempts = 0 }).Failed);
        Assert.True(validator.Validate(null, new RetryOptions { InitialDelay = TimeSpan.Zero }).Failed);
        Assert.True(validator.Validate(null, new RetryOptions { MaxDelay = TimeSpan.FromMilliseconds(1) }).Failed);
        Assert.True(validator.Validate(null, new RetryOptions { BackoffMultiplier = double.NaN }).Failed);
        Assert.True(validator.Validate(null, new RetryOptions { BackoffMultiplier = 0.5 }).Failed);
    }

    [Fact]
    public void Maintenance_and_outbox_reject_invalid_values()
    {
        Assert.True(new MaintenanceOptionsValidator().Validate(null, new MaintenanceOptions { CleanupBatchSize = 0 }).Failed);
        Assert.True(new MaintenanceOptionsValidator().Validate(null, new MaintenanceOptions { StatisticsInterval = TimeSpan.Zero }).Failed);
        Assert.True(new MaintenanceOptionsValidator().Validate(null, new MaintenanceOptions { DeliveredRetention = TimeSpan.FromSeconds(-1) }).Failed);
        Assert.True(new OutboxOptionsValidator().Validate(null, new OutboxOptions { MaxPayloadBytes = 0 }).Failed);
    }

    [Fact]
    public async Task Invalid_options_fail_host_startup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddDbRelay(relay =>
        {
            relay.UseStorage(_ => new InMemoryOutboxStore());
            relay.DisableDispatcher();
            relay.ConfigureRetry(r => r.MaxAttempts = 0);
        });
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains("MaxAttempts", error.Message, StringComparison.Ordinal);
    }
}

public class SqlIdentifierTests
{
    [Theory]
    [InlineData("dbrelay")]
    [InlineData("_outbox")]
    [InlineData("Outbox_Messages_2")]
    [InlineData("a23456789012345678901234567890123456789012345678901234567890123")]
    public void Accepts_safe_identifiers(string value) => Assert.Equal(value, SqlIdentifier.Validate(value, "Schema"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("x; DROP TABLE y")]
    [InlineData("a\"b")]
    [InlineData("a]b")]
    [InlineData("dbo.outbox")]
    [InlineData("ünicode")]
    [InlineData("outbox_messages\n")]
    [InlineData("outbox_messages\r\n")]
    [InlineData("outbox\tmessages")]
    [InlineData("outbox\0")]
    [InlineData("a234567890123456789012345678901234567890123456789012345678901234")]
    public void Rejects_unsafe_identifiers(string? value) =>
        Assert.Throws<ArgumentException>(() => SqlIdentifier.Validate(value, "Schema"));
}

public class Uuid7Tests
{
    [Fact]
    public void Has_version_7_and_rfc_variant()
    {
        var bytes = Uuid7.NewGuid(DateTimeOffset.UtcNow).ToByteArray(bigEndian: true);
        Assert.Equal(0x70, bytes[6] & 0xF0);
        Assert.Equal(0x80, bytes[8] & 0xC0);
    }

    [Fact]
    public void Encodes_the_unix_millisecond_timestamp_and_sorts_by_time()
    {
        var t = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        var a = Uuid7.NewGuid(t);
        var b = Uuid7.NewGuid(t.AddMilliseconds(1));
        var bytes = a.ToByteArray(bigEndian: true);
        long ms = 0;
        for (var i = 0; i < 6; i++)
        {
            ms = (ms << 8) | bytes[i];
        }

        Assert.Equal(t.ToUnixTimeMilliseconds(), ms);
        Assert.True(string.CompareOrdinal(a.ToString(), b.ToString()) < 0);
        Assert.NotEqual(Uuid7.NewGuid(t), Uuid7.NewGuid(t));
    }
}

public class SerializationTests
{
    [Fact]
    public void Round_trips_with_camel_case_by_default()
    {
        var serializer = new SystemTextJsonMessageSerializer();
        var id = Guid.NewGuid();
        var bytes = serializer.Serialize(new OrderCreated(id, 12.5m));
        Assert.Equal($"{{\"orderId\":\"{id}\",\"total\":12.5}}", Encoding.UTF8.GetString(bytes));
        Assert.Equal(new OrderCreated(id, 12.5m), serializer.Deserialize<OrderCreated>(bytes));
        Assert.Equal("application/json", serializer.ContentType);
    }

    [Fact]
    public void Serializes_the_runtime_type()
    {
        object message = new OrderCreated(Guid.Empty, 1);
        Assert.Contains("orderId", Encoding.UTF8.GetString(new SystemTextJsonMessageSerializer().Serialize(message)), StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_null_and_empty()
    {
        var serializer = new SystemTextJsonMessageSerializer();
        Assert.Throws<ArgumentNullException>(() => serializer.Serialize<OrderCreated?>(null));
        Assert.Throws<InvalidOperationException>(() => serializer.Deserialize<OrderCreated>([]));
        Assert.Throws<InvalidOperationException>(() => serializer.Deserialize<OrderCreated>("null"u8));
    }

    [Fact]
    public void Type_names_prefer_explicit_mapping_then_attribute_then_full_name()
    {
        var registry = new MessageTypeRegistry(new Dictionary<Type, string> { [typeof(Mapped)] = "mapped.v1" });
        Assert.Equal("mapped.v1", registry.GetName(typeof(Mapped)));
        Assert.Equal("orders.created", registry.GetName(typeof(OrderCreated)));
        Assert.Equal(typeof(Unattributed).FullName, registry.GetName(typeof(Unattributed)));
    }

    [Fact]
    public void Generic_type_names_do_not_embed_assembly_versions()
    {
        var name = MessageTypeRegistry.FormatClrName(typeof(Dictionary<string, List<Mapped>>));
        Assert.Equal("System.Collections.Generic.Dictionary<System.String,System.Collections.Generic.List<DbRelay.Tests.Mapped>>", name);
        Assert.DoesNotContain("Version=", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Nested_generic_and_array_type_names_are_distinct_and_version_free()
    {
        // FU-07: these used to collide or embed assembly versions.
        var outer = MessageTypeRegistry.FormatClrName(typeof(GenericOuter<int>));
        var inner = MessageTypeRegistry.FormatClrName(typeof(GenericOuter<int>.Inner));
        var inner2 = MessageTypeRegistry.FormatClrName(typeof(GenericOuter<int>.Inner2<string>));
        var array = MessageTypeRegistry.FormatClrName(typeof(List<int>[]));
        var jagged = MessageTypeRegistry.FormatClrName(typeof(int[,]));

        Assert.Equal("DbRelay.Tests.GenericOuter<System.Int32>", outer);
        Assert.Equal("DbRelay.Tests.GenericOuter<System.Int32>+Inner", inner);
        Assert.Equal("DbRelay.Tests.GenericOuter<System.Int32>+Inner2<System.String>", inner2);
        Assert.Equal("System.Collections.Generic.List<System.Int32>[]", array);
        Assert.Equal("System.Int32[,]", jagged);
        Assert.Equal(5, new[] { outer, inner, inner2, array, jagged }.Distinct().Count());
        Assert.All(new[] { outer, inner, inner2, array }, n => Assert.DoesNotContain("Version=", n, StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_row_placeholders_are_recognisable_and_safe()
    {
        var id = Guid.NewGuid();
        var placeholder = UnreadableRow.Create(id, OutboxMessageStatus.Processing, 3, new InvalidCastException("bad \0 value"));
        Assert.True(UnreadableRow.IsUnreadable(placeholder));
        Assert.Equal(id, placeholder.Id);
        Assert.Equal(3, placeholder.AttemptCount);
        Assert.Contains("InvalidCastException", placeholder.Headers[UnreadableRow.ErrorHeader], StringComparison.Ordinal);
        Assert.DoesNotContain('\0', placeholder.Headers[UnreadableRow.ErrorHeader]); // sanitised like any stored error text
        Assert.False(UnreadableRow.IsUnreadable(new OutboxMessageDetails
        {
            Id = id,
            MessageType = "t",
            Payload = Array.Empty<byte>(),
            ContentType = "c",
            Headers = new Dictionary<string, string>(),
            Status = OutboxMessageStatus.Pending,
            AttemptCount = 0,
            CreatedAt = default,
            ScheduledAt = default,
            AvailableAt = default,
        }));
    }

    [Fact]
    public void Serializer_does_not_freeze_the_callers_options()
    {
        var options = new System.Text.Json.JsonSerializerOptions();
        _ = new SystemTextJsonMessageSerializer(options);
        options.WriteIndented = true; // would throw InvalidOperationException if the caller's instance had been frozen
        Assert.False(options.IsReadOnly);
    }

    [Fact]
    public void Headers_round_trip_and_empty_is_null()
    {
        Assert.Null(OutboxHeaders.Serialize(null));
        Assert.Null(OutboxHeaders.Serialize(new Dictionary<string, string>()));
        var json = OutboxHeaders.Serialize(new Dictionary<string, string> { ["a"] = "1", ["ü"] = "\"quoted\"" });
        var parsed = OutboxHeaders.Deserialize(json);
        Assert.Equal("1", parsed["a"]);
        Assert.Equal("\"quoted\"", parsed["ü"]);
        Assert.Empty(OutboxHeaders.Deserialize(null));
    }

    [Theory]
    [InlineData("{\"retries\": 1, \"ok\": \"x\"}", "retries", "1")]
    [InlineData("{\"nested\": {\"a\": [1, 2]}}", "nested", "{\"a\": [1, 2]}")]
    [InlineData("{\"flag\": true, \"none\": null}", "none", "null")]
    public void Header_parsing_never_throws_on_non_string_values(string json, string key, string expected)
    {
        var headers = OutboxHeaders.Deserialize(json);
        Assert.Equal(expected, headers[key]);
        Assert.False(headers.ContainsKey(OutboxHeaders.ErrorHeader));
    }

    [Theory]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("not json at all")]
    [InlineData("{\"unterminated\": ")]
    [InlineData("{\"a\":\"\\ud800\"}")]          // FU-01: escaped lone high surrogate in a value -> GetString() throws
    [InlineData("{\"\\udc00\":\"v\"}")]          // FU-01: escaped lone low surrogate in a key -> Name throws
    [InlineData("{\"a\":\"x\\ud83d\"}")]         // FU-01: string cut mid-emoji by a JavaScript producer
    public void Header_parsing_never_throws_on_unreadable_headers(string json)
    {
        var headers = OutboxHeaders.Deserialize(json);
        Assert.Single(headers);
        Assert.Contains("ignored", headers[OutboxHeaders.ErrorHeader], StringComparison.Ordinal);
    }

    [Fact]
    public void Header_parsing_never_throws_on_a_raw_lone_surrogate()
    {
        // Built at runtime: attribute arguments are stored as UTF-8 in metadata and could turn a lone surrogate into U+FFFD.
        var json = "{\"a\":\"" + '\uD800' + "\"}";
        Assert.Equal(1, json.Count(char.IsSurrogate));
        var headers = OutboxHeaders.Deserialize(json);
        Assert.Single(headers);
        Assert.True(headers.ContainsKey(OutboxHeaders.ErrorHeader));
    }

    [Fact]
    public void Error_text_is_truncated_without_stack_traces()
    {
        var text = ErrorText.FromException(new InvalidOperationException(new string('x', 5000)));
        Assert.Equal(OutboxLimits.MaxErrorLength, text.Length);
        Assert.StartsWith("InvalidOperationException: xxx", text, StringComparison.Ordinal);
        Assert.Equal("short", ErrorText.Truncate("short"));
    }

    [Fact]
    public void Error_text_is_always_storable()
    {
        Assert.Equal("a�b", ErrorText.Truncate("a\0b"));
        Assert.Equal("x�y", ErrorText.Truncate("x\uD83Dy"));   // lone high surrogate
        Assert.Equal("x�y", ErrorText.Truncate("x\uDE00y"));   // lone low surrogate
        Assert.Equal("ok 😀", ErrorText.Truncate("ok 😀"));          // valid pair untouched

        // An emoji straddling the cut must not be split.
        var straddling = new string('a', OutboxLimits.MaxErrorLength - 2) + "😀" + new string('b', 10);
        var truncated = ErrorText.Truncate(straddling);
        Assert.True(truncated.Length <= OutboxLimits.MaxErrorLength);
        Assert.False(char.IsHighSurrogate(truncated[^2]));
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
        Assert.Equal(truncated, new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(truncated)));
    }
}

public class DispatcherStateTests
{
    [Fact]
    public void Finished_deliveries_count_as_progress_while_no_polls_happen()
    {
        var state = new DbRelay.Dispatching.DispatcherState();
        var t0 = new DateTimeOffset(2026, 11, 19, 0, 0, 0, TimeSpan.Zero);
        state.PollSucceeded(t0);
        state.RecordActivity(t0.AddMinutes(5));
        var snapshot = state.GetSnapshot();
        Assert.Equal(t0, snapshot.LastSuccessfulPoll);
        Assert.Equal(t0.AddMinutes(5), snapshot.LastProgress);

        state.PollSucceeded(t0.AddMinutes(6));
        Assert.Equal(t0.AddMinutes(6), state.GetSnapshot().LastProgress);
        Assert.Equal(1, state.PollFailed(new TimeoutException()));
        Assert.Equal(2, state.PollFailed(new TimeoutException()));
        state.PollSucceeded(t0.AddMinutes(7));
        Assert.Equal(0, state.GetSnapshot().ConsecutiveFailures);
    }
}

public class AbandonedHandlerCounterTests
{
    [Fact]
    public void Concurrent_start_and_finish_never_leaves_a_stale_count()
    {
        // Regression: the count used to be copied with a separate write after an atomic update, so a stale write could win and
        // leave the health check Degraded forever.
        var state = new DbRelay.Dispatching.DispatcherState();
        Parallel.For(0, 200_000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            state.AbandonedHandlerStarted();
            state.AbandonedHandlerFinished();
        });
        Assert.Equal(0, state.AbandonedHandlers);
    }
}

public class DeliveryResultTests
{
    [Fact]
    public void Factories_validate_input()
    {
        Assert.Same(DeliveryResult.Delivered(), DeliveryResult.Delivered());
        Assert.Equal(DeliveryOutcome.Retry, DeliveryResult.Retry("busy", TimeSpan.FromSeconds(3)).Outcome);
        Assert.Equal(TimeSpan.FromSeconds(3), DeliveryResult.Retry("busy", TimeSpan.FromSeconds(3)).RetryAfter);
        Assert.Equal("bad", DeliveryResult.DeadLetter("bad").Reason);
        Assert.Throws<ArgumentException>(() => DeliveryResult.Retry(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryResult.Retry("x", TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentException>(() => DeliveryResult.DeadLetter(""));
    }
}

public class RegistrationTests
{
    [Fact]
    public void Requires_a_storage_provider()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbRelay(relay => relay.DisableDispatcher()));
        Assert.Contains("UsePostgreSql", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requires_a_delivery_handler_unless_dispatcher_disabled()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddDbRelay(relay => relay.UseStorage(_ => new InMemoryOutboxStore())));
        Assert.Contains("DisableDispatcher", error.Message, StringComparison.Ordinal);

        new ServiceCollection().AddDbRelay(relay => relay.UseStorage(_ => new InMemoryOutboxStore()).DisableDispatcher());
    }

    [Fact]
    public void Rejects_double_registration()
    {
        var services = new ServiceCollection();
        services.AddDbRelay(relay => relay.UseStorage(_ => new InMemoryOutboxStore()).DisableDispatcher());
        Assert.Throws<InvalidOperationException>(() => services.AddDbRelay(relay => relay.UseStorage(_ => new InMemoryOutboxStore()).DisableDispatcher()));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbRelay(relay =>
            relay.UseStorage(_ => new InMemoryOutboxStore()).UseStorage(_ => new InMemoryOutboxStore())));

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddDbRelay(relay => relay
            .UseStorage(_ => new InMemoryOutboxStore())
            .UseDeliveryHandler((_, _) => Task.FromResult(DeliveryResult.Delivered()))
            .UseDeliveryHandler((_, _) => Task.FromResult(DeliveryResult.Delivered()))));
    }

    [Fact]
    public void Rejects_duplicate_message_type_names()
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddDbRelay(relay => relay
            .UseStorage(_ => new InMemoryOutboxStore())
            .DisableDispatcher()
            .MapMessageType<Mapped>("same")
            .MapMessageType<Unattributed>("same")));
    }
}

public sealed class OutboxValidationTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly InMemoryOutboxStore _store = new();
    private readonly FakeConnection _connection = new();

    public OutboxValidationTests()
    {
        var services = new ServiceCollection();
        services.AddDbRelay(relay => relay
            .UseStorage(_ => _store)
            .DisableDispatcher()
            .MapMessageType<Mapped>("mapped")
            .Configure(o => o.MaxPayloadBytes = 1024));
        _services = services.BuildServiceProvider();
    }

    private IOutbox Outbox => _services.GetRequiredService<IOutbox>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Enqueue_returns_a_uuid7_and_stores_mapped_type()
    {
        var transaction = _connection.BeginTransaction();
        var id = await Outbox.EnqueueAsync(transaction, new Mapped(1), Ct);
        Assert.Equal(0x70, id.ToByteArray(bigEndian: true)[6] & 0xF0); // UUID version 7
        Assert.Equal("mapped", _store.Get(id).MessageType);
        Assert.Equal("{\"value\":1}", Encoding.UTF8.GetString(_store.Get(id).Payload.Span));
    }

    [Fact]
    public async Task Enqueue_never_completes_or_disposes_caller_resources()
    {
        var transaction = (FakeTransaction)_connection.BeginTransaction();
        await Outbox.EnqueueAsync(transaction, new Mapped(1), Ct);
        await Outbox.EnqueueManyAsync(transaction, [new Mapped(2), new Mapped(3)], Ct);
        Assert.False(transaction.Committed);
        Assert.False(transaction.RolledBack);
        Assert.False(_connection.WasDisposed);
        Assert.Equal(System.Data.ConnectionState.Open, _connection.State);
    }

    [Fact]
    public async Task Rejects_invalid_caller_resources()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Outbox.EnqueueAsync(null!, null, new Mapped(1), null, Ct));
        var transaction = (FakeTransaction)_connection.BeginTransaction();
        transaction.Commit();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Outbox.EnqueueAsync(transaction, new Mapped(1), Ct));

        using var other = new FakeConnection();
        var foreignTransaction = other.BeginTransaction();
        await Assert.ThrowsAsync<ArgumentException>(() => Outbox.EnqueueAsync(_connection, foreignTransaction, new Mapped(1), null, Ct));

        using var closed = new FakeConnection();
        closed.Close();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Outbox.EnqueueAsync(closed, null, new Mapped(1), null, Ct));
    }

    [Fact]
    public async Task Rejects_invalid_messages_and_options()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Outbox.EnqueueAsync<Mapped?>(_connection, null, null, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Outbox.EnqueueAsync(_connection, null, new Unattributed(new string('x', 2000)), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { DeliverAt = DateTimeOffset.UtcNow, Delay = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Enqueue(new EnqueueOptions { Delay = TimeSpan.FromSeconds(-1) }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { MessageId = Guid.Empty }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { Destination = new string('d', 513) }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { CorrelationId = "" }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { Headers = new Dictionary<string, string> { ["dbrelay-x"] = "1" } }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { Headers = Enumerable.Range(0, 65).ToDictionary(i => $"h{i}", i => "v") }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { Headers = new Dictionary<string, string> { ["k"] = new string('v', 4097) } }));
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { CorrelationId = "a\0b" }));
        var forgottenMessage = Assert.Throws<ArgumentException>(() =>
            Outbox.EnqueueAsync(_connection.BeginTransaction(), Ct).GetAwaiter().GetResult());
        Assert.Contains("message argument appears to be missing", forgottenMessage.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => Enqueue(new EnqueueOptions { Headers = new Dictionary<string, string> { ["k"] = "v\0" } }));
    }

    [Fact]
    public async Task EnqueueMany_validates_batch()
    {
        Assert.Empty(await Outbox.EnqueueManyAsync(_connection, null, Array.Empty<Mapped>(), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Outbox.EnqueueManyAsync(_connection, null, [new Mapped(1)], new EnqueueOptions { MessageId = Guid.NewGuid() }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Outbox.EnqueueManyAsync(_connection, null, Enumerable.Range(0, 1001).Select(i => new Mapped(i)), null, Ct));
        var ids = await Outbox.EnqueueManyAsync(_connection, null, Enumerable.Range(0, 1000).Select(i => new Mapped(i)), null, Ct);
        Assert.Equal(1000, ids.Distinct().Count());
    }

    [Fact]
    public async Task Captures_w3c_trace_context_without_overriding_user_headers()
    {
        using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.TraceStateString = "vendor=1";
        var id = await Enqueue(new EnqueueOptions { Headers = new Dictionary<string, string> { ["tenant"] = "a" } });
        var headers = _store.Get(id).Headers;
        Assert.Equal(activity.Id, headers["traceparent"]);
        Assert.Equal("vendor=1", headers["tracestate"]);
        Assert.Equal("a", headers["tenant"]);

        var own = await Enqueue(new EnqueueOptions { Headers = new Dictionary<string, string> { ["traceparent"] = "mine" } });
        Assert.Equal("mine", _store.Get(own).Headers["traceparent"]);
    }

    [Fact]
    public async Task Raw_payloads_are_stored_unchanged()
    {
        byte[] bytes = [0, 1, 2, 255];
        var id = await Outbox.EnqueueRawAsync(_connection, null, new RawOutboxMessage("raw.type", bytes, "application/x-protobuf"), null, Ct);
        Assert.Equal(bytes, _store.Get(id).Payload.ToArray());
        Assert.Equal("application/x-protobuf", _store.Get(id).ContentType);
    }

    private Task<Guid> Enqueue(EnqueueOptions options) => Outbox.EnqueueAsync(_connection, null, new Mapped(1), options, Ct);
}
