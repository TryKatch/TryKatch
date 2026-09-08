using System.Text.Json;
using FlatpackApp.Application.Outbox;

namespace FlatpackApp.Infrastructure.Persistence;

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
