using System.Text.Json;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Realtime;

internal sealed class ModuleRealtimePublisher(
    IOrganizationContext organization,
    ModuleWorkExecutionContext execution,
    ModuleCatalog catalog,
    RealtimeAudienceRegistry audiences,
    RealtimeHistoryStore history) : IModuleRealtimePublisher
{
    public async Task PublishAsync(ModuleRealtimeNotification notification, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RealtimeAudienceRegistration audience = audiences.RequireNotification(notification);
        ModuleExecutionIdentity? identity = execution.Identity;
        ModuleOperationDescriptor? operation = execution.Operation;
        if (!organization.IsResolved || organization.OrganizationId == Guid.Empty || organization.ActorId == Guid.Empty
            || identity is null || operation is null
            || identity.ExecutionKind != ModuleExecutionKind.CommittedEvent
            || operation.ExecutionKind != ModuleExecutionKind.CommittedEvent
            || identity.OrganizationId != organization.OrganizationId || identity.EffectiveActorId != organization.ActorId
            || identity.CatalogVersion != ModuleOperationRegistry.CatalogVersion
            || identity.MembershipId is not null || identity.SourceMessageId is null || identity.SourceMessageId == Guid.Empty)
            throw InvalidAuthority();

        ModuleDescriptor? producer = catalog.Descriptors.SingleOrDefault(module => module.Id == operation.ModuleId);
        ModuleSystemGrantDescriptor? grant = producer?.SystemGrants.SingleOrDefault(candidate =>
            candidate.GrantKey == operation.SystemGrantKey);
        if (producer is null || !producer.Operations.Any(candidate => ReferenceEquals(candidate, operation))
            || grant is null || grant.OwnerModuleId != operation.ModuleId
            || identity.GrantKey != grant.GrantKey || identity.GrantVersion != grant.GrantVersion
            || identity.EffectiveActorId != grant.AuditActorId
            || !grant.AllowedOperationKeys.Contains(operation.OperationKey, StringComparer.Ordinal)
            || !grant.AllowedResourceKeys.Contains(operation.ResourcePolicyKey, StringComparer.Ordinal)
            || !grant.AllowedRealtimePolicies.Contains(notification.Audience.Policy)
            || !audience.Descriptor.AllowedSystemProducers.Contains(
                new(operation.ModuleId, operation.OperationKey, operation.OperationVersion)))
            throw InvalidAuthority();

        if (JsonSerializer.SerializeToUtf8Bytes(notification).Length > 2048)
            throw new InvalidOperationException("Realtime invalidation exceeds its payload bound.");
        await history.AppendAsync(notification, identity.SourceMessageId.Value, cancellationToken);
    }

    private static InvalidOperationException InvalidAuthority() =>
        new("Realtime publication requires current registered committed-event authority.");
}
