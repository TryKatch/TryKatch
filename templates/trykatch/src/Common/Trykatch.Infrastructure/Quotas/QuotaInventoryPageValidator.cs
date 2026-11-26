using Trykatch.Modules;

namespace Trykatch.Infrastructure.Quotas;

internal static class QuotaInventoryPageValidator
{
    internal static void Validate(ModuleQuotaInventoryPage page, string? previousCursor)
    {
        if (page?.Entries is null || page.Entries.Count > 500
            || page.Entries.Any(entry => entry is null || entry.SourceId == Guid.Empty
                || entry.OperationId == Guid.Empty || entry.Units <= 0)
            || (page.IsComplete ? page.NextCursor is not null
                : page.Entries.Count == 0 || string.IsNullOrEmpty(page.NextCursor) || page.NextCursor.Length > 1024
                    || string.Equals(page.NextCursor, previousCursor, StringComparison.Ordinal)))
            throw new OrganizationQuotaUnavailableException();
    }
}
