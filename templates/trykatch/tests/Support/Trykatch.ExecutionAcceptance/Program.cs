using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Trykatch.Application;
using Trykatch.Application.Execution;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.ExecutionAcceptance;

internal static class Program
{
    private const string CreateProjectOperation = "projects.create";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("producer" or "worker"))
        {
            Console.Error.WriteLine("Expected process mode: producer or worker.");
            return 2;
        }

        try
        {
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            ConfigureHost(builder);
            using IHost host = builder.Build();
            return args[0] == "producer"
                ? await ProduceAsync(host.Services)
                : await RunWorkerAsync(host);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"EXECUTION_ACCEPTANCE_FAILED {exception.GetType().Name}: {exception.Message}");
            return 1;
        }
    }

    private static void ConfigureHost(HostApplicationBuilder builder)
    {
        builder.Logging.ClearProviders();
        IConfiguration configuration = builder.Configuration;
        builder.Services.AddApplication();
        builder.Services.AddModules(configuration,
        [
            new ProjectsModule(),
            new DocumentsModule(),
            new AcceptanceModule()
        ]);
        builder.Services.AddInfrastructure(configuration);
    }

    private static async Task<int> ProduceAsync(IServiceProvider services)
    {
        Guid workId = RequiredGuid("TRYKATCH_ACCEPTANCE_PROJECT_WORK_ID");
        Guid organizationId = RequiredGuid("TRYKATCH_ACCEPTANCE_ORGANIZATION_ID");
        Guid actorId = RequiredGuid("TRYKATCH_ACCEPTANCE_ACTOR_ID");
        Guid targetId = RequiredGuid("TRYKATCH_ACCEPTANCE_JOB_TARGET_ID");

        ModuleExecutionOutcome project = await services.GetRequiredService<IOrganizationWorkExecutor>()
            .ExecuteAsync(new(workId, CreateProjectOperation, 1), CancellationToken.None);
        if (project.State is not (ModuleExecutionState.Completed or ModuleExecutionState.AlreadyCompleted))
            throw new InvalidOperationException($"Parent-seeded projects.create work did not complete: {project.State}/{project.FailureCode}.");

        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(organizationId, actorId, CancellationToken.None);
        OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
            .ResolveAsync(actorId, organizationId, CancellationToken.None);
        if (access is null || access.OrganizationId != organizationId || access.ActorId != actorId)
            throw new InvalidOperationException("Current organization membership is unavailable for synthetic job admission.");
        scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);

        AcceptanceJobPayload payload = new(targetId);
        ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<IModuleJobScheduler>()
            .EnqueueAsync(new(Guid.NewGuid(), AcceptanceModule.JobKey, 1, JsonSerializer.SerializeToElement(payload)));
        if (receipt.State != ModuleWorkState.Accepted || receipt.WorkId == Guid.Empty)
            throw new InvalidOperationException($"Synthetic user command was not accepted: {receipt.State}/{receipt.FailureCode}.");
        await transaction.CommitAsync(CancellationToken.None);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "producer-committed",
            processId = Environment.ProcessId,
            projectWorkId = workId,
            projectState = project.State.ToString(),
            jobWorkId = receipt.WorkId,
            jobState = receipt.State.ToString(),
            organizationId,
            actorId
        }));
        return 0;
    }

    private static async Task<int> RunWorkerAsync(IHost host)
    {
        await host.StartAsync(CancellationToken.None);
        IScheduler scheduler = await host.Services.GetRequiredService<ISchedulerFactory>()
            .GetScheduler(CancellationToken.None);
        string readyPath = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_READY_FILE")
            ?? throw new InvalidOperationException("Worker ready file path is missing.");
        string ready = JsonSerializer.Serialize(new
        {
            kind = "worker-ready",
            processId = Environment.ProcessId,
            instance = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_INSTANCE") ?? "unspecified",
            schedulerInstanceId = scheduler.SchedulerInstanceId,
            organizationRole = RoleName(host.Services.GetRequiredService<IConfiguration>(), RuntimeDatabaseConnectionContract.Organization),
            outboxRole = RoleName(host.Services.GetRequiredService<IConfiguration>(), RuntimeDatabaseConnectionContract.Outbox),
            workCoordinatorRole = RoleName(host.Services.GetRequiredService<IConfiguration>(), RuntimeDatabaseConnectionContract.WorkCoordinator)
        });
        string temporaryPath = readyPath + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, ready, CancellationToken.None);
            File.Move(temporaryPath, readyPath);
        }
        finally { File.Delete(temporaryPath); }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "worker-ready",
            processId = Environment.ProcessId,
            instance = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_INSTANCE") ?? "unspecified",
            schedulerInstanceId = scheduler.SchedulerInstanceId
        }));
        await host.WaitForShutdownAsync(CancellationToken.None);
        return 0;
    }

    private static string RoleName(IConfiguration configuration, string connection)
    {
        string value = configuration[$"ConnectionStrings:{connection}"]
            ?? throw new InvalidOperationException($"Runtime connection profile '{connection}' is missing.");
        return new Npgsql.NpgsqlConnectionStringBuilder(value).Username
            ?? throw new InvalidOperationException($"Runtime connection profile '{connection}' has no role name.");
    }

    private static Guid RequiredGuid(string name) => Guid.TryParse(Environment.GetEnvironmentVariable(name), out Guid value)
        && value != Guid.Empty
            ? value
            : throw new InvalidOperationException($"Environment variable {name} must contain a non-empty GUID.");
}
