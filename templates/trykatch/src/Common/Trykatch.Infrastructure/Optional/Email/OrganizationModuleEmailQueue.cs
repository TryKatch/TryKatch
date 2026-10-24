using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

/// <summary>Appends approved immutable intent to the originating business transaction. It never sends or commits.</summary>
internal sealed class OrganizationModuleEmailQueue(
    OrganizationContext organization,
    OrganizationControlPlaneDbContext control,
    ApplicationDbContext application,
    ModuleWorkExecutionContext execution,
    OrganizationModuleEmailPolicy policy,
    OrganizationEmailDeliveryStore store,
    IEmailPayloadProtector protector,
    TimeProvider clock) : IModuleEmailQueue
{
    private DbTransaction? pendingTransaction;
    private readonly Dictionary<string, (OrganizationModuleEmailIntent Intent, DateTimeOffset Created)> pending = new(StringComparer.Ordinal);

    public async Task<ModuleWorkReceipt> EnqueueAsync(ModuleEmailMessage message, CancellationToken cancellationToken = default)
    {
        if (!organization.IsResolved || !OrganizationModuleEmailPolicy.IsMessageShapeValid(message))
            return Rejected("invalid_request");
        ModuleExecutionIdentity identity;
        ModuleEmailProducerRef? producer = null;
        if (execution.Identity is { ExecutionKind: not ModuleExecutionKind.UserCommand } system && execution.Operation is { } operation)
        {
            identity = system;
            producer = new(operation.ModuleId, operation.OperationKey, operation.OperationVersion);
        }
        else
        {
            Guid membership;
            try { membership = organization.MembershipId; }
            catch (OrganizationMembershipRequiredException) { return Rejected("not_authorized"); }
            identity = new(organization.OrganizationId, organization.ActorId, organization.ActorId, membership,
                ModuleExecutionKind.UserCommand, null, null, null, ModuleOperationRegistry.CatalogVersion, message.Correlation);
        }
        if (!OrganizationModuleEmailPolicy.IsIdentityShapeValid(identity, producer)
            || identity.OrganizationId != organization.OrganizationId || identity.EffectiveActorId != organization.ActorId)
            return Rejected("not_authorized");

        IDbContextTransaction transaction = control.Database.CurrentTransaction
            ?? throw new InvalidOperationException("Email submission requires an active organization transaction.");
        if (control.Database.CreateExecutionStrategy().RetriesOnFailure || application.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Email submission cannot use automatic transaction retries.");
        DbTransaction dbTransaction = transaction.GetDbTransaction();
        if (!ReferenceEquals(pendingTransaction, dbTransaction))
        {
            pending.Clear();
            pendingTransaction = dbTransaction;
        }
        if (application.Database.CurrentTransaction is null)
            _ = await OrganizationTransactionEnlistment.EnlistAsync(control, application, identity.OrganizationId, identity.EffectiveActorId, cancellationToken);
        else if (!ReferenceEquals(application.Database.CurrentTransaction.GetDbTransaction(), dbTransaction))
            throw new InvalidOperationException("Email and business data must share the organization transaction.");

        string admission = OrganizationModuleEmailIntentCodec.AdmissionKey(identity, producer);
        string storageKey = OrganizationModuleEmailIntentCodec.StorageKey(message.Template);
        string requestKey = string.Join('/', identity.OrganizationId.ToString("N"), admission, storageKey,
            message.Template.TemplateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), message.LogicalDedupId.ToString("N"));
        await control.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({requestKey}, 20261024))", cancellationToken);
        Guid deliveryId = Guid.CreateVersion7();
        EmailDeliveryPreparation current = await policy.PrepareAsync(deliveryId, identity, producer, message, cancellationToken);
        if (current.Disposition != EmailPreparationDisposition.Ready || current.Message is null)
            return current.Disposition == EmailPreparationDisposition.RetryableFailure
                ? new(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "source_unavailable") : Rejected("not_authorized");
        OrganizationModuleEmailIntent intent = new(1, deliveryId, identity, producer,
            message with { TemplateData = message.TemplateData.Clone() }, current.Message.Recipient);
        if (pending.TryGetValue(requestKey, out var submitted))
            return OrganizationModuleEmailIntentCodec.SameIntent(submitted.Intent, intent)
                ? new(submitted.Intent.DeliveryId, ModuleWorkState.AlreadyAccepted, submitted.Created) : Rejected("request_id_conflict");

        OrganizationEmailDeliveryRecord? existing = await store.FindByAdmissionAsync(identity.OrganizationId, admission,
            storageKey, message.Template.TemplateVersion, message.LogicalDedupId, cancellationToken);
        if (existing is not null)
            return OrganizationModuleEmailIntentCodec.TryRead(existing, protector, out OrganizationModuleEmailIntent? accepted)
                && OrganizationModuleEmailIntentCodec.SameIntent(accepted!, intent)
                ? new(existing.Id, ModuleWorkState.AlreadyAccepted, existing.CreatedAtUtc) : Rejected("request_id_conflict");

        string protectedIntent;
        try { protectedIntent = OrganizationModuleEmailIntentCodec.Protect(intent, protector); }
        catch (Exception failure) when (OrganizationModuleEmailIntentCodec.IsPayloadFailure(failure))
        {
            return new(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "protection_unavailable");
        }
        if (protectedIntent.Length > 262_144)
            return Rejected("invalid_payload");
        DateTimeOffset created = clock.GetUtcNow().ToUniversalTime();
        await application.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO infrastructure.email_deliveries
                ("Id", "OrganizationId", "ActorId", "MessageId", "AdmissionIdentityKey", "TemplateKey", "TemplateVersion",
                 "ProtectedIntent", "State", "Attempts", "CreatedAtUtc")
            VALUES ({deliveryId}, {identity.OrganizationId}, {identity.EffectiveActorId}, {message.LogicalDedupId}, {admission},
                {storageKey}, {message.Template.TemplateVersion}, {protectedIntent}, 'queued', 0, {created})
            """, cancellationToken);
        pending.Add(requestKey, (intent, created));
        return new(deliveryId, ModuleWorkState.Accepted, created);
    }

    private static ModuleWorkReceipt Rejected(string code) => new(Guid.Empty, ModuleWorkState.Rejected, FailureCode: code);
}
