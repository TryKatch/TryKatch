using FlatpackApp.Application.Authorization;
using FlatpackApp.Application.Auditing;
using FlatpackApp.Application.Identity;
using FlatpackApp.Application.Organizations;
using FlatpackApp.Application.Outbox;
using FlatpackApp.Application.Overview;
using FlatpackApp.Infrastructure.Organizations;
using FlatpackApp.Infrastructure.Identity;
using FlatpackApp.Infrastructure.Persistence;
using FlatpackApp.Infrastructure.Overview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
#if FLATPACK_EMAIL
using FlatpackApp.Infrastructure.Modules.Email;
#endif
#if FLATPACK_STORAGE
using FlatpackApp.Infrastructure.Modules.Storage;
#endif

namespace FlatpackApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("flatpackdb")
            ?? throw new InvalidOperationException("Connection string 'flatpackdb' is required.");

        services.AddDbContext<PlatformDbContext>(options => options.UseNpgsql(connectionString));
        // Organization-scoped requests use explicit transactions on both contexts so
        // PostgreSQL transaction-local RLS variables cannot leak between pooled
        // connections. Retrying an entire HTTP transaction could replay downstream
        // side effects, therefore these contexts deliberately avoid EF retries.
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<OrganizationContext>();
        services.AddScoped<IOrganizationContext>(provider => provider.GetRequiredService<OrganizationContext>());
        services.AddScoped<IOrganizationContextInitializer>(provider => provider.GetRequiredService<OrganizationContext>());
        services.AddScoped<IOrganizationAccessResolver, OrganizationAccessResolver>();
        services.AddScoped<IPermissionAuthorizer, PermissionAuthorizer>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        services.AddScoped<IOrganizationAdministrationStore, OrganizationAdministrationStore>();
        services.AddScoped<IOrganizationDataScopeFactory, OrganizationDataScopeFactory>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<IWorkspaceOverviewReader, WorkspaceOverviewReader>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.TryAddSingleton<IOutboxTransport, LoggingOutboxTransport>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OutboxDelivery>();
        services.AddHostedService<OutboxProcessor>();
#if FLATPACK_EMAIL
        services.AddFlatpackEmail();
#else
        services.AddSingleton<IAccountRecoveryNotifier, NoOpAccountRecoveryNotifier>();
#endif
#if FLATPACK_STORAGE
        services.AddFlatpackStorage(configuration);
#endif
        return services;
    }
}
