using TrykatchApp.Application.Authorization;
using TrykatchApp.Application.Auditing;
using TrykatchApp.Application.Common;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Application.Outbox;
using TrykatchApp.Domain.Projects;
using FluentValidation;

namespace TrykatchApp.Application.Projects;

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    RecordLifecycleDto Lifecycle);

public sealed record CreateProjectCommand(string Name, string? Description);
public sealed record UpdateProjectCommand(Guid Id, string Name, string? Description);

public sealed class CreateProjectValidator : AbstractValidator<CreateProjectCommand>
{
    public CreateProjectValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Description).MaximumLength(2000);
    }
}

public sealed class ProjectUseCases(
    IProjectStore store,
    IOrganizationContext context,
    IPermissionAuthorizer authorizer,
    IAuditWriter auditWriter,
    IOutboxWriter outbox,
    IValidator<CreateProjectCommand> createValidator)
{
    public async Task<Result<PagedResult<ProjectDto>>> ListAsync(int page, int pageSize, string? search, RecordLifecycleFilter lifecycle, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsRead, cancellationToken))
        {
            return Result.Failure<PagedResult<ProjectDto>>("forbidden", "Projects cannot be viewed by this membership.");
        }

        PagedResult<Project> projects = await store.ListAsync(context.OrganizationId, page, pageSize, search, lifecycle, cancellationToken);
        return Result.Success(new PagedResult<ProjectDto>(
            projects.Items.Select(ToDto).ToArray(),
            projects.Page,
            projects.PageSize,
            projects.TotalCount));
    }

    public async Task<Result<ProjectDto>> GetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsRead, cancellationToken))
            return Result.Failure<ProjectDto>("forbidden", "Projects cannot be viewed by this membership.");
        Project? project = await store.FindAsync(context.OrganizationId, projectId, cancellationToken);
        return project is null
            ? Result.Failure<ProjectDto>("not_found", "Project was not found.")
            : Result.Success(ToDto(project));
    }

    public async Task<Result<ProjectDto>> CreateAsync(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken))
        {
            return Result.Failure<ProjectDto>("forbidden", "Projects cannot be changed by this membership.");
        }

        var validation = await createValidator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
        {
            return Result.Failure<ProjectDto>("validation", validation.Errors[0].ErrorMessage);
        }

        Project project = Project.Create(context.OrganizationId, command.Name, command.Description, context.ActorId);
        await store.AddAsync(project, cancellationToken);
        auditWriter.Record(AuditActions.ProjectCreated, new AuditTarget("Project", project.Id.ToString(), project.Name));
        outbox.Enqueue(new ProjectChanged(project.OrganizationId, project.Id, "created", context.ActorId));
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(project));
    }

    public async Task<Result<ProjectDto>> UpdateAsync(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken))
        {
            return Result.Failure<ProjectDto>("forbidden", "Projects cannot be changed by this membership.");
        }

        Project? project = await store.FindAsync(context.OrganizationId, command.Id, cancellationToken);
        if (project is null)
        {
            return Result.Failure<ProjectDto>("not_found", "Project was not found.");
        }

        if (project.LifecycleState != TrykatchApp.Domain.Common.RecordLifecycleState.Active)
            return Result.Failure<ProjectDto>("conflict", "Restore the project before editing it.");

        project.Update(command.Name, command.Description);
        auditWriter.Record(AuditActions.ProjectUpdated, new AuditTarget("Project", project.Id.ToString(), project.Name));
        outbox.Enqueue(new ProjectChanged(project.OrganizationId, project.Id, "updated", context.ActorId));
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(ToDto(project));
    }

    public async Task<Result<bool>> ArchiveAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken))
        {
            return Result.Failure<bool>("forbidden", "Projects cannot be changed by this membership.");
        }

        Project? project = await store.FindAsync(context.OrganizationId, projectId, cancellationToken);
        if (project is null)
        {
            return Result.Failure<bool>("not_found", "Project was not found.");
        }

        bool changed = project.Archive(context.ActorId, DateTimeOffset.UtcNow);
        if (!changed) return Result.Success(true);
        auditWriter.Record(AuditActions.ProjectArchived, new AuditTarget("Project", project.Id.ToString(), project.Name));
        outbox.Enqueue(new ProjectChanged(project.OrganizationId, project.Id, "archived", context.ActorId));
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    public async Task<Result<bool>> RestoreAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken))
            return Result.Failure<bool>("forbidden", "Projects cannot be changed by this membership.");
        Project? project = await store.FindAsync(context.OrganizationId, projectId, cancellationToken);
        if (project is null) return Result.Failure<bool>("not_found", "Project was not found.");
        if (!project.Restore()) return Result.Success(true);
        auditWriter.Record(AuditActions.ProjectRestored, new AuditTarget("Project", project.Id.ToString(), project.Name));
        outbox.Enqueue(new ProjectChanged(project.OrganizationId, project.Id, "restored", context.ActorId));
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    public async Task<Result<bool>> DeleteAsync(Guid projectId, DeleteRecordCommand command, CancellationToken cancellationToken)
    {
        if (!await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken))
            return Result.Failure<bool>("forbidden", "Projects cannot be changed by this membership.");
        Project? project = await store.FindAsync(context.OrganizationId, projectId, cancellationToken);
        if (project is null) return Result.Failure<bool>("not_found", "Project was not found.");
        if (project.LifecycleState != TrykatchApp.Domain.Common.RecordLifecycleState.Archived)
            return Result.Failure<bool>("conflict", "Archive the project before requesting deletion.");
        string? validationError = RecordLifecycle.ValidateDeletionReason(command.Reason);
        if (validationError is not null) return Result.Failure<bool>("validation", validationError);
        if (!project.Delete(context.ActorId, command.Reason, DateTimeOffset.UtcNow)) return Result.Success(true);
        auditWriter.Record(
            AuditActions.ProjectDeleted,
            new AuditTarget("Project", project.Id.ToString(), project.Name),
            new Dictionary<string, string?> { ["reason"] = project.DeletionReason });
        outbox.Enqueue(new ProjectChanged(project.OrganizationId, project.Id, "deleted", context.ActorId));
        await store.SaveChangesAsync(cancellationToken);
        return Result.Success(true);
    }

    private static ProjectDto ToDto(Project project) =>
        new(project.Id, project.Name, project.Description, project.CreatedAt, project.UpdatedAt, RecordLifecycle.ToDto(project));
}

public sealed record ProjectChanged(Guid OrganizationId, Guid ProjectId, string Change, Guid ActorId);
