using Trykatch.Infrastructure.Features;
using Trykatch.Modules;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationFeatureTests
{
    private static readonly ModuleFeatureDescriptor DeclaredEnabled = new(
        "projects.reads", "Project reads", "View projects.", true);

    private static readonly ModuleFeatureDescriptor DeclaredDisabled = DeclaredEnabled with
    {
        DefaultEnabled = false
    };

    [TestMethod]
    [DynamicData(nameof(PrecedenceCases))]
    public void AvailabilityUsesTheFirstApplicablePolicyLayer(PrecedenceCase testCase)
    {
        FeatureAvailabilityPolicy.IsEnabled(
            testCase.Declaration,
            testCase.DeploymentDisabled,
            testCase.ForceDisabled,
            testCase.OrganizationOverride,
            testCase.EnvironmentDefault,
            testCase.ConfiguredDefault).ShouldBe(testCase.Expected, testCase.Name);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void ReadAndWriteFeaturesAreEvaluatedIndependently(bool readsEnabled, bool writesEnabled)
    {
        ModuleFeatureDescriptor reads = DeclaredEnabled with
        {
            Key = "projects.reads",
            DefaultEnabled = readsEnabled
        };
        ModuleFeatureDescriptor writes = DeclaredEnabled with
        {
            Key = "projects.writes",
            DefaultEnabled = writesEnabled
        };

        FeatureAvailabilityPolicy.IsEnabled(reads, false, false, null, null, null).ShouldBe(readsEnabled);
        FeatureAvailabilityPolicy.IsEnabled(writes, false, false, null, null, null).ShouldBe(writesEnabled);
    }

    public static IEnumerable<object[]> PrecedenceCases =>
    [
        Case("unknown declaration ignores enabled overrides", null, false, false, true, true, true, false),
        Case("deployment disable vetoes enabled policies", DeclaredEnabled, true, false, true, true, true, false),
        Case("platform force disable vetoes enabled policies", DeclaredEnabled, false, true, true, true, true, false),
        Case("organization false overrides every default", DeclaredEnabled, false, false, false, true, true, false),
        Case("organization true overrides every default", DeclaredDisabled, false, false, true, false, false, true),
        Case("environment false overrides configured and declared defaults", DeclaredEnabled, false, false, null, false, true, false),
        Case("environment true overrides configured and declared defaults", DeclaredDisabled, false, false, null, true, false, true),
        Case("configured false overrides declared default", DeclaredEnabled, false, false, null, null, false, false),
        Case("configured true overrides declared default", DeclaredDisabled, false, false, null, null, true, true),
        Case("declared true is the final default", DeclaredEnabled, false, false, null, null, null, true),
        Case("declared false is the final default", DeclaredDisabled, false, false, null, null, null, false)
    ];

    private static object[] Case(
        string name,
        ModuleFeatureDescriptor? declaration,
        bool deploymentDisabled,
        bool forceDisabled,
        bool? organizationOverride,
        bool? environmentDefault,
        bool? configuredDefault,
        bool expected) =>
    [
        new PrecedenceCase(name, declaration, deploymentDisabled, forceDisabled,
            organizationOverride, environmentDefault, configuredDefault, expected)
    ];

    public sealed record PrecedenceCase(
        string Name,
        ModuleFeatureDescriptor? Declaration,
        bool DeploymentDisabled,
        bool ForceDisabled,
        bool? OrganizationOverride,
        bool? EnvironmentDefault,
        bool? ConfiguredDefault,
        bool Expected);
}
