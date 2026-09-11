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
    private const string ProjectsEventSuffix = ".Modules.Projects.IntegrationEvents.ProjectChanged";
    private const string LegacyProjectsEventSuffix = ".Application.Projects.ProjectChanged";
    private const string DocumentsEventSuffix = ".Modules.Documents.IntegrationEvents.DocumentChanged";
    private const string CurrentApplicationRoot = "Trykatch";
    private const string LegacyProjectsApplicationRoot = "TrykatchApp";

    public static string For(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        return For(messageType.FullName ?? messageType.Name);
    }

    internal static string For(string messageTypeName)
    {
        if (messageTypeName.EndsWith(DocumentsEventSuffix, StringComparison.Ordinal))
            return "Try" + "katch.Modules.Documents.DocumentChanged";

        if (!messageTypeName.EndsWith(ProjectsEventSuffix, StringComparison.Ordinal))
            return messageTypeName;

        string applicationRoot = messageTypeName[..^ProjectsEventSuffix.Length];
        string legacyRoot = string.Equals(applicationRoot, CurrentApplicationRoot, StringComparison.Ordinal)
            ? LegacyProjectsApplicationRoot
            : applicationRoot;
        return legacyRoot + LegacyProjectsEventSuffix;
    }
}
