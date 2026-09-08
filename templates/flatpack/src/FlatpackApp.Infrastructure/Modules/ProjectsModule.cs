using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Projects;
using FlatpackApp.Infrastructure.Projects;
using FlatpackApp.Modules;
using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Infrastructure.Modules;

/// <summary>
/// Reference full-stack module. Its public interface is the module descriptor;
/// application and persistence details remain behind the registration seam.
/// </summary>
public sealed class ProjectsModule : IFlatpackModule
{
    public FlatpackModuleDescriptor Descriptor { get; } = new(
        Id: "projects",
        Name: "Projects",
        Version: "1.0.0",
        Description: "Organization-scoped project management and lifecycle reference feature.",
        Requires: [],
        OptionalDependencies: [],
        Capabilities: FlatpackModuleCapabilities.Api
            | FlatpackModuleCapabilities.Web
            | FlatpackModuleCapabilities.Data
            | FlatpackModuleCapabilities.BackgroundWork);

    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ProjectUseCases>();
        services.AddScoped<IProjectStore, ProjectStore>();
        services.AddSingleton<IValidator<CreateProjectCommand>, CreateProjectValidator>();
        services.AddSingleton<IPermissionDefinitionProvider, ProjectPermissionDefinitionProvider>();
    }
}
