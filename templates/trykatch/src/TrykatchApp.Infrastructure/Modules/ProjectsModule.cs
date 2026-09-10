using TrykatchApp.Application.Authorization;
using TrykatchApp.Application.Projects;
using TrykatchApp.Application.Overview;
using TrykatchApp.Infrastructure.Projects;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Modules;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TrykatchApp.Infrastructure.Modules;

/// <summary>
/// Reference full-stack module. Its public interface is the module descriptor;
/// application and persistence details remain behind the registration seam.
/// </summary>
public sealed class ProjectsModule : ITrykatchModule
{
    public TrykatchModuleDescriptor Descriptor { get; } = new(
        Id: "projects",
        Name: "Projects",
        Version: "1.0.0",
        Description: "Organization-scoped project management and lifecycle reference feature.",
        Requires: [],
        OptionalDependencies: [],
        Capabilities: TrykatchModuleCapabilities.Api
            | TrykatchModuleCapabilities.Web
            | TrykatchModuleCapabilities.Data
            | TrykatchModuleCapabilities.BackgroundWork
            | TrykatchModuleCapabilities.Assistant,
        ExtensionPoints:
        [
            new(
                "projects.list.after-table",
                "Renders module-owned workspace content after the projects table.",
                TrykatchExtensionPointKind.UiSlot,
                TrykatchModuleCapabilities.Web)
        ])
    {
        DefaultDataOwnership = TrykatchDataOwnership.Organization,
        DataResources =
        [
            new(
                "projects",
                "app",
                "projects",
                TrykatchDataOwnership.Organization,
                typeof(global::TrykatchApp.Domain.Projects.Project).FullName,
                "projects_organization_isolation")
        ],
        Permissions =
        [
            new(
                Permissions.ProjectsRead,
                "View projects",
                "View projects and their details.",
                Order: 10,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member, DefaultOrganizationRoles.Viewer]),
            new(
                Permissions.ProjectsManage,
                "Manage projects",
                "Create, change, archive, restore, and request reasoned deletion of projects.",
                IsSensitive: true,
                Order: 20,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member])
        ],
        AssistantTools =
        [
            new(
                "trykatch_list_projects",
                "Projects_List",
                "List the projects visible in the current organization workspace.",
                TrykatchAssistantToolRisk.ReadOnly,
                RequiresHumanConfirmation: false),
            new(
                "trykatch_get_project",
                "Projects_Get",
                "Get one project by its identifier in the current organization workspace.",
                TrykatchAssistantToolRisk.ReadOnly,
                RequiresHumanConfirmation: false)
        ]
    };

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddSingleton<IApplicationModelContributor, ProjectsModelContributor>();
        services.AddScoped<IWorkspaceOverviewMetricProvider, ProjectsOverviewMetricProvider>();
        services.AddSingleton<IValidator<CreateProjectCommand>, CreateProjectValidator>();
    }
}
