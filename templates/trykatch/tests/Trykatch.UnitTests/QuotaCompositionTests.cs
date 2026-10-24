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
}
