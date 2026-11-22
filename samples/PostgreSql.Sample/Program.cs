using Dapper;
using DbRelay;
using Npgsql;
using PostgreSql.Sample;

var builder = WebApplication.CreateBuilder(args);

// The schema holds both the sample's business table and the outbox table. It is a constant, not user input.
const string Schema = "sample";

var connectionString = builder.Configuration.GetConnectionString("Outbox")
    ?? throw new InvalidOperationException("Set the connection string 'ConnectionStrings:Outbox'.");

// snippet: pg-registration
// One NpgsqlDataSource for business writes and for DbRelay. The DI container owns it; DbRelay never disposes it.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

builder.Services.AddDbRelay(relay =>
{
    relay.UsePostgreSql(sp => sp.GetRequiredService<NpgsqlDataSource>(), pg => pg.Schema = Schema);
    relay.UseDeliveryHandler<LoggingDeliveryHandler>();
    relay.ConfigureDispatcher(d => d.PollingInterval = TimeSpan.FromMilliseconds(500));
});

// Unhealthy when the database is unreachable; Degraded when the dispatcher stalls or the backlog is too old.
builder.Services.AddHealthChecks().AddDbRelay(configure: health => health.MaxOldestDueAge = TimeSpan.FromMinutes(5));
// end-snippet

var app = builder.Build();

// snippet: pg-schema
// Explicit, idempotent schema creation. It never drops or alters anything. If the application's database user may not run
// DDL, run IOutboxSchemaManager.GetCreateScript() through your migration tool instead.
await app.Services.GetRequiredService<IOutboxSchemaManager>().EnsureCreatedAsync();
// end-snippet

await using (var connection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
{
    // The sample's business table, in the schema EnsureCreatedAsync just created.
    await connection.ExecuteAsync(
        "CREATE TABLE IF NOT EXISTS sample.orders (id uuid PRIMARY KEY, customer text NOT NULL, total numeric(12, 2) NOT NULL)");
}

// snippet: pg-enqueue
app.MapPost("/orders", async (CreateOrderRequest request, NpgsqlDataSource dataSource, IOutbox outbox, CancellationToken ct) =>
{
    var order = new { Id = Guid.NewGuid(), request.Customer, request.Total };

    // The application owns the connection and the transaction: it opens, commits and disposes them.
    // DbRelay only inserts the outbox row with this connection, inside this transaction. It never commits,
    // rolls back or disposes either of them.
    await using var connection = await dataSource.OpenConnectionAsync(ct);
    await using var transaction = await connection.BeginTransactionAsync(ct);

    await connection.ExecuteAsync(
        "INSERT INTO sample.orders (id, customer, total) VALUES (@Id, @Customer, @Total)", order, transaction);

    var messageId = await outbox.EnqueueAsync(transaction, new OrderCreated(order.Id, order.Customer, order.Total), ct);

    // The order and the message commit together, or neither does. If this line is never reached,
    // disposing the transaction rolls both back and nothing is delivered.
    await transaction.CommitAsync(ct);

    return Results.Created($"/orders/{order.Id}", new { orderId = order.Id, messageId });
});
// end-snippet

// snippet: pg-admin
app.MapGet("/outbox/stats", (IOutboxAdmin admin, CancellationToken ct) => admin.GetStatisticsAsync(ct));

app.MapGet("/outbox/messages/{id:guid}", async (Guid id, IOutboxAdmin admin, CancellationToken ct) =>
    await admin.GetAsync(id, ct) is { } message
        ? Results.Ok(new
        {
            message.Id,
            message.MessageType,
            Status = message.Status.ToString(),
            message.AttemptCount,
            message.CreatedAt,
            message.DeliveredAt,
            message.LastError,
        })
        : Results.NotFound());

app.MapHealthChecks("/health");
// end-snippet

app.Run();
