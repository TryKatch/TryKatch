using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trykatch.Modules;

/// <summary>An explicitly named event schema with its compile-time payload reader.</summary>
public sealed record IntegrationEventDescriptor(
    string EventKey,
    int SchemaVersion,
    string OwnerModuleId,
    [property: JsonIgnore] Type PayloadType,
    [property: JsonIgnore] Func<JsonElement, object> ReadPayload,
    int MaximumPayloadBytes);

/// <summary>One accepted version of an explicitly registered integration event.</summary>
public sealed record ModuleEventVersion(string EventKey, int SchemaVersion);

/// <summary>A stable installed consumer mapped to its operation and system grant.</summary>
public sealed record ModuleEventConsumerDescriptor(
    string ConsumerKey,
    string OwnerModuleId,
    IReadOnlyList<ModuleEventVersion> AcceptedEvents,
    string OperationKey,
    int OperationVersion,
    string SystemGrantKey);

/// <summary>Validated host provenance supplied to one event consumer invocation.</summary>
public sealed record ModuleEventContext(
    Guid MessageId,
    Guid OrganizationId,
    Guid EffectiveActorId,
    Guid? OriginActorId,
    string EventKey,
    int SchemaVersion,
    DateTimeOffset OccurredAtUtc,
    string? TraceCorrelation);

/// <summary>Implemented by an explicitly registered module event consumer.</summary>
public interface IModuleEventConsumer
{
    Task HandleAsync(ModuleEventContext context, JsonElement payload, CancellationToken cancellationToken);
}
