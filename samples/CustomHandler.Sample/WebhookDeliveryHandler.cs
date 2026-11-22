using System.Net;
using System.Net.Http.Headers;
using DbRelay;
using Microsoft.Extensions.Options;

namespace CustomHandler.Sample;

/// <summary>Where the webhook handler posts messages.</summary>
public sealed class WebhookOptions
{
    /// <summary>The absolute URL that receives one POST per message.</summary>
    public Uri Url { get; set; } = new("http://localhost:5080/webhook");
}

// snippet: webhook-handler
/// <summary>
/// Posts each outbox message to an HTTP webhook and maps the response to a <see cref="DeliveryResult"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>2xx: delivered.</description></item>
/// <item><description>408, 429 and 503: retry, honouring the <c>Retry-After</c> header when present.</description></item>
/// <item><description>Other 4xx: the receiver rejected the message and will keep rejecting it, so dead-letter it.</description></item>
/// <item><description>Other statuses and network errors: retry with DbRelay's backoff.</description></item>
/// </list>
/// The message ID is sent as the <c>Idempotency-Key</c> header. It is the same on every attempt, so the receiver can drop
/// duplicates: delivery is at-least-once.
/// </remarks>
internal sealed class WebhookDeliveryHandler(
    IHttpClientFactory httpClientFactory,
    IOptions<WebhookOptions> options,
    TimeProvider time) : IMessageDeliveryHandler
{
    public const string HttpClientName = "webhook";

    public async Task<DeliveryResult> DeliverAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Url);
        request.Headers.Add("Idempotency-Key", message.Id.ToString());
        request.Headers.Add("X-Message-Type", message.MessageType);
        if (message.CorrelationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", message.CorrelationId);
        }

        // The stored bytes are sent unchanged.
        request.Content = new ReadOnlyMemoryContent(message.Payload);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(message.ContentType);

        var client = httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage response;
        try
        {
            // Pass the token: DbRelay cancels it when DispatcherOptions.DeliveryTimeout elapses or the host shuts down.
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Connection refused, DNS failure, reset: transient. The reason is stored, so keep it free of secrets.
            return DeliveryResult.Retry($"Webhook unreachable: {exception.HttpRequestError}");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return DeliveryResult.Delivered();
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout)
            {
                // RetryAfter overrides the computed backoff; DbRelay caps it at RetryOptions.MaxDelay.
                return DeliveryResult.Retry($"Webhook returned {status}", GetRetryAfter(response));
            }

            if (status is >= 400 and < 500)
            {
                // Do not store the response body: it may echo the payload.
                return DeliveryResult.DeadLetter($"Webhook rejected the message with {status} {response.ReasonPhrase}");
            }

            return DeliveryResult.Retry($"Webhook returned {status}");
        }
    }

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null; // use DbRelay's exponential backoff
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
// end-snippet
