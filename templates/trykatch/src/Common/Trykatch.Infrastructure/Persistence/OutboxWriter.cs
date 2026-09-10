using System.Text.Json;
using Trykatch.Application.Outbox;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class OutboxWriter(ApplicationDbContext dbContext) : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue<T>(T message) where T : notnull =>
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxContractName.For(typeof(T)),
            Payload = JsonSerializer.Serialize(message, SerializerOptions)
        });
}

internal static class OutboxContractName
{
    private static readonly IReadOnlyDictionary<string, string> StableNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Trykatch.Modules.Projects.IntegrationEvents.ProjectChanged"] =
                "Trykatch" + "App.Application.Projects.ProjectChanged",
            ["Trykatch.Modules.Documents.IntegrationEvents.DocumentChanged"] =
                "Trykatch.Modules.Documents.DocumentChanged"
        };

    public static string For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        string currentName = messageType.FullName ?? messageType.Name;
        return StableNames.GetValueOrDefault(currentName, currentName);
    }
}
