using System.Collections.Concurrent;

namespace CustomHandler.Sample;

/// <summary>
/// A stand-in for a third-party webhook, hosted in the same process so the sample runs without external services.
/// It behaves like a typical receiver:
/// <list type="bullet">
/// <item><description>accepts <c>customers.registered</c>;</description></item>
/// <item><description>throttles the first <c>invoices.issued</c> it sees with <c>429</c> and <c>Retry-After: 2</c>, then accepts it;</description></item>
/// <item><description>rejects any other message type with <c>422</c>;</description></item>
/// <item><description>uses <c>Idempotency-Key</c> to acknowledge duplicates without processing them twice.</description></item>
/// </list>
/// </summary>
internal sealed class DemoWebhookReceiver(ILogger<DemoWebhookReceiver> logger)
{
    private readonly ConcurrentDictionary<string, bool> _processed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _throttled = new(StringComparer.Ordinal);

    public IResult Receive(HttpRequest request)
    {
        var key = request.Headers["Idempotency-Key"].ToString();
        var type = request.Headers["X-Message-Type"].ToString();
        if (key.Length == 0)
        {
            return Respond(StatusCodes.Status400BadRequest, key, type, request);
        }

        if (_processed.ContainsKey(key))
        {
            logger.LogInformation("Webhook: duplicate {IdempotencyKey} acknowledged without processing", key);
            return Results.Ok();
        }

        switch (type)
        {
            case "customers.registered":
                break;
            case "invoices.issued" when _throttled.TryAdd(key, true):
                request.HttpContext.Response.Headers.RetryAfter = "2";
                return Respond(StatusCodes.Status429TooManyRequests, key, type, request);
            case "invoices.issued":
                break;
            default:
                return Respond(StatusCodes.Status422UnprocessableEntity, key, type, request);
        }

        _processed.TryAdd(key, true);
        return Respond(StatusCodes.Status200OK, key, type, request);
    }

    private IResult Respond(int status, string key, string type, HttpRequest request)
    {
        logger.LogInformation(
            "Webhook: {IdempotencyKey} ({MessageType}, {Length} bytes) -> {Status}", key, type, request.ContentLength, status);
        return Results.StatusCode(status);
    }
}
