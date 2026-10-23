using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Quartz;
using Npgsql;
using Trykatch.Infrastructure.Execution;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Jobs;

/// <summary>Opt-in registration for the host-owned durable module job bridge.</summary>
public static class ModuleJobHosting
{
    public static IServiceCollection AddModuleJobs(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isOpenApiGeneration = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<IModuleJobScheduler, PostgresModuleJobScheduler>();
        services.AddScoped<ModuleJobRegistry>();
        services.AddScoped<ModuleScheduleRegistry>();
        if (isOpenApiGeneration || !configuration.GetValue<bool>("Jobs:Enabled")) return services;

        string coordinatorConnection = RuntimeDatabaseConnectionContract.Get(
            configuration,
            RuntimeDatabaseConnectionContract.WorkCoordinator);
        if (new NpgsqlConnectionStringBuilder(coordinatorConnection).Username != "trykatch_work_coordinator")
            throw new InvalidOperationException("Module jobs require the fixed work-coordinator PostgreSQL role.");
        services.AddSingleton<ModuleJobWorkerState>();
        services.AddHealthChecks().AddCheck<ModuleJobHealthCheck>("module-jobs", tags: ["ready"]);
        services.AddQuartz(quartz =>
        {
            quartz.ConfigureScheduler(options =>
            {
                options.InstanceName = "TrykatchModuleJobs";
                options.InstanceId = "AUTO";
                options.ShutdownJobInterruption = ShutdownJobInterruption.WhenWaitingForJobs;
            });
            quartz.UsePersistentStore(store =>
            {
                store.UsePostgres(dataSource => dataSource.ConnectionString = coordinatorConnection);
                store.UseSystemTextJsonSerializer();
                store.ConfigureStore(options =>
                {
                    options.TablePrefix = "infrastructure.qrtz_";
                    options.StoreJobDataAsStrings = true;
                    options.SchemaProvisioning = SchemaProvisioning.Validate;
                });
                store.UseClustering();
            });
        });
        services.AddQuartzHostedService(options => options.WaitForJobsToComplete = true);
        services.AddSingleton<ModuleJobProcessor>();
        services.AddHostedService(provider => provider.GetRequiredService<ModuleJobProcessor>());
        return services;
    }
}
