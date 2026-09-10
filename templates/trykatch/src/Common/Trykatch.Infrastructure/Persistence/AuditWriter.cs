using Trykatch.Application.Organizations;
using System.Text.Json;
using Trykatch.Application.Auditing;
using Trykatch.Domain.Organizations;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class AuditWriter(ApplicationDbContext dbContext, IOrganizationContext context) : IAuditWriter
{
    public void Record(string action, AuditTarget target, IReadOnlyDictionary<string, string?>? details = null) =>
        dbContext.AuditEntries.Add(new AuditEntry(
            context.OrganizationId,
            context.ActorId,
            action,
            target.Type,
            target.Id,
            target.DisplayName,
            JsonSerializer.Serialize(details ?? new Dictionary<string, string?>())));

    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
