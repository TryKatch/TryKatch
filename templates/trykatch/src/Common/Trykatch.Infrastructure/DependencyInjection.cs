using Trykatch.Application.Authorization;
using Trykatch.Application.Auditing;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Application.Outbox;
using Trykatch.Application.Overview;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Identity;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Overview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trykatch.Modules;
using Trykatch.Infrastructure.Modules;
#if TRYKATCH_EMAIL
using Trykatch.Infrastructure.Modules.Email;
#endif
#if TRYKATCH_STORAGE
using Trykatch.Infrastructure.Modules.Storage;
#endif

namespace Trykatch.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration, bool isDevelopment = false, bool isOpenApiGeneration = false)
    {
        string platformConnection = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Platform);
        string organizationConnection = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Organization);
        string outboxConnection = RuntimeDatabaseConnectionContract.Get(configuration, RuntimeDatabaseConnectionContract.Outbox);

        services.AddDbContext<PlatformDbContext>(options => options.UseNpgsql(platformConnection));
        services.AddDbContext<OrganizationControlPlaneDbContext>(options => options.UseNpgsql(organizationConnection));
        // Organization-scoped requests use explicit transactions on both contexts so
        // PostgreSQL transaction-local RLS variables cannot leak between pooled
        // connections. Retrying an entire HTTP transaction could replay downstream
        // side effects, therefore these contexts deliberately avoid EF retries.
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(organizationConnection));
        services.AddDbContext<OutboxDbContext>(options => options.UseNpgsql(outboxConnection));

        services.AddScoped<OrganizationContext>();
        services.AddScoped<IOrganizationContext>(provider => provider.GetRequiredService<OrganizationContext>());
        services.AddScoped<IOrganizationContextInitializer>(provider => provider.GetRequiredService<OrganizationContext>());
        services.AddScoped<IOrganizationAccessResolver, OrganizationAccessResolver>();
        services.AddScoped<PermissionAuthorizer>();
        services.AddScoped<IPermissionAuthorizer>(provider => provider.GetRequiredService<PermissionAuthorizer>());
        services.AddScoped<IModulePermissionAuthorizer>(provider => provider.GetRequiredService<PermissionAuthorizer>());
        services.AddSingleton<IPermissionDefinitionProvider, ModulePermissionDefinitionProvider>();
        services.AddScoped<IOrganizationDirectory, OrganizationDirectory>();
        services.AddScoped<IOrganizationInvitationTokenProtector, OrganizationInvitationTokenProtector>();
        services.AddScoped<IActorOrganizationDirectory, ActorOrganizationDirectory>();
        services.AddScoped<IOrganizationAdministrationStore, OrganizationAdministrationStore>();
        services.AddScoped<IOrganizationDataScopeFactory, OrganizationDataScopeFactory>();
        services.AddScoped<IOrganizationDataPlacement, OrganizationDataPlacement>();
        services.AddScoped<IOrganizationDataPlacementAdapter, SharedPostgresDataPlacement>();
        services.AddScoped<IOrganizationDataPlacementAdapter, DedicatedPostgresDataPlacement>();
        services.TryAddSingleton<IDedicatedPostgresProvisioner, UnconfiguredDedicatedPostgresProvisioner>();
        services.AddScoped<IAuditReader, AuditReader>();
        services.AddScoped<IWorkspaceOverviewReader, WorkspaceOverviewReader>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IOutboxWriter, OutboxWriter>();
        services.AddScoped<IOrganizationModuleData, OrganizationModuleData>();
        services.TryAddSingleton<IOutboxTransport, LoggingOutboxTransport>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<OutboxDelivery>();
        services.AddHostedService<OutboxProcessor>();
#if TRYKATCH_EMAIL
        services.AddEmailModule(configuration, isDevelopment, isOpenApiGeneration);
#else
        services.AddSingleton<IAccountRecoveryNotifier, NoOpAccountRecoveryNotifier>();
        services.AddSingleton<IInvitationNotifier, NoOpInvitationNotifier>();
#endif
#if TRYKATCH_STORAGE
        services.AddStorageModule(configuration);
#endif
        return services;
    }
}
