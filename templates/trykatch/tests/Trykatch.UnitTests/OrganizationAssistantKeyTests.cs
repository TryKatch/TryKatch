using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Shouldly;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationAssistantKeyTests
{
    [TestMethod]
    public void SavedKeysAreAuthenticatedAndOrganizationBound()
    {
        OrganizationAssistantKeyProtector protector = new(new EphemeralDataProtectionProvider());
        Guid organization = Guid.NewGuid();
        string ciphertext = protector.Protect(organization, "test-only-provider-key");
        ciphertext.ShouldNotContain("test-only-provider-key");
        protector.Unprotect(organization, ciphertext).ShouldBe("test-only-provider-key");
        Should.Throw<CryptographicException>(() => protector.Unprotect(Guid.NewGuid(), ciphertext));
        Should.Throw<CryptographicException>(() => protector.Unprotect(organization, ciphertext[..^8] + "tampered"));
    }

    [TestMethod]
    public void PublicSettingsContainOnlyKeyPresenceAndCommandFormattingIsRedacted()
    {
        string json = JsonSerializer.Serialize(new OrganizationAssistantSettingDto(false, Guid.NewGuid(), "deepseek", "model", "https://api.deepseek.com", true));
        json.ShouldContain("HasApiKey");
        json.ShouldNotContain("ProtectedApiKey");
        json.ShouldNotContain("test-only-provider-key");
        new SaveOrganizationAssistantSetting(true, Guid.NewGuid(), ApiKey: "test-only-provider-key").ToString().ShouldNotContain("test-only-provider-key");
    }
}
