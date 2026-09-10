using Trykatch.Application.Auditing;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Persistence;

internal sealed class OrganizationModuleData(
    ApplicationDbContext dbContext,
    IOrganizationContext organization,
    IAuditWriter audit,
    IOutboxWriter outbox) : IOrganizationModuleData
{
    public Guid OrganizationId => organization.OrganizationId;
    public Guid ActorId => organization.ActorId;
    public IQueryable<TEntity> Query<TEntity>() where TEntity : class => dbContext.Set<TEntity>();
    public void Add<TEntity>(TEntity entity) where TEntity : class => dbContext.Set<TEntity>().Add(entity);
    public void Remove<TEntity>(TEntity entity) where TEntity : class => dbContext.Set<TEntity>().Remove(entity);
    public void RecordAudit(string action, string subjectType, string subjectId, string displayName,
        IReadOnlyDictionary<string, string?>? details = null) =>
        audit.Record(action, new(subjectType, subjectId, displayName), details);
    public void Enqueue<TMessage>(TMessage message) where TMessage : notnull => outbox.Enqueue(message);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}
