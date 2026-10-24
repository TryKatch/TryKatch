using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed class OrganizationEmailDeliverySource(IServiceScopeFactory scopes, OrganizationEmailDeliveryStore store,
    IEmailPayloadProtector protector, TimeProvider clock) : IEmailDeliverySource
{
    public EmailDeliveryBoundary Boundary => EmailDeliveryBoundary.Organization;
    public Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken) => store.ClaimAsync(limit, cancellationToken);
    public Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken) =>
        store.CompleteAsync(lease, outcome, cancellationToken);

    public async Task<EmailDeliveryPreparation> PrepareAsync(EmailDeliveryLease lease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        if (lease.Context is null || lease.Context.Boundary != Boundary || lease.Context.BoundaryOwnerId == Guid.Empty
            || lease.Context.DeliveryId == Guid.Empty || lease.LeaseId == Guid.Empty || lease.Attempts < 1
            || lease.LeaseUntilUtc <= clock.GetUtcNow())
            return Denied(EmailFailureCode.SourceDenied);
        OrganizationEmailDeliveryRecord? row = await store.FindAsync(lease.Context.BoundaryOwnerId, lease.Context.DeliveryId, cancellationToken);
        if (row is null || row.State != "leased" || row.LeaseId != lease.LeaseId || row.Attempts != lease.Attempts
            || row.TemplateKey != lease.Context.TemplateKey || row.TemplateVersion != lease.Context.TemplateVersion
            || row.LeaseUntilUtc <= clock.GetUtcNow() || row.LeaseUntilUtc != lease.LeaseUntilUtc
            || !string.Equals(row.ProtectedIntent, lease.ProtectedIntent, StringComparison.Ordinal))
            return Denied(EmailFailureCode.SourceDenied);
        if (row.TemplateKey == InvitationEmailIntentCodec.TemplateKey)
            return await PrepareInvitationAsync(row, cancellationToken);
        if (!OrganizationModuleEmailIntentCodec.TryRead(row, protector, out OrganizationModuleEmailIntent? intent) || intent is null)
            return Denied(EmailFailureCode.PayloadInvalid);

        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;
        OrganizationControlPlaneDbContext control = services.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = services.GetRequiredService<ApplicationDbContext>();
        await using IOrganizationDataScope transaction = await services.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(row.OrganizationId, row.ActorId, cancellationToken);
        await using var enlistment = await OrganizationTransactionEnlistment.EnlistAsync(control, application,
            row.OrganizationId, row.ActorId, cancellationToken);
        EmailDeliveryPreparation preparation = await services.GetRequiredService<OrganizationModuleEmailPolicy>()
            .PrepareAsync(row.Id, intent.Identity, intent.Producer, intent.Message, cancellationToken);
        if (preparation.Message is { } message
            && !string.Equals(message.Recipient, intent.RecipientAtAdmission, StringComparison.OrdinalIgnoreCase))
            return Denied(EmailFailureCode.RecipientChanged);
        // Read-only disclosure transaction and scope are disposed before the caller can invoke SMTP.
        return preparation;
    }

    private async Task<EmailDeliveryPreparation> PrepareInvitationAsync(OrganizationEmailDeliveryRecord row, CancellationToken cancellationToken)
    {
        if (!InvitationEmailIntentCodec.TryRead(row, protector, out InvitationEmailIntent? intent) || intent is null)
            return Denied(EmailFailureCode.PayloadInvalid);
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(row.OrganizationId, row.ActorId, cancellationToken);
        // The usable invitation carries its own pending access grant; no synthetic membership is introduced.
        return await scope.ServiceProvider.GetRequiredService<OrganizationInvitationEmailPolicy>().PrepareAsync(intent, cancellationToken);
    }

    private static EmailDeliveryPreparation Denied(EmailFailureCode failure) => new(EmailPreparationDisposition.Suppressed, FailureCode: failure);
}
