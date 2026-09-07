using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Common;
using FlatpackApp.Application.Projects;
using FlatpackApp.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[Authorize]
[OrganizationScoped]
[Route("api/v1/projects")]
public sealed class ProjectsController(ProjectUseCases projects) : ControllerBase
{
    [HttpGet(Name = "Projects_List")]
    [RequirePermission(Permissions.ProjectsRead)]
    public async Task<ActionResult<PagedResult<ProjectDto>>> List(int page = 1, int pageSize = 25, string? search = null, string lifecycle = "active", CancellationToken cancellationToken = default)
    {
        if (!RecordLifecycle.TryParseFilter(lifecycle, out RecordLifecycleFilter filter))
            return ProblemResult("validation", "Lifecycle must be active, archived, deleted, recoverable, or all.");
        Result<PagedResult<ProjectDto>> result = await projects.ListAsync(Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search, filter, cancellationToken);
        return ToActionResult(result);
    }

    [HttpGet("{id:guid}", Name = "Projects_Get")]
    [RequirePermission(Permissions.ProjectsRead)]
    public async Task<ActionResult<ProjectDto>> Get(Guid id, CancellationToken cancellationToken)
    {
        return ToActionResult(await projects.GetAsync(id, cancellationToken));
    }

    [HttpPost(Name = "Projects_Create")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.ProjectsManage)]
    public async Task<ActionResult<ProjectDto>> Create(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await projects.CreateAsync(command, cancellationToken));
    }

    [HttpPut("{id:guid}", Name = "Projects_Update")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.ProjectsManage)]
    public async Task<ActionResult<ProjectDto>> Update(Guid id, CreateProjectCommand command, CancellationToken cancellationToken)
    {
        return ToActionResult(await projects.UpdateAsync(new UpdateProjectCommand(id, command.Name, command.Description), cancellationToken));
    }

    [HttpPost("{id:guid}/archive", Name = "Projects_Archive")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.ProjectsManage)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.ArchiveAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ProblemResult(result.ErrorCode, result.ErrorMessage);
    }

    [HttpPost("{id:guid}/restore", Name = "Projects_Restore")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.ProjectsManage)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.RestoreAsync(id, cancellationToken);
        return result.IsSuccess ? NoContent() : ProblemResult(result.ErrorCode, result.ErrorMessage);
    }

    [HttpDelete("{id:guid}", Name = "Projects_Delete")]
    [CookieAntiforgery]
    [RequirePermission(Permissions.ProjectsManage)]
    public async Task<IActionResult> Delete(Guid id, DeleteRecordCommand command, CancellationToken cancellationToken)
    {
        Result<bool> result = await projects.DeleteAsync(id, command, cancellationToken);
        return result.IsSuccess ? NoContent() : ProblemResult(result.ErrorCode, result.ErrorMessage);
    }

    private ActionResult<T> ToActionResult<T>(Result<T> result) =>
        result.IsSuccess && result.Value is not null ? Ok(result.Value) : ProblemResult(result.ErrorCode, result.ErrorMessage);

    private ObjectResult ProblemResult(string? code, string? detail) =>
        Problem(statusCode: code switch { "forbidden" => 403, "not_found" => 404, "conflict" or "slug_conflict" => 409, _ => 400 }, title: code, detail: detail);
}
