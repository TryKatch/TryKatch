using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Trykatch.Application;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Execution;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleEventCoordinatorStartupTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public async Task InstalledConsumerRequiresAnExplicitCoordinatorEvenWhenJobsAndLiveDeliveryAreDisabled(string? coordinator)
    {
        await using ServiceProvider provider = Create(coordinator, installed: true);
        ModuleWorkStartupValidator validator = Validator(provider);

        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));

        exception.Message.ShouldContain("trykatch-work");
        exception.Message.ShouldNotContain("unreachable");
    }

    [TestMethod]
    [DataRow(false, true, null)]
    [DataRow(true, true, "Host=127.0.0.1;Port=1;Database=unreachable;Username=trykatch_work_coordinator")]
    [DataRow(true, false, null)]
    public async Task ValidConfiguredOrGenerationOrConsumerAbsentStartupDoesNotOpenADatabase(
        bool installed, bool normalStartup, string? coordinator)
    {
        await using ServiceProvider provider = Create(coordinator, installed, isGeneration: !normalStartup);
        await Validator(provider).StartAsync(CancellationToken.None);
    }

    private static ModuleWorkStartupValidator Validator(ServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<ModuleWorkStartupValidator>().Single();

    private static ServiceProvider Create(string? coordinator, bool installed, bool isGeneration = false)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatchdb"] = "Host=127.0.0.1;Port=1;Database=unreachable;Username=postgres",
            ["ConnectionStrings:trykatch-work"] = coordinator
        }).Build();
        ServiceCollection services = new();
        services.AddSingleton(configuration);
        services.AddApplication();
        services.AddModules(configuration, installed ? [new ProjectsModule()] : []);
        services.AddInfrastructure(configuration, isDevelopment: true, isOpenApiGeneration: isGeneration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
