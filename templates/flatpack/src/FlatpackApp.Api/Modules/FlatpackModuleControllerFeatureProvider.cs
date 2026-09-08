using System.Reflection;
using FlatpackApp.Modules;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace FlatpackApp.Api.Modules;

/// <summary>
/// Removes controllers owned by modules that are not present in the explicit catalog.
/// It runs after MVC discovery, so disabled modules have no mapped HTTP surface.
/// </summary>
public sealed class FlatpackModuleControllerFeatureProvider(IReadOnlySet<string> enabledModuleIds)
    : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        _ = parts;
        for (int index = feature.Controllers.Count - 1; index >= 0; index--)
        {
            FlatpackModuleAttribute? owner = feature.Controllers[index].GetCustomAttribute<FlatpackModuleAttribute>();
            if (owner is not null && !enabledModuleIds.Contains(owner.ModuleId))
                feature.Controllers.RemoveAt(index);
        }
    }
}
