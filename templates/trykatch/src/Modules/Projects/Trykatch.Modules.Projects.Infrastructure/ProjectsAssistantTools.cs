using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Trykatch.Modules.Projects.Domain;

namespace Trykatch.Modules.Projects.Infrastructure;

internal sealed class ListProjectsAssistantTool(IOrganizationModuleData data, IModulePermissionAuthorizer authorizer) : IReadOnlyAssistantTool
{
    public string OperationId => "Projects_List";
    public string RequiredPermission => "projects.read";
    public JsonElement Parameters { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"page":{"type":"integer","minimum":1,"maximum":1000},"search":{"type":["string","null"],"maxLength":200}},"required":["page","search"],"additionalProperties":false}
        """);

    public async Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(RequiredPermission, cancellationToken))
            throw new UnauthorizedAccessException();
        int page = arguments.GetProperty("page").GetInt32();
        string? search = arguments.GetProperty("search").GetString();
        var query = data.Query<Project>().AsNoTracking().Where(project => search == null || project.Name.Contains(search));
        var items = await query.OrderByDescending(project => project.CreatedAt).ThenBy(project => project.Id)
            .Skip((page - 1) * 20).Take(21)
            .Select(project => new { project.Id, project.Name }).ToArrayAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(new { page, pageSize = 20, hasMore = items.Length > 20, items = items.Take(20) });
    }
}

internal sealed class GetProjectAssistantTool(IOrganizationModuleData data, IModulePermissionAuthorizer authorizer) : IReadOnlyAssistantTool
{
    public string OperationId => "Projects_Get";
    public string RequiredPermission => "projects.read";
    public JsonElement Parameters { get; } = JsonSerializer.Deserialize<JsonElement>("""
        {"type":"object","properties":{"id":{"type":"string","format":"uuid","maxLength":36}},"required":["id"],"additionalProperties":false}
        """);

    public async Task<JsonElement> ExecuteAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(RequiredPermission, cancellationToken))
            throw new UnauthorizedAccessException();
        Guid id = arguments.GetProperty("id").GetGuid();
        var project = await data.Query<Project>().AsNoTracking().Where(project => project.Id == id)
            .Select(project => new { project.Id, project.Name, description = project.Description.Substring(0, Math.Min(project.Description.Length, 400)) })
            .SingleOrDefaultAsync(cancellationToken);
        return JsonSerializer.SerializeToElement(new { found = project is not null, item = project });
    }
}
