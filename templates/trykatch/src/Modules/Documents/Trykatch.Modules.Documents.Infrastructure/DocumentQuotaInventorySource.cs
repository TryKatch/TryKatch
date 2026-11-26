using Microsoft.EntityFrameworkCore;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Infrastructure;

internal sealed class DocumentQuotaInventorySource(IOrganizationModuleData data) : IModuleQuotaInventorySource
{
    private const int MaximumPageSize = 500;

    public async Task<ModuleQuotaInventoryPage> ReadPageAsync(
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"Page size must be between 1 and {MaximumPageSize}.");

        Guid? afterId = ParseCursor(cursor);
        IQueryable<DocumentRecord> query = data.Query<DocumentRecord>()
            .IgnoreQueryFilters(["LifecycleVisibility"])
            .AsNoTracking()
            .Where(document => document.OrganizationId == data.OrganizationId && document.ObjectKey != null);
        if (afterId is { } id)
            query = query.Where(document => document.Id.CompareTo(id) > 0);

        var documents = await query
            .OrderBy(document => document.Id)
            .Take(pageSize + 1)
            .Select(document => new { document.Id, document.UploadOperationId, document.SizeBytes })
            .ToArrayAsync(cancellationToken);

        bool hasMore = documents.Length > pageSize;
        var currentPage = documents.Take(pageSize).ToArray();
        if (documents.Any(document => document.SizeBytes is null or <= 0))
            throw new OrganizationQuotaUnavailableException();

        ModuleQuotaInventoryEntry[] entries = currentPage
            .Select(document => new ModuleQuotaInventoryEntry(
                document.Id,
                document.UploadOperationId ?? document.Id,
                document.SizeBytes!.Value))
            .ToArray();
        string? nextCursor = hasMore ? currentPage[^1].Id.ToString("N") : null;
        return new(entries, nextCursor, IsComplete: !hasMore);
    }

    private static Guid? ParseCursor(string? cursor)
    {
        if (cursor is null) return null;
        if (!Guid.TryParseExact(cursor, "N", out Guid id)
            || id == Guid.Empty
            || !string.Equals(cursor, id.ToString("N"), StringComparison.Ordinal))
            throw new ArgumentException("Cursor must be a canonical, nonempty Guid in N format.", nameof(cursor));
        return id;
    }
}
