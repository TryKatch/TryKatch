using Trykatch.Application.Authorization;
using Trykatch.Modules.Projects.Application;
using Trykatch.Application.Overview;
using Trykatch.Modules.Projects.Infrastructure;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Projects.Presentation;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Trykatch.Modules.Projects.Infrastructure;

/// <summary>
/// Reference full-stack module. Its public interface is the module descriptor;
/// application and persistence details remain behind the registration seam.
/// </summary>
public sealed class ProjectsModule : IModule, IModuleMigrationContributor
{
    public ModuleDescriptor Descriptor { get; } = new(
        Id: "projects",
        Name: "Projects",
        Version: "1.0.0",
        Description: "Organization-scoped project management and lifecycle reference feature.",
        Requires: [],
        OptionalDependencies: [],
        Capabilities: ModuleCapabilities.Api
            | ModuleCapabilities.Web
            | ModuleCapabilities.Data
            | ModuleCapabilities.BackgroundWork
            | ModuleCapabilities.Assistant,
        ExtensionPoints:
        [
            new(
                "projects.list.after-table",
                "Renders module-owned workspace content after the projects table.",
                ExtensionPointKind.UiSlot,
                ModuleCapabilities.Web)
        ])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources =
        [
            new(
                "projects",
                "app",
                "projects",
                ModuleDataOwnership.Organization,
                typeof(global::Trykatch.Modules.Projects.Domain.Project).FullName,
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
                "try" + "katch_list_projects",
                "Projects_List",
                "List the projects visible in the current organization workspace.",
                AssistantToolRisk.ReadOnly,
                RequiresHumanConfirmation: false),
            new(
                "try" + "katch_get_project",
                "Projects_Get",
                "Get one project by its identifier in the current organization workspace.",
                AssistantToolRisk.ReadOnly,
                RequiresHumanConfirmation: false)
        ]
    };

    /// <summary>
    /// Establishes the module-owned migration ledger without changing the existing
    /// EF migration identity. All statements are idempotent because existing
    /// installations already own this schema through the historical host migration.
    /// </summary>
    public IReadOnlyList<ModuleMigration> Migrations { get; } =
    [
        new("202609101500_projects_baseline", """
            CREATE TABLE IF NOT EXISTS app.projects
            (
                "Id" uuid PRIMARY KEY,
                "OrganizationId" uuid NOT NULL,
                "Name" character varying(120) NOT NULL,
                "Description" character varying(2000) NOT NULL,
                "CreatedBy" uuid NOT NULL,
                "CreatedAt" timestamp with time zone NOT NULL,
                "UpdatedAt" timestamp with time zone NULL,
                "ArchivedAt" timestamp with time zone NULL,
                "ArchivedBy" uuid NULL,
                "DeletedAt" timestamp with time zone NULL,
                "DeletedBy" uuid NULL,
                "DeletionReason" character varying(500) NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_projects_OrganizationId_Name"
                ON app.projects ("OrganizationId", "Name");
            ALTER TABLE app.projects ENABLE ROW LEVEL SECURITY;
            ALTER TABLE app.projects FORCE ROW LEVEL SECURITY;
            DO $policy$
            BEGIN
              IF NOT EXISTS (
                SELECT 1 FROM pg_policies
                WHERE schemaname = 'app'
                  AND tablename = 'projects'
                  AND policyname = 'projects_organization_isolation') THEN
                CREATE POLICY projects_organization_isolation ON app.projects
                  USING ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid)
                  WITH CHECK ("OrganizationId" = NULLIF(current_setting('app.organization_id', true), '')::uuid);
              END IF;
            END
            $policy$;
            """)
    ];

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IOrganizationEndpointContributor, ProjectsEndpoints>();
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddSingleton<IApplicationModelContributor, ProjectsModelContributor>();
        services.AddScoped<IWorkspaceOverviewMetricProvider, ProjectsOverviewMetricProvider>();
        services.AddSingleton<IValidator<CreateProjectCommand>, CreateProjectValidator>();
    }
}
