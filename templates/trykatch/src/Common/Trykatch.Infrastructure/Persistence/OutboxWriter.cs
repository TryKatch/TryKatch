using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class OutboxWriter(
    ApplicationDbContext dbContext,
    ModuleCatalog catalog,
    IOrganizationContext organization) : IOutboxWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumPayloadBytes = 1_048_576;
    private const int ContextVersion = 1;

    public void Enqueue<T>(T message) where T : notnull =>
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Type = OutboxContractName.For(typeof(T)),
            Payload = JsonSerializer.Serialize(message, SerializerOptions)
        });

    public void Enqueue<T>(T message, string operationKey, int operationVersion) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(operationKey) || operationVersion <= 0)
            throw new InvalidOperationException("The outbox operation registration is invalid.");

        if (!organization.IsResolved
            || organization.OrganizationId == Guid.Empty
            || organization.ActorId == Guid.Empty
            || organization.MembershipId == Guid.Empty)
            throw new InvalidOperationException("A resolved organization membership is required to enqueue this event.");

        ModuleOperationDescriptor? operation = catalog.Descriptors
            .SelectMany(descriptor => descriptor.Operations)
            .SingleOrDefault(candidate =>
                string.Equals(candidate.OperationKey, operationKey, StringComparison.Ordinal)
                && candidate.OperationVersion == operationVersion);
        IntegrationEventDescriptor[] events = catalog.Descriptors
            .SelectMany(descriptor => descriptor.IntegrationEvents)
            .Where(descriptor => descriptor.PayloadType == typeof(T))
            .ToArray();

        if (operation is null
            || operation.ExecutionKind != ModuleExecutionKind.UserCommand
            || events.Length != 1
            || !string.Equals(events[0].OwnerModuleId, operation.ModuleId, StringComparison.Ordinal))
            throw new InvalidOperationException("The outbox operation and event must be explicitly registered to the same module.");

        foreach (string permission in operation.RequiredPermissionKeys)
        {
            if (!organization.Permissions.Contains(permission))
                throw new UnauthorizedAccessException("The current organization membership lacks a required permission.");
        }

        IntegrationEventDescriptor integrationEvent = events[0];
        int maximumPayloadBytes = Math.Min(MaximumPayloadBytes, integrationEvent.MaximumPayloadBytes);
        string payload = JsonSerializer.Serialize<T>(message, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(payload) > maximumPayloadBytes)
            throw new InvalidOperationException("The registered outbox event payload exceeds its size limit.");

        OutboxContext context = new(
            organization.OrganizationId,
            organization.ActorId,
            organization.ActorId,
            organization.MembershipId,
            nameof(ModuleExecutionKind.UserCommand),
            operation.OperationKey,
            operation.OperationVersion,
            integrationEvent.EventKey,
            integrationEvent.SchemaVersion,
            OutboxCatalogVersion.Compute(catalog),
            Activity.Current?.TraceId.ToString());
        string contextJson = JsonSerializer.Serialize(context, SerializerOptions);
        string payloadHash = OutboxPayloadHash.Compute(payload);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Type = integrationEvent.EventKey,
            Payload = payload,
            ContextVersion = ContextVersion,
            ContextJson = contextJson,
            PayloadHash = payloadHash
        });
    }
}

internal static class OutboxCatalogVersion
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static string Compute(ModuleCatalog catalog)
    {
        object[] fingerprint = catalog.Descriptors
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .Select(module => (object)new
            {
                module.Id,
                module.Version,
                Operations = module.Operations
                    .OrderBy(operation => operation.OperationKey, StringComparer.Ordinal)
                    .ThenBy(operation => operation.OperationVersion)
                    .Select(operation => new
                    {
                        operation.ModuleId,
                        operation.OperationKey,
                        operation.OperationVersion,
                        operation.ExecutionKind,
                        operation.PayloadSchemaKey,
                        operation.PayloadSchemaVersion,
                        Permissions = operation.RequiredPermissionKeys.Order(StringComparer.Ordinal),
                        operation.ResourcePolicyKey,
                        operation.SystemGrantKey,
                        operation.MaximumPayloadBytes
                    }),
                Events = module.IntegrationEvents
                    .OrderBy(integrationEvent => integrationEvent.EventKey, StringComparer.Ordinal)
                    .ThenBy(integrationEvent => integrationEvent.SchemaVersion)
                    .Select(integrationEvent => new
                    {
                        integrationEvent.EventKey,
                        integrationEvent.SchemaVersion,
                        integrationEvent.OwnerModuleId,
                        integrationEvent.MaximumPayloadBytes
                    })
            })
            .ToArray();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(fingerprint, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
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
