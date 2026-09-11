using Trykatch.Application.Auditing;
using Trykatch.Application.Organizations;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class AuditIntentWriter(
    OrganizationControlPlaneDbContext dbContext,
    IOrganizationContext context,
    TimeProvider timeProvider) : IAuditIntentWriter
{
    public Guid Record(
        string operation,
        AuditTarget target,
        IReadOnlyDictionary<string, string?>? details = null)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Audit intent requires the active organization business transaction.");
        Guid eventId = Guid.CreateVersion7();
        dbContext.AuditIntents.Add(AuditIntent.Create(
            eventId,
            context.OrganizationId,
            context.ActorId,
            operation,
            target,
            details,
            timeProvider.GetUtcNow()));
        return eventId;
    }
}
