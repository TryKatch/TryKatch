using Microsoft.EntityFrameworkCore;
using __ROOT_NAMESPACE__.Modules;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Application;
using __ROOT_NAMESPACE__.Modules.__MODULE__.Domain;

namespace __ROOT_NAMESPACE__.Modules.__MODULE__.Infrastructure;

internal sealed class __ENTITY__Store(IOrganizationModuleData data) : I__ENTITY__Store
{
    public async Task<__ENTITY__Page<__ENTITY__Record>> PageAsync(__ENTITY__PageQuery request, CancellationToken cancellationToken)
    {
        request.Validate();
        IQueryable<__ENTITY__Record> query = data.Query<__ENTITY__Record>();
        if (request.Scope == __ENTITY__QueryScope.Recoverable)
            query = query.IgnoreQueryFilters(["LifecycleVisibility"])
                .Where(record => record.ArchivedAt != null || record.DeletedAt != null);
        string search = request.Search?.Trim() ?? string.Empty;
        string pattern = "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
        if (search.Length > 0) query = query.Where(record => __PAGE_SEARCH_PREDICATE__);
        IOrderedQueryable<__ENTITY__Record> ordered = request.Sort == "oldest"
            ? query.OrderBy(record => record.CreatedAt).ThenBy(record => record.Id)
            : query.OrderByDescending(record => record.CreatedAt).ThenByDescending(record => record.Id);
        __ENTITY__Record[] records = await ordered.AsNoTracking()
            .Skip((request.Page - 1) * request.PageSize).Take(request.PageSize + 1).ToArrayAsync(cancellationToken);
        return new(records.Take(request.PageSize).ToArray(), request.Page, request.PageSize, records.Length > request.PageSize);
    }

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
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try { await data.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new __ENTITY__ConflictException(); }
    }
}
