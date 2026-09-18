using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trykatch.Modules.Documents.Domain;

namespace Trykatch.Modules.Documents.Infrastructure;

internal sealed class ListDocumentsAssistantTool(IOrganizationModuleData data, IModulePermissionAuthorizer authorizer) : IReadOnlyAssistantTool
{
    public string OperationId => "Documents_List";
    public string RequiredPermission => "documents.read";
    public JsonElement Parameters { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"page":{"type":"integer","minimum":1,"maximum":1000},"search":{"type":["string","null"],"maxLength":200}},"required":["page","search"],"additionalProperties":false}
        """);

    public async Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(RequiredPermission, cancellationToken))
            throw new UnauthorizedAccessException();
        int page = arguments.GetProperty("page").GetInt32();
        string? search = arguments.GetProperty("search").GetString();
        var items = await data.Query<DocumentRecord>().AsNoTracking().Where(document => search == null || document.Title.Contains(search))
            .OrderByDescending(document => document.CreatedAt).ThenBy(document => document.Id)
            .Skip((page - 1) * 20).Take(21)
            .Select(document => new { document.Id, document.Title, document.DocumentType })
            .ToArrayAsync(cancellationToken);
        // Metadata only: never ship object keys, download streams, checksums or whole files to the provider.
        return JsonSerializer.SerializeToElement(new { page, pageSize = 20, hasMore = items.Length > 20, metadataOnly = true, items = items.Take(20) });
    }
}
