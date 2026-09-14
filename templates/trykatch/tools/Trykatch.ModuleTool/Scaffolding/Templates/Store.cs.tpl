using Microsoft.EntityFrameworkCore;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Infrastructure;

internal sealed class __ENTITY__Store(IOrganizationModuleData data) : I__ENTITY__Store
{
    public async Task<IReadOnlyList<__ENTITY__Record>> ListAsync(__ENTITY__QueryScope scope, CancellationToken cancellationToken)
    {
        IQueryable<__ENTITY__Record> query = data.Query<__ENTITY__Record>();
        if (scope == __ENTITY__QueryScope.Recoverable)
            query = query.IgnoreQueryFilters(["LifecycleVisibility"])
                .Where(record => record.ArchivedAt != null || record.DeletedAt != null);
        return await query.AsNoTracking().OrderByDescending(record => record.CreatedAt).ToArrayAsync(cancellationToken);
    }

    public Task<__ENTITY__Record?> FindAsync(Guid id, bool includeRecoverable, CancellationToken cancellationToken)
    {
        IQueryable<__ENTITY__Record> query = data.Query<__ENTITY__Record>();
        if (includeRecoverable) query = query.IgnoreQueryFilters(["LifecycleVisibility"]);
        return query.SingleOrDefaultAsync(record => record.Id == id, cancellationToken);
    }

    public void Add(__ENTITY__Record record) => data.Add(record);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => data.SaveChangesAsync(cancellationToken);
}
