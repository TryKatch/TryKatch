using TrykatchApp.Application.Authorization;
using TrykatchApp.Modules;

namespace TrykatchApp.Infrastructure.Modules;

internal sealed class ModulePermissionDefinitionProvider(TrykatchModuleCatalog catalog) : IPermissionDefinitionProvider
{
    public IReadOnlyList<PermissionModuleDefinition> GetModules() => catalog.Descriptors
        .Where(module => module.Permissions.Count > 0)
        .Select((module, index) => new PermissionModuleDefinition(
            module.Id,
            module.Name,
            module.Description,
            100 + index,
            module.Permissions.Select(permission => new PermissionDefinition(
                permission.Key,
                permission.Name,
                permission.Description,
                permission.IsSensitive,
                permission.Order,
                permission.DefaultRoles)).ToArray()))
        .ToArray();
}
