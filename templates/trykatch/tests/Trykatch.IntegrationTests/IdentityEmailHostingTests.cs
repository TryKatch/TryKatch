using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
public sealed class IdentityEmailHostingTests
{
    [TestMethod]
    public async Task MissingNamedIdentityConnectionDoesNotFallbackOrRegisterQueueSource()
    {
        ServiceCollection services = CreateServices(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatchdb"] = "Host=legacy-identity-fallback.invalid;Database=legacy;Username=unused;Password=unused"
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        IIdentitySecurityEmailSubmission submission = provider.GetRequiredService<IIdentitySecurityEmailSubmission>();
        submission.IsConfigured.ShouldBeFalse();
        ModuleWorkReceipt unavailable = await submission.EnqueuePasswordResetAsync(
            new PasswordResetEmailIntent(Guid.NewGuid(), Guid.NewGuid(), "closed@trykatch.test", "Closed", "token", "https://reset.trykatch.test/reset-password"),
            CancellationToken.None);
        unavailable.State.ShouldBe(ModuleWorkState.Unavailable);
        unavailable.FailureCode.ShouldBe("email_unavailable");
        provider.GetService<IEmailDeliverySource>().ShouldBeNull();
        provider.GetService<IdentityEmailDeliveryStore>().ShouldBeNull();
        provider.GetService<DbContextOptions<IdentityEmailDeliveryDbContext>>().ShouldBeNull();
    }

    [TestMethod]
    public void OpenApiGenerationDoesNotRegisterQueueSourceOrOpenConfiguredConnection()
    {
        ServiceCollection services = CreateServices(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatch-identity"] = "Host=identity-openapi-no-network.invalid;Database=identity;Username=unused;Password=unused;Timeout=1",
            ["Application:PublicUrl"] = "https://reset.trykatch.test"
        }, isOpenApiGeneration: true);
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IIdentitySecurityEmailSubmission>().IsConfigured.ShouldBeFalse();
        provider.GetService<IEmailDeliverySource>().ShouldBeNull();
        provider.GetService<IdentityEmailDeliveryStore>().ShouldBeNull();
        provider.GetService<DbContextOptions<IdentityEmailDeliveryDbContext>>().ShouldBeNull();
    }

#if TRYKATCH_EMAIL
    [TestMethod]
    public void InvalidPublicOriginKeepsSubmissionClosed()
    {
        ServiceCollection services = CreateServices(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatch-identity"] = ConnectionString,
            ["Application:PublicUrl"] = "http://reset.trykatch.test"
        }, isDevelopment: false);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IIdentitySecurityEmailSubmission>().IsConfigured.ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<IEmailDeliverySource>().Boundary.ShouldBe(EmailDeliveryBoundary.Identity);
    }

    [TestMethod]
    public void ActiveRegistrationUsesOnlyNamedIdentityConnectionAndNoRetryQueueOptions()
    {
        ServiceCollection services = CreateServices(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatch-identity"] = ConnectionString,
            ["ConnectionStrings:trykatchdb"] = "Host=must-not-be-used.invalid;Database=legacy;Username=unused;Password=unused",
            ["Application:PublicUrl"] = "https://reset.trykatch.test/base"
        });
        using ServiceProvider provider = services.BuildServiceProvider(validateScopes: true);
        using IServiceScope scope = provider.CreateScope();

        IIdentitySecurityEmailSubmission submission = scope.ServiceProvider.GetRequiredService<IIdentitySecurityEmailSubmission>();
        IEmailDeliverySource source = scope.ServiceProvider.GetRequiredService<IEmailDeliverySource>();
        ReferenceEquals(submission, source).ShouldBeTrue();
        submission.IsConfigured.ShouldBeTrue();
        source.Boundary.ShouldBe(EmailDeliveryBoundary.Identity);

        DbContextOptions<IdentityEmailDeliveryDbContext> options = provider.GetRequiredService<DbContextOptions<IdentityEmailDeliveryDbContext>>();
        using IdentityEmailDeliveryDbContext context = new(options);
        context.Database.GetConnectionString().ShouldBe(ConnectionString);
        context.Database.CreateExecutionStrategy().RetriesOnFailure.ShouldBeFalse();
    }
#endif

#if (!TRYKATCH_EMAIL)
    [TestMethod]
    public void EmailDisabledBuildRegistersUnavailableSubmissionWithoutQueueServices()
    {
        ServiceCollection services = CreateServices(new Dictionary<string, string?>
        {
            ["ConnectionStrings:trykatch-identity"] = ConnectionString,
            ["Application:PublicUrl"] = "https://reset.trykatch.test"
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IIdentitySecurityEmailSubmission>().IsConfigured.ShouldBeFalse();
        provider.GetService<IEmailDeliverySource>().ShouldBeNull();
        provider.GetService<IdentityEmailDeliveryStore>().ShouldBeNull();
        provider.GetService<DbContextOptions<IdentityEmailDeliveryDbContext>>().ShouldBeNull();
    }
#endif

    private const string ConnectionString = "Host=identity-email-test.invalid;Database=identity;Username=unused;Password=unused;Timeout=1";

    private static ServiceCollection CreateServices(
        Dictionary<string, string?> settings,
        bool isDevelopment = true,
        bool isOpenApiGeneration = false)
    {
        ServiceCollection services = new();
        IConfiguration configuration = Configuration(settings);
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        string identityConnection = configuration.GetConnectionString("trykatch-identity")
            ?? configuration.GetConnectionString("trykatchdb")
            ?? "Host=identity-context-unused.invalid;Database=identity;Username=unused;Password=unused";
        services.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(identityConnection));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IdentityDbContext>()
            .AddDefaultTokenProviders();
        services.AddSingleton(TimeProvider.System);
#if TRYKATCH_EMAIL
        services.AddSingleton<IEmailPayloadProtector, TestProtector>();
        services.AddSingleton<IPasswordResetEmailRenderer, TestRenderer>();
#endif
        IdentityEmailHosting.AddIdentityEmailDelivery(services, configuration, isDevelopment, isOpenApiGeneration);
        return services;
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

#if TRYKATCH_EMAIL
    private sealed class TestProtector : IEmailPayloadProtector
    {
        public string Protect(EmailDeliveryProtectionContext context, string intent) => intent;
        public string Unprotect(EmailDeliveryProtectionContext context, string protectedIntent) => protectedIntent;
    }

    private sealed class TestRenderer : IPasswordResetEmailRenderer
    {
        public EmailPreparedMessage Render(PasswordResetEmailIntent intent) =>
            new(intent.Recipient, "Reset password", "Use the reset link.");
    }
#endif
}
