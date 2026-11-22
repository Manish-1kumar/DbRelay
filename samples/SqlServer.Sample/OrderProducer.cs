using Dapper;
using DbRelay;
using Microsoft.Data.SqlClient;

namespace SqlServer.Sample;

/// <summary>Places a few orders, each in its own business transaction that also enqueues an <see cref="OrderPlaced"/> event.</summary>
internal sealed class OrderProducer(IOutbox outbox, IConfiguration configuration, ILogger<OrderProducer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = configuration.GetConnectionString("Outbox")!;
        var count = configuration.GetValue("Producer:Orders", 3);

        for (var i = 1; i <= count && !stoppingToken.IsCancellationRequested; i++)
        {
            var messageId = await PlaceOrderAsync(connectionString, $"Customer {i}", 10m * i, stoppingToken);
            logger.LogInformation("Order {Number} committed with outbox message {MessageId}", i, messageId);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    // snippet: sqlserver-enqueue
    private async Task<Guid> PlaceOrderAsync(string connectionString, string customer, decimal total, CancellationToken ct)
    {
        var order = new { Id = Guid.NewGuid(), Customer = customer, Total = total };

        // The application owns the connection and the transaction: it opens, commits and disposes them.
        // DbRelay inserts the outbox row on this connection, inside this SqlTransaction, and never commits,
        // rolls back or disposes either of them.
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        await connection.ExecuteAsync(
            "INSERT INTO [sample].[orders] ([id], [customer], [total]) VALUES (@Id, @Customer, @Total)", order, transaction);

        var messageId = await outbox.EnqueueAsync(
            transaction,
            new OrderPlaced(order.Id, order.Customer, order.Total),
            new EnqueueOptions { CorrelationId = order.Id.ToString(), Destination = "orders" },
            ct);

        // Both rows commit together. Without this call, disposing the transaction rolls both back.
        await transaction.CommitAsync(ct);
        return messageId;
    }
    // end-snippet
}
