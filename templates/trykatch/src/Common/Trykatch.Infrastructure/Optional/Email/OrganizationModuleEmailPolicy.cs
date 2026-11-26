using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Eventing;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Current authority and source disclosure, always inside a fresh or originating organization transaction.</summary>
internal sealed class OrganizationModuleEmailPolicy(
    EmailTemplateRegistry templates,
    ModuleOperationRegistry operations,
    ModuleEventRegistry events,
    OrganizationContext organization,
    OrganizationControlPlaneDbContext control,
    IOrganizationAccessResolver accessResolver,
    IOrganizationDataPlacement placement,
    OutboxDbContext outbox)
{
    public async Task<EmailDeliveryPreparation> PrepareAsync(Guid deliveryId, ModuleExecutionIdentity identity,
        ModuleEmailProducerRef? producer, ModuleEmailMessage message, CancellationToken cancellationToken)
    {
        if (control.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Email disclosure requires an active organization transaction.");
        if (!IsIdentityShapeValid(identity, producer) || !IsMessageShapeValid(message)
            || deliveryId == Guid.Empty)
            return Denied(EmailFailureCode.PayloadInvalid);
        if (!templates.TryGetTemplate(message.Template, out EmailTemplateRegistration? template) || template is null
            || !templates.TryGetPolicy(message.DisclosurePolicy, out EmailDisclosurePolicyRegistration? policy) || policy is null)
            return Denied(EmailFailureCode.TemplateUnavailable);
        if (Encoding.UTF8.GetByteCount(message.TemplateData.GetRawText()) > template.Descriptor.MaximumTemplateDataBytes
            || !policy.Descriptor.AllowedTemplates.Contains(message.Template)
            || !policy.Descriptor.AllowedSourceResourceKeys.Contains(message.Source.ResourceKey, StringComparer.Ordinal))
            return Denied(EmailFailureCode.SourceDenied);

        Organization? currentOrganization = await control.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == identity.OrganizationId, cancellationToken);
        if (currentOrganization is null || !currentOrganization.IsActive)
            return Denied(EmailFailureCode.SourceDenied);
        OrganizationDataRoute route;
        try { route = await placement.ResolveAsync(identity.OrganizationId, cancellationToken); }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException)
        {
            return Unavailable();
        }
        if (route.OrganizationId != identity.OrganizationId || route.Placement != OrganizationDataPlacementKind.Shared
            || route.Provider != "postgres" || route.DatabaseIdentifier != control.Database.GetDbConnection().Database)
            return Denied(EmailFailureCode.SourceDenied);

        if (identity.ExecutionKind == ModuleExecutionKind.UserCommand)
        {
            OrganizationAccess? access = await accessResolver.ResolveAsync(identity.EffectiveActorId, identity.OrganizationId, cancellationToken);
            if (access is null || access.OrganizationId != identity.OrganizationId || access.ActorId != identity.EffectiveActorId
                || access.MembershipId != identity.MembershipId || policy.Descriptor.RequiredUserPermissionKeys.Count == 0
                || policy.Descriptor.RequiredUserPermissionKeys.Any(permission => !access.Permissions.Contains(permission)))
                return Denied(EmailFailureCode.SourceDenied);
            if (!organization.IsResolved) organization.Initialize(access);
        }
        else
        {
            if (producer is null || !operations.TryGet(producer.ModuleId, producer.OperationKey, producer.OperationVersion,
                    out ModuleOperationRegistration? operation) || operation is null
                || !SystemWorkGrantValidator.TryValidate(operation, identity)
                || !operation.SystemGrant!.AllowedEmailDisclosurePolicies.Contains(message.DisclosurePolicy)
                || !policy.Descriptor.AllowedSystemProducers.Contains(new(producer, message.Template)))
                return Denied(EmailFailureCode.SourceDenied);
            if (identity.ExecutionKind == ModuleExecutionKind.CommittedEvent)
            {
                OutboxMessage? source = await outbox.Messages.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == identity.SourceMessageId, cancellationToken);
                if (source is null || !OutboxContextReader.TryRead(source, out OutboxContext? context, out bool legacy)
                    || legacy || context is null || context.OrganizationId != identity.OrganizationId
                    || context.OriginActorId != identity.OriginActorId
                    || !operations.AcceptsSourceEvent(operation, context.EventKey, context.SchemaVersion)
                    || !events.TryValidateSource(context, source.Payload, out _, out _))
                    return Denied(EmailFailureCode.SourceDenied);
            }
            if (!organization.IsResolved)
                organization.InitializeSystem(new(identity.OrganizationId, currentOrganization.Slug, identity.EffectiveActorId,
                    operation.Descriptor.RequiredPermissionKeys.ToHashSet(StringComparer.Ordinal)));
        }
        if (organization.OrganizationId != identity.OrganizationId || organization.ActorId != identity.EffectiveActorId)
            return Denied(EmailFailureCode.SourceDenied);

        ModuleEmailDisclosureDecision decision = await policy.Adapter.ResolveAsync(new(deliveryId, identity, producer), message, cancellationToken);
        if (decision is null || !Enum.IsDefined(decision.State)) return Denied(EmailFailureCode.PayloadInvalid);
        if (decision.State == ModuleEmailDisclosureState.Unavailable) return Unavailable();
        if (decision.State != ModuleEmailDisclosureState.Allowed) return Denied(EmailFailureCode.SourceDenied);
        if (!OrganizationModuleEmailIntentCodec.IsRecipient(decision.Recipient)
            || !templates.TryRender(message.Template, decision.CanonicalData, out ModuleEmailContent? content) || content is null)
            return Denied(EmailFailureCode.PayloadInvalid);
        return new(EmailPreparationDisposition.Ready, new(decision.Recipient!, content.Subject, content.TextBody, content.HtmlBody));
    }

    internal static bool IsIdentityShapeValid(ModuleExecutionIdentity identity, ModuleEmailProducerRef? producer) =>
        identity is not null && identity.OrganizationId != Guid.Empty && identity.EffectiveActorId != Guid.Empty
        && identity.OriginActorId is { } origin && origin != Guid.Empty
        && identity.CatalogVersion == ModuleOperationRegistry.CatalogVersion
        && SafeText(identity.TraceCorrelation, 128, optional: true)
        && (identity.ExecutionKind == ModuleExecutionKind.UserCommand
            ? producer is null && identity.MembershipId is { } membership && membership != Guid.Empty
                && identity.OriginActorId == identity.EffectiveActorId && identity.GrantKey is null
                && identity.GrantVersion is null && identity.SourceMessageId is null
            : producer is not null && identity.MembershipId is null && SafeText(identity.GrantKey, 160)
                && identity.GrantVersion > 0
                && (identity.ExecutionKind == ModuleExecutionKind.CommittedEvent
                    ? identity.SourceMessageId is { } source && source != Guid.Empty
                    : identity.ExecutionKind == ModuleExecutionKind.ScheduledSystem && identity.SourceMessageId is null));

    internal static bool IsMessageShapeValid(ModuleEmailMessage message) => message is not null
        && message.MessageId != Guid.Empty && message.LogicalDedupId != Guid.Empty
        && message.Template is not null && message.DisclosurePolicy is not null
        && message.Source is not null && message.Recipient is not null
        && SafeText(message.Template.OwnerModuleId, 80) && SafeText(message.Template.TemplateKey, 160)
        && message.Template.TemplateVersion > 0 && SafeText(message.DisclosurePolicy.OwnerModuleId, 80)
        && SafeText(message.DisclosurePolicy.PolicyKey, 160) && message.DisclosurePolicy.PolicyVersion > 0
        && SafeText(message.Source.ResourceKey, 160) && SafeText(message.Source.ResourceId, 160)
        && SafeText(message.Recipient.RecipientKey, 160) && SafeText(message.Correlation, 128, optional: true)
        && message.TemplateData.ValueKind == JsonValueKind.Object;

    private static bool SafeText(string? value, int maximum, bool optional = false) =>
        value is null ? optional : value.Length is > 0 && value.Length <= maximum && !value.Any(char.IsControl);
    private static EmailDeliveryPreparation Denied(EmailFailureCode code) => new(EmailPreparationDisposition.Suppressed, FailureCode: code);
    private static EmailDeliveryPreparation Unavailable() => new(EmailPreparationDisposition.RetryableFailure, FailureCode: EmailFailureCode.SourceUnavailable);
}
