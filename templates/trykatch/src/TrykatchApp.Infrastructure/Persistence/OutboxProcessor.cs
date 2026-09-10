using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace TrykatchApp.Infrastructure.Persistence;

internal sealed class OutboxProcessor(IServiceScopeFactory scopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProcessBatchAsync(stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        OutboxDbContext dbContext = scope.ServiceProvider.GetRequiredService<OutboxDbContext>();
        OutboxDelivery delivery = scope.ServiceProvider.GetRequiredService<OutboxDelivery>();
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            OutboxMessage[] messages = await dbContext.Messages
                .FromSqlRaw("""
                    SELECT * FROM platform.outbox_messages
                    WHERE "ProcessedAt" IS NULL AND "Attempts" < 10
                    ORDER BY "OccurredAt"
                    FOR UPDATE SKIP LOCKED
                    LIMIT 50
                    """)
                .ToArrayAsync(cancellationToken);

            foreach (OutboxMessage message in messages)
            {
                await delivery.DeliverAsync(message, cancellationToken);
            }

            if (messages.Length > 0)
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

}
