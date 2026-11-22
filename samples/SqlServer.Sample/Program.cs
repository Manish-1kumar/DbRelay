using Dapper;
using DbRelay;
using Microsoft.Data.SqlClient;
using SqlServer.Sample;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Outbox")
    ?? throw new InvalidOperationException("Set the connection string 'ConnectionStrings:Outbox'.");

// snippet: sqlserver-registration
builder.Services.AddDbRelay(relay =>
{
    // DbRelay opens its own connections from this string for dispatching, administration and schema creation.
    // Transactional enqueues always use the caller's SqlConnection and SqlTransaction.
    relay.UseSqlServer(connectionString, sql => sql.Schema = "sample");

    // This worker enqueues and delivers. In an application that should only enqueue (for example a web API whose messages
    // are delivered by a separate worker), remove UseDeliveryHandler and call DisableDispatcher instead:
    // relay.DisableDispatcher();
    relay.UseDeliveryHandler<ConsoleDeliveryHandler>();

    relay.ConfigureDispatcher(dispatcher =>
    {
        dispatcher.PollingInterval = TimeSpan.FromMilliseconds(500);
        dispatcher.DeliveryTimeout = TimeSpan.FromSeconds(20);
        dispatcher.LeaseDuration = TimeSpan.FromMinutes(1); // must be at least twice DeliveryTimeout
    });

    // snippet: retry-configuration
    relay.ConfigureRetry(retry =>
    {
        retry.MaxAttempts = 8;                        // including the first attempt; then the message is dead-lettered
        retry.InitialDelay = TimeSpan.FromSeconds(2); // delay before the first retry
        retry.BackoffMultiplier = 3;                  // 2 s, 6 s, 18 s, ... before jitter
        retry.MaxDelay = TimeSpan.FromMinutes(10);    // cap for computed delays and for handler-supplied RetryAfter

        // Exceptions are transient by default. Classify the ones that can never succeed as permanent.
        retry.IsPermanentFailure = exception => exception is FormatException or NotSupportedException;
    });
    // end-snippet
});
// end-snippet

builder.Services.AddHostedService<OrderProducer>();

using var host = builder.Build();

// Explicit, idempotent schema creation, then the sample's business table.
await host.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
await using (var connection = new SqlConnection(connectionString))
{
    await connection.ExecuteAsync(
        """
        IF OBJECT_ID(N'[sample].[orders]', N'U') IS NULL
            CREATE TABLE [sample].[orders] (
                [id] uniqueidentifier NOT NULL PRIMARY KEY,
                [customer] nvarchar(200) NOT NULL,
                [total] decimal(12, 2) NOT NULL);
        """);
}

await host.RunAsync();
