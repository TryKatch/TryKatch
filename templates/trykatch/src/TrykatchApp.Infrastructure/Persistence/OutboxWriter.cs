using System.Text.Json;
using TrykatchApp.Application.Outbox;

namespace TrykatchApp.Infrastructure.Persistence;

internal sealed class OutboxWriter(ApplicationDbContext dbContext) : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue<T>(T message) where T : notnull =>
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Type = typeof(T).FullName ?? typeof(T).Name,
            Payload = JsonSerializer.Serialize(message, SerializerOptions)
        });
}
