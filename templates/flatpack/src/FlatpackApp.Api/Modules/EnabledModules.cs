using FlatpackApp.Infrastructure.Modules;
using FlatpackApp.Modules;

namespace FlatpackApp.Api.Modules;

/// <summary>
/// Explicit build-time module registry. Package tooling updates this file;
/// the application never scans arbitrary assemblies for executable code.
/// </summary>
internal static class EnabledModules
{
    public static IReadOnlyList<IFlatpackModule> All { get; } =
    [
        new ProjectsModule()
    ];
}
