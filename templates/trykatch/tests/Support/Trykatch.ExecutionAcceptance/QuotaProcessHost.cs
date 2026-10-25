using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trykatch.Application;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Storage;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.ExecutionAcceptance;

/// <summary>Runs actual quota admission/fencing with OS-process crash points for integration acceptance.</summary>
internal static class QuotaProcessHost
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<int> RunAsync(bool producer)
    {
        TextWriter protocolOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            string? phase = ReadPhase(producer);
            IConfiguration configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            HostApplicationBuilder builder = Host.CreateApplicationBuilder();
            builder.Logging.ClearProviders();
            builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = false
            }));

            DateTimeOffset? clockUtc = ReadClock();
            if (clockUtc is { } fixedUtc)
                builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedUtc));

            builder.Services.AddApplication();
            bool documentsAbsent = !producer && Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_QUOTA_DOCUMENTS_ABSENT") == "1";
            List<IModule> modules = [new ProjectsModule()];
            if (!documentsAbsent) modules.Add(new DocumentsModule());
            builder.Services.AddModules(configuration, modules);
            builder.Services.AddInfrastructure(configuration);

            QuotaCrashProbe? probe = phase is null ? null : new QuotaCrashProbe(phase, protocolOutput);
            if (probe is not null)
            {
                builder.Services.AddSingleton(probe);
                WrapConditionalStorage(builder.Services, probe);
                if (!producer && phase == "after-hold-release")
                {
                    builder.Services.ConfigureDbContext<ApplicationDbContext>((services, options) =>
                        options.AddInterceptors(services.GetRequiredService<QuotaReleaseCommandInterceptor>(),
                            services.GetRequiredService<QuotaReleaseTransactionInterceptor>()));
                    builder.Services.ConfigureDbContext<OrganizationControlPlaneDbContext>((services, options) =>
                        options.AddInterceptors(services.GetRequiredService<QuotaReleaseCommandInterceptor>(),
                            services.GetRequiredService<QuotaReleaseTransactionInterceptor>()));
                    builder.Services.AddSingleton<QuotaReleaseCommitProbe>();
                    builder.Services.AddSingleton<QuotaReleaseCommandInterceptor>();
                    builder.Services.AddSingleton<QuotaReleaseTransactionInterceptor>();
                }
            }

            if (!producer)
            {
                ServiceDescriptor[] unrelatedWorkers = builder.Services
                    .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType?.Name != "QuotaMaintenanceWorker")
                    .ToArray();
                foreach (ServiceDescriptor descriptor in unrelatedWorkers)
                    builder.Services.Remove(descriptor);
            }

            using IHost host = builder.Build();
            if (producer)
                return await ProduceAsync(host.Services, probe, protocolOutput);
            return await RunWorkerAsync(host, configuration, protocolOutput);
        }
        catch (Exception exception)
        {
            await WriteAsync(protocolOutput, new
            {
                kind = "quota-error",
                code = exception.GetType().Name,
                processId = Environment.ProcessId
            });
            Console.Error.WriteLine($"QUOTA_PROCESS_FAILED {exception.GetType().Name}");
            return 1;
        }
        finally
        {
            Console.SetOut(protocolOutput);
        }
    }

    private static async Task<int> ProduceAsync(IServiceProvider services, QuotaCrashProbe? probe, TextWriter protocolOutput)
    {
        Guid organizationId = RequiredGuid("TRYKATCH_ACCEPTANCE_ORGANIZATION_ID");
        Guid actorId = RequiredGuid("TRYKATCH_ACCEPTANCE_ACTOR_ID");
        Guid operationId = RequiredGuid("TRYKATCH_ACCEPTANCE_UPLOAD_OPERATION_ID");
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider
            .GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organizationId, actorId, CancellationToken.None);
        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        _ = await OrganizationTransactionEnlistment.EnlistAsync(control, application,
            organizationId, actorId, CancellationToken.None);
        OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>()
            .ResolveAsync(actorId, organizationId, CancellationToken.None);
        if (access is null || access.OrganizationId != organizationId || access.ActorId != actorId)
            throw new InvalidOperationException("Current organization membership is unavailable for quota process admission.");
        scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);

        byte[] payload = Encoding.UTF8.GetBytes("quota process upload");
        string sha256 = Convert.ToHexString(SHA256.HashData(payload));
        ModuleTransactionCompensation compensation = new();
        DocumentsUseCases documents = ActivatorUtilities.CreateInstance<DocumentsUseCases>(scope.ServiceProvider, compensation);
        DocumentOperationResult<DocumentDto> result = await documents.UploadAsync(new UploadDocumentCommand(
            "Process upload", "Crash recovery", "process.txt", "text/plain", payload.LongLength,
            sha256, new MemoryStream(payload, writable: false), OperationId: operationId), CancellationToken.None);
        if (!result.IsSuccess || result.Value is null)
            throw new InvalidOperationException($"Production Documents upload failed with code '{result.Code ?? "unknown"}'.");

        await transaction.CommitAsync(CancellationToken.None);
        if (probe?.Phase == "after-final-commit") await probe.CheckpointAsync();
        await WriteAsync(protocolOutput, new
        {
            kind = "quota-producer-completed",
            processId = Environment.ProcessId,
            organizationId,
            actorId,
            operationId,
            subjectId = result.Value.Id
        });
        return 0;
    }

    private static async Task<int> RunWorkerAsync(IHost host, IConfiguration configuration, TextWriter protocolOutput)
    {
        await host.StartAsync(CancellationToken.None);
        string organizationRole = await QueryCurrentUserAsync(configuration, RuntimeDatabaseConnectionContract.Organization);
        string platformRole = await QueryCurrentUserAsync(configuration, RuntimeDatabaseConnectionContract.Platform);
        string identityRole = await QueryCurrentUserAsync(configuration, RuntimeDatabaseConnectionContract.Identity);
        string outboxRole = await QueryCurrentUserAsync(configuration, RuntimeDatabaseConnectionContract.Outbox);
        string coordinatorRole = await QueryCurrentUserAsync(configuration, RuntimeDatabaseConnectionContract.WorkCoordinator);
        await WriteAsync(protocolOutput, new
        {
            kind = "quota-ready",
            processId = Environment.ProcessId,
            roles = new
            {
                organization = organizationRole,
                platform = platformRole,
                identity = identityRole,
                outbox = outboxRole,
                coordinator = coordinatorRole
            }
        });
        while (await Console.In.ReadLineAsync() is not null) { }
        await host.StopAsync(CancellationToken.None);
        return 0;
    }

    private static async Task<string> QueryCurrentUserAsync(IConfiguration configuration, string name)
    {
        await using NpgsqlConnection connection = new(ConnectionString(configuration, name));
        await connection.OpenAsync(CancellationToken.None);
        await using NpgsqlCommand command = new("SELECT current_user", connection);
        return (string?)await command.ExecuteScalarAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Runtime database role could not be confirmed.");
    }

    private static string ConnectionString(IConfiguration configuration, string name) =>
        configuration.GetConnectionString(name)
        ?? throw new InvalidOperationException("A required runtime database connection is missing.");

    private static string? ReadPhase(bool producer)
    {
        string? phase = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_QUOTA_PHASE");
        string[] allowed = producer
            ? ["before-put", "after-put", "after-final-commit"]
            : ["before-sentinel", "after-sentinel", "after-hold-release"];
        if (phase is not null && !allowed.Contains(phase, StringComparer.Ordinal))
            throw new InvalidOperationException("Unsupported quota acceptance checkpoint.");
        return phase;
    }

    private static DateTimeOffset? ReadClock()
    {
        string? value = Environment.GetEnvironmentVariable("TRYKATCH_ACCEPTANCE_QUOTA_CLOCK_UTC");
        if (value is null) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset result))
            throw new InvalidOperationException("Quota acceptance clock must be an ISO timestamp.");
        return result;
    }

    private static Guid RequiredGuid(string name) => Guid.TryParse(Environment.GetEnvironmentVariable(name), out Guid value)
        && value != Guid.Empty
            ? value
            : throw new InvalidOperationException($"Environment variable {name} must contain a non-empty GUID.");

    private static void WrapConditionalStorage(IServiceCollection services, QuotaCrashProbe probe)
    {
        ServiceDescriptor descriptor = services.Single(item => item.ServiceType == typeof(IConditionalObjectStorage));
        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Singleton(typeof(IConditionalObjectStorage), provider =>
        {
            IConditionalObjectStorage inner = descriptor.ImplementationFactory is { } factory
                ? (IConditionalObjectStorage)factory(provider)
                : descriptor.ImplementationInstance is { } instance
                    ? (IConditionalObjectStorage)instance
                    : throw new InvalidOperationException("Conditional object storage must have an explicit factory or instance.");
            return new GatedConditionalObjectStorage(inner, probe);
        }));
    }

    private static async Task WriteAsync<T>(TextWriter writer, T value)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(value, Json));
        await writer.FlushAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class QuotaCrashProbe(string phase, TextWriter protocolOutput)
    {
        private int reached;
        internal string Phase { get; } = phase;

        internal Task CheckpointAsync() => Interlocked.Exchange(ref reached, 1) == 0
            ? CheckpointCoreAsync()
            : Task.CompletedTask;

        internal async Task CheckpointCoreAsync()
        {
            await WriteAsync(protocolOutput, new
            {
                kind = "quota-checkpoint",
                phase = Phase,
                processId = Environment.ProcessId
            });
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class GatedConditionalObjectStorage(IConditionalObjectStorage inner, QuotaCrashProbe probe)
        : IConditionalObjectStorage
    {
        private int sentinelStat;
        public string NamespaceFingerprint => inner.NamespaceFingerprint;

        public async Task<ConditionalObjectWriteResult> TryCreateAsync(string key, Stream content, long length,
            string contentType, ObjectWriteIdentity identity, CancellationToken cancellationToken = default)
        {
            if (probe.Phase == "before-put") await probe.CheckpointAsync();
            ConditionalObjectWriteResult result = await inner.TryCreateAsync(key, content, length, contentType,
                identity, cancellationToken);
            if (probe.Phase == "after-put") await probe.CheckpointAsync();
            return result;
        }

        public async Task<ConditionalObjectStat> StatAsync(string key, CancellationToken cancellationToken = default)
        {
            if (probe.Phase == "before-sentinel" && Interlocked.Exchange(ref sentinelStat, 1) == 0)
                await probe.CheckpointAsync();
            return await inner.StatAsync(key, cancellationToken);
        }

        public async Task<ConditionalObjectWriteResult> TryReplaceWithCancellationAsync(string key,
            ObjectStorageRevision? expectedVersion, ObjectWriteIdentity identity,
            CancellationToken cancellationToken = default)
        {
            ConditionalObjectWriteResult result = await inner.TryReplaceWithCancellationAsync(key, expectedVersion,
                identity, cancellationToken);
            if (probe.Phase == "after-sentinel") await probe.CheckpointAsync();
            return result;
        }
    }

    private sealed class QuotaReleaseCommitProbe(QuotaCrashProbe probe)
    {
        private readonly ConcurrentDictionary<DbTransaction, byte> releasedTransactions = new(ReferenceEqualityComparer.Instance);

        internal void ObserveReleaseCommand(DbCommand command, int result)
        {
            if (result == 1 && command.CommandText.Contains("UPDATE app.module_upload_operations SET \"State\" = 3",
                    StringComparison.OrdinalIgnoreCase)
                && command.Transaction is { } transaction)
                releasedTransactions[transaction] = 0;
        }

        internal Task ObserveCommittedAsync(DbTransaction transaction) =>
            releasedTransactions.TryRemove(transaction, out _) ? probe.CheckpointAsync() : Task.CompletedTask;
    }

    private sealed class QuotaReleaseCommandInterceptor(QuotaReleaseCommitProbe state) : DbCommandInterceptor
    {

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            state.ObserveReleaseCommand(command, result);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class QuotaReleaseTransactionInterceptor(QuotaReleaseCommitProbe state) : DbTransactionInterceptor
    {
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await state.ObserveCommittedAsync(transaction);
        }
    }
}
