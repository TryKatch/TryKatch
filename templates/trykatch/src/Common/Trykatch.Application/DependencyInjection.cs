using Trykatch.Application.Authorization;
using Trykatch.Application.Organizations;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Trykatch.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CreateOrganization>();
        services.AddScoped<ManageOrganizations>();
        services.AddScoped<OrganizationAdministration>();
        services.AddSingleton<IPermissionDefinitionProvider, BuiltInPermissionDefinitionProvider>();
        services.AddSingleton<IPermissionCatalog, PermissionCatalog>();
        services.AddSingleton<IValidator<CreateOrganizationCommand>, CreateOrganizationValidator>();
        services.AddSingleton<IValidator<UpdateOrganizationCommand>, UpdateOrganizationValidator>();
        return services;
    }
}
