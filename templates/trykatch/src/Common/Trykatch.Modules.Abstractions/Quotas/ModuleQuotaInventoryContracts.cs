namespace Trykatch.Modules;

/// <summary>A stable identity for one registered owner-module inventory source.</summary>
public sealed record ModuleQuotaInventorySourceRef(
    string OwnerModuleId,
    string SourceKey,
    int SourceVersion);

/// <summary>One uploaded logical-usage item discovered by a module's registered inventory source.</summary>
public sealed record ModuleQuotaInventoryEntry(Guid SourceId, Guid OperationId, long Units);

/// <summary>A bounded inventory response; the host validates paging progress and entry consistency.</summary>
public sealed record ModuleQuotaInventoryPage(
    IReadOnlyList<ModuleQuotaInventoryEntry> Entries,
    string? NextCursor,
    bool IsComplete);

/// <summary>Read-only module adapter for a declared quota inventory source.</summary>
public interface IModuleQuotaInventorySource
{
    Task<ModuleQuotaInventoryPage> ReadPageAsync(
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}

/// <summary>Associates a declared source identity with its implementation at the host composition boundary.</summary>
public sealed record ModuleQuotaInventorySourceBinding(
    ModuleQuotaInventorySourceRef Source,
    IModuleQuotaInventorySource Adapter);
