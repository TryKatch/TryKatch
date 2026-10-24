#if TRYKATCH_EMAIL
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class EmailPayloadProtectionTests
{
    [TestMethod]
    public void EmailModuleResolvesProtectionFromTheSharedHostProviderWithoutOpeningSmtp()
    {
        EphemeralDataProtectionProvider keys = new();
        ServiceCollection services = new();
        services.AddSingleton<IDataProtectionProvider>(keys);
        services.AddEmailModule(new ConfigurationBuilder().Build(), isDevelopment: true);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });

        IEmailPayloadProtector protector = provider.GetRequiredService<IEmailPayloadProtector>();
        string protectedIntent = protector.Protect(Context, "private intent");

        new DataProtectionEmailPayloadProtector(keys).Unprotect(Context, protectedIntent).ShouldBe("private intent");
    }

    private static readonly EmailDeliveryProtectionContext Context = new(
        EmailDeliveryBoundary.Organization,
        Guid.Parse("44e22c6f-e44c-4572-92d0-ff98cac013b7"),
        Guid.Parse("bcb8baa3-8553-45b9-b13d-948247281682"),
        "orders.summary", 1);

    [TestMethod]
    public void ProtectedIntentRoundTripsWithoutPersistingRecipientOrTokenInPlaintext()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());
        const string intent = "{\"recipient\":\"recipient@example.test\",\"token\":\"private-reset-token\",\"policy\":\"orders.current-recipient\"}";

        string protectedIntent = protector.Protect(Context, intent);

        protectedIntent.ShouldNotContain("recipient@example.test");
        protectedIntent.ShouldNotContain("private-reset-token");
        protector.Unprotect(Context, protectedIntent).ShouldBe(intent);
    }

    [TestMethod]
    public void ProtectedIntentCannotMoveAcrossOwnerDeliveryBoundaryOrTemplateVersion()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());
        string protectedIntent = protector.Protect(Context, "private intent");
        EmailDeliveryProtectionContext[] differentPurposes =
        [
            Context with { Boundary = EmailDeliveryBoundary.Identity },
            Context with { BoundaryOwnerId = Guid.NewGuid() },
            Context with { DeliveryId = Guid.NewGuid() },
            Context with { TemplateKey = "orders.other" },
            Context with { TemplateVersion = 2 }
        ];

        foreach (EmailDeliveryProtectionContext other in differentPurposes)
            Should.Throw<CryptographicException>(() => protector.Unprotect(other, protectedIntent));
    }

    [TestMethod]
    public void ProtectionRejectsMissingBoundaryIdentityRatherThanSharingAnEmptyPurpose()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());

        Should.Throw<ArgumentException>(() => protector.Protect(Context with { BoundaryOwnerId = Guid.Empty }, "private intent"));
        Should.Throw<ArgumentException>(() => protector.Protect(Context with { DeliveryId = Guid.Empty }, "private intent"));
        Should.Throw<ArgumentException>(() => protector.Protect(Context with { TemplateVersion = 0 }, "private intent"));
        Should.Throw<ArgumentException>(() => protector.Protect(Context with { Boundary = (EmailDeliveryBoundary)99 }, "private intent"));
    }
}
#endif
