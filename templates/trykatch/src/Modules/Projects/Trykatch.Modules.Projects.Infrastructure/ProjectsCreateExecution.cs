using System.Text.Json;
using FluentValidation;
using Trykatch.Application.Authorization;
using Trykatch.Application.Common;
using Trykatch.Application.Organizations;
using Trykatch.Modules.Projects.Application;

namespace Trykatch.Modules.Projects.Infrastructure;

internal sealed class ProjectsCreateOperationHandler(ProjectUseCases projects) : IModuleOperationHandler
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public string ModuleId => "projects";
    public string OperationKey => "projects.create";
    public int OperationVersion => 1;

    public async Task HandleAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        if (context.ExecutionKind != ModuleExecutionKind.UserCommand
            || context.OperationKey != OperationKey
            || context.OperationVersion != OperationVersion)
            throw new InvalidOperationException("The Projects create operation context is invalid.");

        CreateProjectCommand command;
        try
        {
            command = payload.Deserialize<CreateProjectCommand>(PayloadJson)
                ?? throw new JsonException("Create-project payload is required.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The Projects create payload is invalid.");
        }

        Result<ProjectDto> result = await projects.CreateAsync(command, cancellationToken);
        if (!result.IsSuccess)
            throw new InvalidOperationException("The Projects create operation was rejected.");
    }
}

internal sealed class ProjectsResourcePolicy(
    IOrganizationContext organization,
    IPermissionAuthorizer authorizer,
    IValidator<CreateProjectCommand> validator,
    IProjectStore store) : IModuleOperationResourcePolicy
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public string ModuleId => "projects";
    public string PolicyKey => "projects";

    public async Task<bool> AuthorizeAsync(ModuleOperationContext context, JsonElement payload, CancellationToken cancellationToken)
    {
        if (context.ExecutionKind == ModuleExecutionKind.CommittedEvent && context.OperationKey == ProjectsRealtime.Operation)
        {
            if (context.WorkId == Guid.Empty || context.OperationVersion != 1
                || context.EffectiveActorId != ProjectsRealtime.AuditActor || !organization.IsResolved
                || organization.OrganizationId != context.OrganizationId || organization.ActorId != context.EffectiveActorId)
                return false;
            var source = ProjectsRealtime.ReadSource(payload, context.OrganizationId, context.OriginActorId);
            if (source is null) return false;
            var project = await store.FindAsync(context.OrganizationId, source.ProjectId, cancellationToken);
            return project is not null && project.CreatedBy == source.ActorId;
        }
        if (context.WorkId == Guid.Empty
            || context.OperationKey != "projects.create"
            || context.OperationVersion != 1
            || context.ExecutionKind != ModuleExecutionKind.UserCommand
            || context.OrganizationId == Guid.Empty
            || context.EffectiveActorId == Guid.Empty
            || context.OriginActorId != context.EffectiveActorId
            || !organization.IsResolved
            || organization.OrganizationId != context.OrganizationId
            || organization.ActorId != context.EffectiveActorId
            || organization.MembershipId == Guid.Empty)
            return false;

        CreateProjectCommand? command;
        try
        {
            if (payload.ValueKind != JsonValueKind.Object)
                return false;
            command = payload.Deserialize<CreateProjectCommand>(PayloadJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (command is null || !(await validator.ValidateAsync(command, cancellationToken)).IsValid)
            return false;

        return await authorizer.HasPermissionAsync(Permissions.ProjectsManage, cancellationToken);
    }
}
