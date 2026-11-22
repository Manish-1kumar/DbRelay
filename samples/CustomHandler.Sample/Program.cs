using CustomHandler.Sample;
using DbRelay;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Outbox")
    ?? throw new InvalidOperationException("Set the connection string 'ConnectionStrings:Outbox'.");

builder.Services.Configure<WebhookOptions>(builder.Configuration.GetSection("Webhook"));

// snippet: webhook-registration
// A named client from IHttpClientFactory. Leave HttpClient.Timeout longer than DispatcherOptions.DeliveryTimeout so the
// cancellation token DbRelay passes to the handler is what ends a slow call.
builder.Services.AddHttpClient(WebhookDeliveryHandler.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(connectionString, pg => pg.Schema = "webhook_sample");
    relay.UseDeliveryHandler<WebhookDeliveryHandler>();
    relay.ConfigureDispatcher(d =>
    {
        d.PollingInterval = TimeSpan.FromMilliseconds(500);
        d.DeliveryTimeout = TimeSpan.FromSeconds(10);
        d.LeaseDuration = TimeSpan.FromSeconds(30);
    });
    relay.ConfigureRetry(r =>
    {
        r.MaxAttempts = 5;
        r.InitialDelay = TimeSpan.FromSeconds(1);
        r.MaxDelay = TimeSpan.FromMinutes(1);
    });
});
// end-snippet

builder.Services.AddSingleton<DemoWebhookReceiver>();

var app = builder.Build();

await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();

if (app.Configuration.GetValue("Webhook:HostLocalReceiver", true))
{
    app.MapPost("/webhook", (HttpRequest request, DemoWebhookReceiver receiver) => receiver.Receive(request));
}

await app.StartAsync();

if (app.Configuration.GetValue("Demo:EnqueueOnStartup", true))
{
    // EnqueueStandaloneAsync uses a connection DbRelay opens and commits itself. It is NOT atomic with any business write;
    // it is used here only to keep the sample focused on the handler. See PostgreSql.Sample for the transactional pattern.
    var outbox = app.Services.GetRequiredService<IOutbox>();
    var correlation = new EnqueueOptions { CorrelationId = Guid.NewGuid().ToString() };
    await outbox.EnqueueStandaloneAsync(new CustomerRegistered(Guid.NewGuid(), "ada@example.com"), correlation);
    await outbox.EnqueueStandaloneAsync(new InvoiceIssued(Guid.NewGuid(), 99.90m), correlation);
    await outbox.EnqueueStandaloneAsync(new LegacyEvent("a type the receiver does not understand"), correlation);
}

await app.WaitForShutdownAsync();

[OutboxMessageType("customers.registered")]
internal sealed record CustomerRegistered(Guid CustomerId, string Email);

[OutboxMessageType("invoices.issued")]
internal sealed record InvoiceIssued(Guid InvoiceId, decimal Amount);

[OutboxMessageType("legacy.event")]
internal sealed record LegacyEvent(string Text);
