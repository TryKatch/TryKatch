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
        string connectionString = configuration.GetConnectionString("trykatchdb")
            ?? throw new InvalidOperationException("Connection string 'trykatchdb' is required.");

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
