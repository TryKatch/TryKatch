using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QuotaCompositionTests
{
    [TestMethod]
    public async Task StartupCapturesOneImmutablePolicyWithoutDatabaseOrBillingServices()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        ServiceCollection services = new();
        services.AddSingleton(new ModuleCatalog([]));
        services.AddOrganizationQuotas(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        QuotaSettings initial = provider.GetRequiredService<QuotaSettings>();
        configuration["Quotas:EnforcementEnabled"] = "true";
        configuration["Quotas:Revision"] = "3";
        foreach (IHostedService validator in provider.GetServices<IHostedService>())
            await validator.StartAsync(CancellationToken.None);
        provider.GetRequiredService<QuotaSettings>().ShouldBeSameAs(initial);
        initial.EnforcementEnabled.ShouldBeFalse();
        initial.Revision.ShouldBe(0);
    }

    [TestMethod]
    public void InvalidStartupPolicyFailsBeforeAnyDatabaseConnectionCanBeResolved()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quotas:EnforcementEnabled"] = "true", ["Quotas:Revision"] = "0"
        }).Build();
        ServiceCollection services = new();
        services.AddSingleton(new ModuleCatalog([]));
        services.AddOrganizationQuotas(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        Should.Throw<InvalidOperationException>(() => provider.GetServices<IHostedService>().ToArray())
            .Message.ShouldBe("Organization quota configuration is invalid.");
    }

    [TestMethod]
    public async Task StartupRejectsDeclaredInventoryWithoutItsRegisteredAdapter()
    {
        ServiceCollection services = new();
        services.AddSingleton(new ModuleCatalog([new InventoryModule()]));
        services.AddOrganizationQuotas(new ConfigurationBuilder().Build());
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        IHostedService[] validators = provider.GetServices<IHostedService>().ToArray();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            foreach (IHostedService validator in validators)
                await validator.StartAsync(CancellationToken.None);
        });
    }

    [TestMethod]
    public async Task StartupValidatesWorkflowBindingsInScopeWithoutReadingInventory()
    {
        ServiceCollection services = new();
        services.AddSingleton(new ModuleCatalog([new InventoryModule()]));
        services.AddScoped<ModuleQuotaInventorySourceBinding>(_ => new(
            new("inventory", "inventory.source", 1), new UnreadableInventory()));
        services.AddOrganizationQuotas(new ConfigurationBuilder().Build());
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        foreach (IHostedService validator in provider.GetServices<IHostedService>())
            await validator.StartAsync(CancellationToken.None);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<QuotaWorkflowRegistry>()
            .TryGetInventory("inventory.bytes", out _).ShouldBeTrue();
    }

    private sealed class InventoryModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("inventory", "Inventory", "1.0.0", "Inventory fixture.",
            [], [], ModuleCapabilities.Api, [])
        {
            QuotaResources = [new("inventory.bytes", "Bytes", "Stored bytes.", ModuleQuotaUnit.Bytes)
            { InventorySource = new("inventory", "inventory.source", 1) }]
        };
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class UnreadableInventory : IModuleQuotaInventorySource
    {
        public Task<ModuleQuotaInventoryPage> ReadPageAsync(string? cursor, int pageSize,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Startup must not execute inventory.");
    }
}
