using TrykatchApp.Application.Authorization;
using TrykatchApp.Application.Auditing;
using TrykatchApp.Application.Identity;
using TrykatchApp.Application.Organizations;
using TrykatchApp.Application.Outbox;
using TrykatchApp.Application.Overview;
using TrykatchApp.Infrastructure.Organizations;
using TrykatchApp.Infrastructure.Identity;
using TrykatchApp.Infrastructure.Persistence;
using TrykatchApp.Infrastructure.Overview;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrykatchApp.Modules;
using TrykatchApp.Infrastructure.Modules;
#if TRYKATCH_EMAIL
using TrykatchApp.Infrastructure.Modules.Email;
#endif
#if TRYKATCH_STORAGE
using TrykatchApp.Infrastructure.Modules.Storage;
#endif

namespace TrykatchApp.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
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
        services.AddTrykatchEmail();
#else
        services.AddSingleton<IAccountRecoveryNotifier, NoOpAccountRecoveryNotifier>();
        services.AddSingleton<IInvitationNotifier, NoOpInvitationNotifier>();
#endif
#if TRYKATCH_STORAGE
        services.AddTrykatchStorage(configuration);
#endif
        return services;
    }
}
