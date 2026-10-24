using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Shouldly;
using Trykatch.Application;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Jobs;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleJobAvailabilityTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("false")]
    public async Task DefaultAndDisabledJobsReturnUnavailableWithoutResolvingOrganizationOrOpeningConnections(string? enabled)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Enabled"] = enabled,
            ["ConnectionStrings:trykatch-organization"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_org_runtime",
            ["ConnectionStrings:trykatch-platform"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_platform_runtime",
            ["ConnectionStrings:trykatch-identity"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_identity_runtime",
            ["ConnectionStrings:trykatch-outbox"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_outbox_worker",
            ["ConnectionStrings:trykatch-work"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_work_coordinator"
        }).Build();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddApplication();
        services.AddModules(configuration, []);
        services.AddInfrastructure(configuration, isDevelopment: true);

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetService<ISchedulerFactory>().ShouldBeNull();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IModuleJobScheduler scheduler = scope.ServiceProvider.GetRequiredService<IModuleJobScheduler>();
        ModuleJobRequest request = new(Guid.NewGuid(), "absent.job", 1, JsonSerializer.SerializeToElement(new { value = 1 }));

        ModuleWorkReceipt enqueued = await scheduler.EnqueueAsync(request);
        ModuleWorkReceipt scheduled = await scheduler.ScheduleAsync(request, DateTimeOffset.UtcNow.AddMinutes(1));

        enqueued.ShouldBe(new ModuleWorkReceipt(Guid.Empty, ModuleWorkState.Unavailable, FailureCode: "jobs_unavailable"));
        scheduled.ShouldBe(enqueued);
    }

    [TestMethod]
    public void EnabledJobsDoNotFallBackToLegacyOwnerConnectionWhenWorkConnectionIsAbsent()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jobs:Enabled"] = "true",
            ["ConnectionStrings:trykatchdb"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=postgres"
        }).Build();

        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddModuleJobs(configuration));

        exception.Message.ShouldContain("trykatch-work");
    }
}
