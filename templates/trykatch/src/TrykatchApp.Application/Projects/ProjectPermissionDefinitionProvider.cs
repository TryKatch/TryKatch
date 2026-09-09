using TrykatchApp.Application.Authorization;

namespace TrykatchApp.Application.Projects;

public sealed class ProjectPermissionDefinitionProvider : IPermissionDefinitionProvider
{
    private static readonly IReadOnlyList<PermissionModuleDefinition> Modules =
    [
        new("projects", "Projects", "Organization-owned project records.", 30,
        [
            new(Permissions.ProjectsRead, "View projects", "View projects and their details.", Order: 10,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member, DefaultOrganizationRoles.Viewer]),
            new(Permissions.ProjectsManage, "Manage projects", "Create, change, archive, restore, and request reasoned deletion of projects.", IsSensitive: true, Order: 20,
                DefaultRoles: [DefaultOrganizationRoles.Admin, DefaultOrganizationRoles.Member])
        ])
    ];

    public IReadOnlyList<PermissionModuleDefinition> GetModules() => Modules;
}
