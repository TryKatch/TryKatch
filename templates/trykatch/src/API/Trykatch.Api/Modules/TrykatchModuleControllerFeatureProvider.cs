using System.Reflection;
using Trykatch.Modules;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Trykatch.Api.Modules;

/// <summary>
/// Removes controllers owned by modules that are not present in the explicit catalog.
/// It runs after MVC discovery, so disabled modules have no mapped HTTP surface.
/// </summary>
public sealed class ModuleControllerFeatureProvider(IReadOnlySet<string> enabledModuleIds)
    : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        _ = parts;
        for (int index = feature.Controllers.Count - 1; index >= 0; index--)
        {
            ModuleAttribute? owner = feature.Controllers[index].GetCustomAttribute<ModuleAttribute>();
            if (owner is not null && !enabledModuleIds.Contains(owner.ModuleId))
                feature.Controllers.RemoveAt(index);
        }
    }
}
