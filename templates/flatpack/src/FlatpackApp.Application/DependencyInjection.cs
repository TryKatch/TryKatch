using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Application.Projects;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace FlatpackApp.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateOrganization>();
        services.AddScoped<ManageOrganizations>();
        services.AddScoped<OrganizationAdministration>();
        services.AddScoped<ProjectUseCases>();
        services.AddSingleton<IPermissionDefinitionProvider, BuiltInPermissionDefinitionProvider>();
        services.AddSingleton<IPermissionCatalog, PermissionCatalog>();
        services.AddSingleton<IValidator<CreateOrganizationCommand>, CreateOrganizationValidator>();
        services.AddSingleton<IValidator<UpdateOrganizationCommand>, UpdateOrganizationValidator>();
        services.AddSingleton<IValidator<CreateProjectCommand>, CreateProjectValidator>();
        return services;
    }
}
