using FlatpackApp.Application.Common;
using FlatpackApp.Application.Auditing;
using FlatpackApp.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace FlatpackApp.Infrastructure.Persistence;

internal sealed class AuditReader(ApplicationDbContext dbContext) : IAuditReader
{
    public async Task<AuditReadPage> ListAsync(Guid organizationId, AuditQuery request, CancellationToken cancellationToken)
    {
        IQueryable<AuditEntry> organizationEntries = dbContext.AuditEntries.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId);

        string[] actions = await organizationEntries.Select(x => x.Action).Distinct().OrderBy(x => x).ToArrayAsync(cancellationToken);
        string[] subjectTypes = await organizationEntries.Select(x => x.SubjectType).Distinct().OrderBy(x => x).ToArrayAsync(cancellationToken);
        Guid[] actorIds = await organizationEntries.Select(x => x.ActorId).Distinct().ToArrayAsync(cancellationToken);

        IQueryable<AuditEntry> query = organizationEntries;
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string pattern = $"%{request.Search.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.Action, pattern)
                || EF.Functions.ILike(x.SubjectType, pattern)
                || EF.Functions.ILike(x.SubjectDisplayName, pattern)
                || EF.Functions.ILike(x.SubjectId, pattern));
        }
        if (!string.IsNullOrWhiteSpace(request.Action)) query = query.Where(x => x.Action == request.Action);
        if (!string.IsNullOrWhiteSpace(request.SubjectType)) query = query.Where(x => x.SubjectType == request.SubjectType);
        if (request.ActorId is Guid actorId) query = query.Where(x => x.ActorId == actorId);
        if (request.From is DateTimeOffset from) query = query.Where(x => x.OccurredAt >= from);
        if (request.To is DateTimeOffset to) query = query.Where(x => x.OccurredAt <= to);

        long total = await query.LongCountAsync(cancellationToken);
        bool ascending = string.Equals(request.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        query = request.SortBy.ToLowerInvariant() switch
        {
            "action" => ascending ? query.OrderBy(x => x.Action).ThenBy(x => x.OccurredAt) : query.OrderByDescending(x => x.Action).ThenByDescending(x => x.OccurredAt),
            "subject" => ascending ? query.OrderBy(x => x.SubjectDisplayName).ThenBy(x => x.OccurredAt) : query.OrderByDescending(x => x.SubjectDisplayName).ThenByDescending(x => x.OccurredAt),
            "actor" => ascending ? query.OrderBy(x => x.ActorId).ThenBy(x => x.OccurredAt) : query.OrderByDescending(x => x.ActorId).ThenByDescending(x => x.OccurredAt),
            _ => ascending ? query.OrderBy(x => x.OccurredAt) : query.OrderByDescending(x => x.OccurredAt)
        };
        AuditEntry[] items = await query.Skip((request.Page - 1) * request.PageSize).Take(request.PageSize).ToArrayAsync(cancellationToken);
        return new AuditReadPage(new PagedResult<AuditEntry>(items, request.Page, request.PageSize, total), actions, subjectTypes, actorIds);
    }
}
