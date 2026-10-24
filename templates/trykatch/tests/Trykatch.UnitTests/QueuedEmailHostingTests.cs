#if TRYKATCH_EMAIL
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Application;
using Trykatch.Infrastructure;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Modules.Email;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QueuedEmailHostingTests
{
    [TestMethod]
    public async Task EmailModuleComposesOrganizationQueueOnlyWithExplicitWorkConnectionAndDoesNotStartWorker()
    {
        ServiceCollection services = new();
        services.AddEmailModule(Configuration(("ConnectionStrings:trykatch-work", "Host=work;Database=email")), isDevelopment: true);

        services.Any(descriptor => descriptor.ServiceType == typeof(IEmailDeliverySource)).ShouldBeTrue();
        services.Single(descriptor => descriptor.ServiceType == typeof(Trykatch.Modules.IModuleEmailQueue)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
        services.Single(descriptor => descriptor.ServiceType == typeof(EmailTemplateRegistry)).Lifetime.ShouldBe(ServiceLifetime.Scoped);
        services.Single(descriptor => descriptor.ServiceType == typeof(OrganizationEmailDeliveryStore)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Single(descriptor => descriptor.ServiceType == typeof(IEmailDeliverySource)).Lifetime.ShouldBe(ServiceLifetime.Singleton);
        services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType is not null
            && descriptor.ImplementationType.Name.Contains("QueuedEmail", StringComparison.Ordinal)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(QueuedEmailDispatcher)).ShouldBeFalse();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        DbContextOptions<OrganizationEmailDeliveryDbContext> options = provider
            .GetRequiredService<DbContextOptions<OrganizationEmailDeliveryDbContext>>();
        await using OrganizationEmailDeliveryDbContext context = new(options);
        context.Database.GetConnectionString().ShouldNotBeNull().ShouldContain("Database=email");
        context.Database.CreateExecutionStrategy().RetriesOnFailure.ShouldBeFalse();
    }

    [TestMethod]
    public void EmailModuleDoesNotUseLegacyConnectionWhenWorkConnectionIsAbsent()
    {
        ServiceCollection services = new();
        services.AddEmailModule(Configuration(("ConnectionStrings:trykatchdb", "Host=legacy;Database=legacy")), isDevelopment: true);

        services.Any(descriptor => descriptor.ServiceType == typeof(IEmailDeliverySource)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(Trykatch.Modules.IModuleEmailQueue)).ShouldBeFalse();
    }

    [TestMethod]
    public void OpenApiCompositionKeepsModuleQueueUnavailableEvenWithWorkConnection()
    {
        IConfiguration configuration = Configuration(
            ("ConnectionStrings:trykatchdb", "Host=legacy;Database=legacy"),
            ("ConnectionStrings:trykatch-work", "Host=work;Database=email"));
        ServiceCollection services = new();
        services.AddApplication();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration, isDevelopment: true, isOpenApiGeneration: true);
        services.AddModules(configuration, []);
        services.AddQueuedEmailDelivery(isOpenApiGeneration: true);

        services.Any(descriptor => descriptor.ServiceType == typeof(IEmailDeliverySource)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(OrganizationEmailDeliveryStore)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(QueuedEmailDispatcher)).ShouldBeFalse();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IModuleEmailQueue>().ShouldBeOfType<UnavailableModuleEmailQueue>();
    }

    [TestMethod]
    public void ScopedModuleEmailBindingsResolveWithScopedRegistryAndValidateScopes()
    {
        IConfiguration configuration = Configuration(
            ("ConnectionStrings:trykatchdb", "Host=localhost;Database=legacy;Username=none;Password=none"),
            ("ConnectionStrings:trykatch-work", "Host=work;Database=email"));
        ServiceCollection services = new();
        services.AddApplication();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration, isDevelopment: true);
        services.AddModules(configuration, [new EmailFixtureModule()]);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<EmailTemplateRegistry>().ShouldNotBeNull();
    }

    [TestMethod]
    public async Task OpenApiHostingDoesNotRegisterDispatcherOrResolveSources()
    {
        ServiceCollection services = new();
        CountingSource source = new(EmailDeliveryBoundary.Identity);
        services.AddScoped<IEmailDeliverySource>(_ => source);

        services.AddQueuedEmailDelivery(isOpenApiGeneration: true);

        services.Any(descriptor => descriptor.ServiceType == typeof(QueuedEmailDispatcher)).ShouldBeFalse();
        services.Any(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType?.Name.Contains("QueuedEmail", StringComparison.Ordinal) == true).ShouldBeFalse();
        await using ServiceProvider provider = services.BuildServiceProvider();
        source.ClaimCalls.ShouldBe(0);
        source.PrepareCalls.ShouldBe(0);
    }

    [TestMethod]
    public async Task IdentityOnlySourceCanValidateWithoutWorkDatabaseOrNetworkProbe()
    {
        ServiceCollection services = new();
        services.AddEmailModule(Configuration(), isDevelopment: true);
        CountingSource source = new(EmailDeliveryBoundary.Identity);
        services.AddScoped<IEmailDeliverySource>(_ => source);
        services.AddQueuedEmailDelivery(isOpenApiGeneration: false);

        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        provider.GetRequiredService<QueuedEmailDispatcher>().ShouldNotBeNull();
        IHostedService validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains("StartupValidator", StringComparison.Ordinal));
        await validator.StartAsync(CancellationToken.None);
        source.ClaimCalls.ShouldBe(0);
        source.PrepareCalls.ShouldBe(0);
    }

    [TestMethod]
    public async Task RuntimeHostingValidatorRejectsDuplicateSourceBoundaryWithoutClaiming()
    {
        ServiceCollection services = new();
        CountingSource first = new(EmailDeliveryBoundary.Identity);
        CountingSource second = new(EmailDeliveryBoundary.Identity);
        services.AddScoped<IEmailDeliverySource>(_ => first);
        services.AddScoped<IEmailDeliverySource>(_ => second);
        services.AddQueuedEmailDelivery(isOpenApiGeneration: false);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains("StartupValidator", StringComparison.Ordinal));
        await Should.ThrowAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
        first.ClaimCalls.ShouldBe(0);
        second.ClaimCalls.ShouldBe(0);
    }

    [TestMethod]
    public async Task RuntimeHostingValidatorRejectsUndefinedBoundaryWithoutClaiming()
    {
        ServiceCollection services = new();
        CountingSource source = new((EmailDeliveryBoundary)99);
        services.AddScoped<IEmailDeliverySource>(_ => source);
        services.AddQueuedEmailDelivery(isOpenApiGeneration: false);

        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService validator = provider.GetServices<IHostedService>()
            .Single(service => service.GetType().Name.Contains("StartupValidator", StringComparison.Ordinal));
        await Should.ThrowAsync<InvalidOperationException>(() => validator.StartAsync(CancellationToken.None));
        source.ClaimCalls.ShouldBe(0);
    }

    [TestMethod]
    public void CoreInfrastructureResolvesModuleQueueWithoutOpeningDatabaseConnections()
    {
        IConfiguration configuration = Configuration(
            ("ConnectionStrings:trykatchdb", "Host=localhost;Database=legacy;Username=none;Password=none"),
            ("ConnectionStrings:trykatch-work", "Host=localhost;Database=work;Username=none;Password=none"));
        ServiceCollection services = new();
        services.AddApplication();
        services.AddSingleton(configuration);
        services.AddInfrastructure(configuration, isDevelopment: true);
        services.AddModules(configuration, []);

        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<Trykatch.Modules.IModuleEmailQueue>()
            .ShouldBeOfType<OrganizationModuleEmailQueue>();
        OrganizationEmailDeliveryDbContext context = new(
            provider.GetRequiredService<DbContextOptions<OrganizationEmailDeliveryDbContext>>());
        context.Database.GetDbConnection().State.ShouldBe(System.Data.ConnectionState.Closed);
        context.Dispose();
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value)).Build();

    private sealed class CountingSource(EmailDeliveryBoundary boundary) : IEmailDeliverySource
    {
        public int ClaimCalls { get; private set; }
        public int PrepareCalls { get; private set; }
        public EmailDeliveryBoundary Boundary => boundary;
        public Task<IReadOnlyList<EmailDeliveryLease>> ClaimAsync(int limit, CancellationToken cancellationToken)
        {
            ClaimCalls++;
            return Task.FromResult<IReadOnlyList<EmailDeliveryLease>>([]);
        }
        public Task<EmailDeliveryPreparation> PrepareAsync(EmailDeliveryLease lease, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Not expected in hosting validation.");
        }
        public Task<bool> CompleteAsync(EmailDeliveryLease lease, EmailAttemptOutcome outcome, CancellationToken cancellationToken) =>
            Task.FromResult(false);
    }

    private static ModuleEmailTemplateRef TemplateRef() => new("messages", "message.notice", 1);
    private static ModuleEmailDisclosurePolicyRef PolicyRef() => new("messages", "message.disclose", 1);

    private sealed class EmailFixtureModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("messages", "Messages", "1.0.0", "Fixture", [], [],
            ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            DataResources = [new("messages", "app", "messages", ModuleDataOwnership.Organization, IsolationPolicy: "messages_isolation")],
            EmailTemplates = [new("messages", "message.notice", 1, 1024)],
            EmailDisclosurePolicies = [new("messages", "message.disclose", 1, ["messages"], [], [TemplateRef()], [])]
        };
        public void Register(IServiceCollection services, IConfiguration configuration)
        {
            services.AddScoped<ScopedRenderer>();
            services.AddScoped<ScopedDisclosurePolicy>();
            services.AddScoped<ModuleEmailTemplateBinding>(provider => new(TemplateRef(), provider.GetRequiredService<ScopedRenderer>()));
            services.AddScoped<ModuleEmailDisclosurePolicyBinding>(provider => new(PolicyRef(), provider.GetRequiredService<ScopedDisclosurePolicy>()));
        }
    }

    private sealed class ScopedRenderer : IModuleEmailTemplateRenderer
    {
        public System.Text.Json.JsonElement PreviewData => System.Text.Json.JsonSerializer.SerializeToElement(new { title = "Preview" });
        public ModuleEmailContent Render(System.Text.Json.JsonElement canonicalData) => new("Subject", "Body");
    }

    private sealed class ScopedDisclosurePolicy : IModuleEmailDisclosurePolicy
    {
        public Task<ModuleEmailDisclosureDecision> ResolveAsync(ModuleEmailDisclosureContext context,
            ModuleEmailMessage message, CancellationToken cancellationToken) =>
            Task.FromResult(new ModuleEmailDisclosureDecision(ModuleEmailDisclosureState.Denied));
    }
}
#endif
