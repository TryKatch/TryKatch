using Microsoft.Extensions.Configuration;
using Shouldly;
using Trykatch.Infrastructure.Quotas;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationRequestLimitSettingsTests
{
    [TestMethod]
    public void RequestThrottlingIsDisabledByDefaultWithFiniteOptInDefaults()
    {
        OrganizationRequestLimitSettings settings = OrganizationRequestLimitSettings.Create(new ConfigurationBuilder().Build());
        settings.Enabled.ShouldBeFalse();
        settings.PermitLimit.ShouldBe(300);
        settings.WindowSeconds.ShouldBe(60);
    }

    [TestMethod]
    public void FlattenedHostConfigurationCanRepresentAParentObjectAsAnEmptyValue()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OrganizationRequestLimits"] = "", ["OrganizationRequestLimits:Enabled"] = "true"
        }).Build();
        OrganizationRequestLimitSettings.Create(configuration).Enabled.ShouldBeTrue();
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(1_000_000, 3_600)]
    public void ExplicitRequestPolicyUsesConfiguredFiniteBounds(int permits, int seconds)
    {
        OrganizationRequestLimitSettings settings = OrganizationRequestLimitSettings.Create(Configuration(
            ("Enabled", "true"), ("PermitLimit", permits.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("WindowSeconds", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        settings.Enabled.ShouldBeTrue();
        settings.PermitLimit.ShouldBe(permits);
        settings.WindowSeconds.ShouldBe(seconds);
    }

    [TestMethod]
    [DataRow("PermitLimit", "0")]
    [DataRow("PermitLimit", "1000001")]
    [DataRow("PermitLimit", "2147483648")]
    [DataRow("WindowSeconds", "0")]
    [DataRow("WindowSeconds", "3601")]
    [DataRow("WindowSeconds", "unbounded")]
    [DataRow("Enabled", "sometimes")]
    [DataRow("PermitLimit:Value", "1")]
    [DataRow("Unknown", "1")]
    public void InvalidRequestPolicyFailsClosedEvenWhenDisabled(string key, string value) =>
        Should.Throw<InvalidOperationException>(() => OrganizationRequestLimitSettings.Create(Configuration((key, value))));

    private static IConfiguration Configuration(params (string Key, string Value)[] values) => new ConfigurationBuilder()
        .AddInMemoryCollection(values.ToDictionary(item => "OrganizationRequestLimits:" + item.Key, item => (string?)item.Value))
        .Build();
}
