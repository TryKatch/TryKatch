using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Infrastructure;

internal sealed class DocumentStore(IOrganizationModuleData data) : IDocumentStore
{
    public async Task<IReadOnlyList<DocumentRecord>> ListAsync(
        DocumentQueryScope scope,
        CancellationToken cancellationToken)
    {
        IQueryable<DocumentRecord> query = data.Query<DocumentRecord>();
        if (scope == DocumentQueryScope.Recoverable)
        {
            query = query.IgnoreQueryFilters(["LifecycleVisibility"])
                .Where(document => document.ArchivedAt != null || document.DeletedAt != null);
        }

        return await query.AsNoTracking()
            .OrderByDescending(document => document.CreatedAt)
            .ToArrayAsync(cancellationToken);
    }

    public Task<DocumentRecord?> FindAsync(
        Guid id,
        bool includeRecoverable,
        CancellationToken cancellationToken)
    {
        IQueryable<DocumentRecord> query = data.Query<DocumentRecord>();
        if (includeRecoverable)
            query = query.IgnoreQueryFilters(["LifecycleVisibility"]);
        return query.SingleOrDefaultAsync(document => document.Id == id, cancellationToken);
    }

    public void Add(DocumentRecord document) => data.Add(document);

    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        data.SaveChangesAsync(cancellationToken);
}
