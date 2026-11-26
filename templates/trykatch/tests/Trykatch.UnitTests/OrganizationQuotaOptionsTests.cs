using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaOptionsTests
{
    [TestMethod]
    public void MissingConfigurationReturnsDisabledRevisionZeroAndNoAllowances()
    {
        QuotaSettings settings = QuotaOptions.CreateSettings(Configuration(), Catalog());

        settings.EnforcementEnabled.ShouldBeFalse();
        settings.Revision.ShouldBe(0);
        settings.Allowances.ShouldBeEmpty();
        settings.Hash.ShouldNotBeNullOrWhiteSpace();
    }

    [TestMethod]
    public void EnforcedConfigurationRequiresPositiveRevision()
    {
        IConfiguration configuration = Configuration(
            ("Quotas:EnforcementEnabled", "true"),
            ("Quotas:Revision", "0"));

        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(configuration, Catalog()));
    }

    [TestMethod]
    public void AcceptsFiniteZeroMaximumAndExplicitUnlimitedAllowances()
    {
        IConfiguration configuration = Configuration(
            ("Quotas:EnforcementEnabled", "true"),
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "0"),
            ("Quotas:Resources:documents.storage-bytes:Limit", long.MaxValue.ToString(CultureInfo.InvariantCulture)),
            ("Quotas:Resources:projects.exports:Unlimited", "true"));

        QuotaSettings settings = QuotaOptions.CreateSettings(configuration, Catalog());

        settings.Allowances["projects.seats"].Mode.ShouldBe(OrganizationEntitlementMode.Finite);
        settings.Allowances["projects.seats"].Limit.ShouldBe(0L);
        settings.Allowances["documents.storage-bytes"].Mode.ShouldBe(OrganizationEntitlementMode.Finite);
        settings.Allowances["documents.storage-bytes"].Limit.ShouldBe(long.MaxValue);
        settings.Allowances["projects.exports"].Mode.ShouldBe(OrganizationEntitlementMode.Unlimited);
        settings.Allowances["projects.exports"].Limit.ShouldBeNull();
    }

    [TestMethod]
    public void AllowsDisabledPositiveRevisionAndValidUnusedEntries()
    {
        IConfiguration configuration = Configuration(
            ("Quotas:EnforcementEnabled", "false"),
            ("Quotas:Revision", "4"),
            ("Quotas:Resources:projects.seats:Limit", "12"));

        QuotaSettings settings = QuotaOptions.CreateSettings(configuration, Catalog());

        settings.EnforcementEnabled.ShouldBeFalse();
        settings.Revision.ShouldBe(4);
        settings.Allowances["projects.seats"].Limit.ShouldBe(12L);
    }

    [TestMethod]
    [DataRow("not-a-boolean")]
    [DataRow("1")]
    [DataRow("")]
    public void RejectsMalformedEnforcementFlag(string value)
    {
        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(
            Configuration(("Quotas:EnforcementEnabled", value)), Catalog()));
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("9223372036854775808")]
    [DataRow("1.5")]
    [DataRow("not-a-number")]
    [DataRow("")]
    public void RejectsMalformedRevision(string value)
    {
        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(
            Configuration(("Quotas:Revision", value)), Catalog()));
    }

    [TestMethod]
    [DataRow("-1")]
    [DataRow("9223372036854775808")]
    [DataRow("1.5")]
    [DataRow("not-a-number")]
    [DataRow("")]
    public void RejectsMalformedFiniteLimit(string value)
    {
        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(
            Configuration(("Quotas:Revision", "1"), ("Quotas:Resources:projects.seats:Limit", value)), Catalog()));
    }

    [TestMethod]
    public void RejectsMalformedResourceTreesAndUnknownProperties()
    {
        (string Key, string Value)[][] invalidConfigurations =
        [
            [("Quotas:Resources", "scalar")],
            [("Quotas:Resources:projects.seats", "scalar")],
            [("Quotas:Resources:projects.seats:Unknown", "true")],
            [("Quotas:Unknown", "true")],
            [("Quotas:Revision:Child", "1")],
            [("Quotas:EnforcementEnabled:Child", "true")],
            [("Quotas:Resources:projects.seats:Limit", "1"), ("Quotas:Resources:projects.seats:Unlimited", "true")],
            [("Quotas:Resources:projects.seats:Unlimited", "false")],
            [("Quotas:Resources:projects.seats:Unlimited", "sometimes")],
            [("Quotas:Resources:projects.seats", "")]
        ];

        foreach ((string Key, string Value)[] values in invalidConfigurations)
            Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(Configuration(values), Catalog()));
    }

    [TestMethod]
    [DataRow("projects.unknown")]
    [DataRow("Projects.seats")]
    [DataRow("projects.*")]
    public void RejectsUnknownWildcardOrIncorrectlyCasedCatalogResourceKeys(string resourceKey)
    {
        IConfiguration configuration = Configuration(
            ("Quotas:Revision", "1"),
            ($"Quotas:Resources:{resourceKey}:Limit", "1"));

        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(configuration, Catalog()));
    }

    [TestMethod]
    public void RejectsCaseVariantOrDuplicateSemanticConfigurationProperties()
    {
        IConfiguration caseVariant = Configuration(
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:limit", "1"));
        IConfiguration bothAllowanceModes = Configuration(
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "1"),
            ("Quotas:Resources:projects.seats:Unlimited", "true"));

        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(caseVariant, Catalog()));
        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(bothAllowanceModes, Catalog()));
    }

    [TestMethod]
    public void RevisionZeroRequiresDisabledAndNoResourceEntries()
    {
        IConfiguration enabled = Configuration(("Quotas:EnforcementEnabled", "true"));
        IConfiguration resource = Configuration(("Quotas:Resources:projects.seats:Limit", "1"));

        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(enabled, Catalog()));
        Should.Throw<InvalidOperationException>(() => QuotaOptions.CreateSettings(resource, Catalog()));
    }

    [TestMethod]
    public void HashIsStableAcrossEntryOrderAndIgnoresRevisionAndInstalledCatalog()
    {
        IConfiguration first = Configuration(
            ("Quotas:EnforcementEnabled", "true"),
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "10"),
            ("Quotas:Resources:documents.storage-bytes:Unlimited", "true"));
        IConfiguration reorderedAndNewRevision = Configuration(
            ("Quotas:Resources:documents.storage-bytes:Unlimited", "true"),
            ("Quotas:Resources:projects.seats:Limit", "10"),
            ("Quotas:Revision", "2"),
            ("Quotas:EnforcementEnabled", "true"));

        string firstHash = QuotaOptions.CreateSettings(first, Catalog()).Hash;
        string secondHash = QuotaOptions.CreateSettings(reorderedAndNewRevision, Catalog(withExtraResource: true)).Hash;

        firstHash.ShouldBe(secondHash);
    }

    [TestMethod]
    public void HashChangesWhenEnforcementOrAllowanceChanges()
    {
        string baseline = QuotaOptions.CreateSettings(Configuration(
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "10")), Catalog()).Hash;
        string changedAllowance = QuotaOptions.CreateSettings(Configuration(
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "11")), Catalog()).Hash;
        string changedEnforcement = QuotaOptions.CreateSettings(Configuration(
            ("Quotas:EnforcementEnabled", "true"),
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "10")), Catalog()).Hash;

        baseline.ShouldNotBe(changedAllowance);
        baseline.ShouldNotBe(changedEnforcement);
    }

    [TestMethod]
    public void ReturnedAllowancesAreImmutableAndDoNotTrackConfigurationChanges()
    {
        IConfiguration configuration = Configuration(
            ("Quotas:Revision", "1"),
            ("Quotas:Resources:projects.seats:Limit", "10"));

        QuotaSettings settings = QuotaOptions.CreateSettings(configuration, Catalog());
        configuration["Quotas:Resources:projects.seats:Limit"] = "20";

        settings.Allowances["projects.seats"].Limit.ShouldBe(10L);
        Should.Throw<NotSupportedException>(() =>
            ((IDictionary<string, QuotaAllowance>)settings.Allowances).Add(
                "documents.storage-bytes", new QuotaAllowance(OrganizationEntitlementMode.Finite, 1)));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();

    private static ModuleCatalog Catalog(bool withExtraResource = false)
    {
        ModuleQuotaResourceDescriptor[] projectsResources = withExtraResource
            ? [Resource("projects.seats"), Resource("projects.exports"), Resource("projects.extra")]
            : [Resource("projects.seats"), Resource("projects.exports")];
        StubModule[] modules =
        [
            new(new ModuleDescriptor("projects", "Projects", "1.0.0", "Projects test module.", [], [], ModuleCapabilities.Api, [])
            {
                QuotaResources = projectsResources
            }),
            new(new ModuleDescriptor("documents", "Documents", "1.0.0", "Documents test module.", [], [], ModuleCapabilities.Api, [])
            {
                QuotaResources = [Resource("documents.storage-bytes")]
            })
        ];
        return new ModuleCatalog(modules);
    }

    private static ModuleQuotaResourceDescriptor Resource(string key) =>
        new(key, "Test quota", "A test quota resource.", ModuleQuotaUnit.Count);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
