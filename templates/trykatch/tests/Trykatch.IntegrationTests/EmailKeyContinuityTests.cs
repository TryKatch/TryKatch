#if TRYKATCH_EMAIL
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Identity;
using Trykatch.Infrastructure.Modules.Email;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class EmailKeyContinuityTests
{
    [TestMethod]
    public async Task HostIdentityKeyRepositorySurvivesProviderRecreationAndRejectsOtherPurposes()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        EmailDeliveryProtectionContext context = new(
            EmailDeliveryBoundary.Identity,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "core.password-reset",
            1);
        const string privatePacket = "{\"recipient\":\"recipient@example.test\",\"token\":\"fixture-secret-token\"}";

        IEmailPayloadProtector originalProtector = host.Services.GetRequiredService<IEmailPayloadProtector>();
        string protectedPacket = originalProtector.Protect(context, privatePacket);
        protectedPacket.ShouldNotContain("recipient@example.test");
        protectedPacket.ShouldNotContain("fixture-secret-token");

        // SecurityHost disposes and recreates the WebApplicationFactory/service provider in this process.
        // Its identity connection and PostgreSQL data_protection_keys repository remain the same.
        await host.RestartAsync(new Dictionary<string, string?>(), environment: "Development");
        IEmailPayloadProtector recreatedProtector = host.Services.GetRequiredService<IEmailPayloadProtector>();
        recreatedProtector.Unprotect(context, protectedPacket).ShouldBe(privatePacket);
        Should.Throw<CryptographicException>(() => recreatedProtector.Unprotect(
            context with { BoundaryOwnerId = Guid.NewGuid() }, protectedPacket));

        string identityConnection = new Npgsql.NpgsqlConnectionStringBuilder(host.OwnerConnection)
        {
            Username = PostgresRuntimeRoleFixture.IdentityRole,
            Password = PostgresRuntimeRoleFixture.IdentityPassword
        }.ConnectionString;
        ServiceCollection changedDiscriminatorServices = new();
        changedDiscriminatorServices.AddDbContext<IdentityDbContext>(options => options.UseNpgsql(identityConnection));
        changedDiscriminatorServices.AddDataProtection()
            .PersistKeysToDbContext<IdentityDbContext>()
            .SetApplicationName("Trykatch-email-key-continuity-other-application");
        using ServiceProvider changedDiscriminatorProvider = changedDiscriminatorServices.BuildServiceProvider();
        DataProtectionEmailPayloadProtector changedDiscriminatorProtector = new(
            changedDiscriminatorProvider.GetRequiredService<IDataProtectionProvider>());
        Should.Throw<CryptographicException>(() => changedDiscriminatorProtector.Unprotect(context, protectedPacket));
    }
}
#endif
