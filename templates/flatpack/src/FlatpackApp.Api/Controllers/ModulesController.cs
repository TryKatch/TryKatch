using FlatpackApp.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FlatpackApp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/modules")]
public sealed class ModulesController(FlatpackModuleCatalog catalog) : ControllerBase
{
    [HttpGet(Name = "Modules_List")]
    public ActionResult<IReadOnlyList<ModuleDto>> List()
    {
        ModuleDto[] modules = catalog.Descriptors.Select(descriptor => new ModuleDto(
            descriptor.Id,
            descriptor.Name,
            descriptor.Version,
            descriptor.Description,
            descriptor.Requires,
            descriptor.OptionalDependencies,
            Enum.GetValues<FlatpackModuleCapabilities>()
                .Where(capability => capability != FlatpackModuleCapabilities.None && descriptor.Capabilities.HasFlag(capability))
                .Select(capability => capability.ToString())
                .ToArray(),
            descriptor.ExtensionPoints.Select(point => new ModuleExtensionPointDto(
                point.Id,
                point.Description,
                point.Kind.ToString(),
                point.Surface.ToString())).ToArray())).ToArray();

        return Ok(modules);
    }
}

public sealed record ModuleDto(
    string Id,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<string> Requires,
    IReadOnlyList<string> OptionalDependencies,
    IReadOnlyList<string> Capabilities,
    IReadOnlyList<ModuleExtensionPointDto> ExtensionPoints);

public sealed record ModuleExtensionPointDto(
    string Id,
    string Description,
    string Kind,
    string Surface);
