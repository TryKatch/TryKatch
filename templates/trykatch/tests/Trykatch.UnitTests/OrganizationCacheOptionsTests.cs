using Microsoft.Extensions.Options;
using Shouldly;
using Trykatch.Infrastructure.Caching;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationCacheOptionsTests
{
    private readonly OrganizationCacheOptionsValidator validator = new();

    [TestMethod]
    public void InvalidLargeMapDoesNotMultiplyDiagnosticMessages()
    {
        Dictionary<string, int> values = DefaultTtls();
        for (int index = 0; index < 100; index++) values["invalid_value_" + index] = -1;

        ValidateOptionsResult result = validator.Validate(null, new OrganizationCacheOptions { TtlSeconds = values });

        result.Failed.ShouldBeTrue();
        result.Failures!.Count().ShouldBeLessThanOrEqualTo(7);
    }

    [TestMethod]
    public void DefaultsAreDisabledAndUseBoundedHostTtlPolicies()
    {
        OrganizationCacheOptions options = new();

        options.Mode.ShouldBe(OrganizationCacheMode.Disabled);
        options.ApplicationNamespace.ShouldBeNull();
        options.TtlSeconds.ShouldBe(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["short"] = 30,
            ["standard"] = 120
        });
        options.FactoryTimeout.ShouldBe(TimeSpan.FromSeconds(5));
        OrganizationCacheOptions.MaximumPayloadBytes.ShouldBe(65_536);
        Validate(options).ShouldBeTrue();
    }

    [TestMethod]
    public void ValidOverridesAndInclusiveBoundsAreAccepted()
    {
        OrganizationCacheOptions options = new()
        {
            Mode = OrganizationCacheMode.Distributed,
            ApplicationNamespace = "trykatch-prod",
            TtlSeconds = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["short"] = 1,
                ["standard"] = 600,
                ["module-detail"] = 60
            },
            FactoryTimeout = TimeSpan.FromSeconds(30)
        };

        Validate(options).ShouldBeTrue();

        Validate(new OrganizationCacheOptions
        {
            Mode = options.Mode,
            ApplicationNamespace = options.ApplicationNamespace,
            TtlSeconds = options.TtlSeconds,
            FactoryTimeout = TimeSpan.FromSeconds(1)
        }).ShouldBeTrue();
    }

    [TestMethod]
    public void UndefinedModeIsRejected()
    {
        Validate(new OrganizationCacheOptions { Mode = (OrganizationCacheMode)99 }).ShouldBeFalse();
    }

    [TestMethod]
    public void OptionalApplicationNamespaceMustBeLowerKebabAndBounded()
    {
        string[] invalidNamespaces =
        [
            "Upper-case",
            "contains space",
            "ends-",
            "double--hyphen",
            "-starts",
            "",
            new string('a', 121)
        ];

        foreach (string applicationNamespace in invalidNamespaces)
        {
            Validate(new OrganizationCacheOptions { ApplicationNamespace = applicationNamespace }).ShouldBeFalse();
        }

        Validate(new OrganizationCacheOptions { ApplicationNamespace = null }).ShouldBeTrue();
        Validate(new OrganizationCacheOptions { ApplicationNamespace = new string('a', 120) }).ShouldBeTrue();
    }

    [TestMethod]
    public void TtlMapRequiresShortAndStandardPoliciesAndAtMostThirtyTwoEntries()
    {
        Validate(new OrganizationCacheOptions
        {
            TtlSeconds = new Dictionary<string, int>(StringComparer.Ordinal) { ["standard"] = 120 }
        }).ShouldBeFalse();
        Validate(new OrganizationCacheOptions
        {
            TtlSeconds = new Dictionary<string, int>(StringComparer.Ordinal) { ["short"] = 30 }
        }).ShouldBeFalse();

        Dictionary<string, int> tooMany = new(StringComparer.Ordinal)
        {
            ["short"] = 30,
            ["standard"] = 120
        };
        for (int index = 0; index < 31; index++)
            tooMany[$"policy-{index}"] = 1;
        tooMany.Count.ShouldBe(33);
        Validate(new OrganizationCacheOptions { TtlSeconds = tooMany }).ShouldBeFalse();
    }

    [TestMethod]
    public void TtlPolicyNamesMustBeLowerKebabAndAtMostSixtyFourCharacters()
    {
        string[] invalidNames =
        [
            "Short",
            "bad_name",
            "bad.key",
            "ends-",
            "double--hyphen",
            "-starts",
            new string('a', 65)
        ];

        foreach (string name in invalidNames)
        {
            Dictionary<string, int> policies = DefaultTtls();
            policies[name] = 10;
            Validate(new OrganizationCacheOptions { TtlSeconds = policies }).ShouldBeFalse();
        }

        Dictionary<string, int> maximumName = DefaultTtls();
        maximumName[new string('a', 64)] = 10;
        Validate(new OrganizationCacheOptions { TtlSeconds = maximumName }).ShouldBeTrue();
    }

    [TestMethod]
    public void TtlDurationsMustBeWithinOneThroughSixHundredSeconds()
    {
        foreach (int seconds in new[] { 0, -1, 601 })
        {
            Dictionary<string, int> policies = DefaultTtls();
            policies["short"] = seconds;
            Validate(new OrganizationCacheOptions { TtlSeconds = policies }).ShouldBeFalse();
        }

        foreach (int seconds in new[] { 1, 600 })
        {
            Dictionary<string, int> policies = DefaultTtls();
            policies["short"] = seconds;
            Validate(new OrganizationCacheOptions { TtlSeconds = policies }).ShouldBeTrue();
        }
    }

    [TestMethod]
    public void FactoryTimeoutMustBeWithinOneThroughThirtySeconds()
    {
        foreach (TimeSpan timeout in new[]
                 {
                     TimeSpan.FromMilliseconds(999),
                     TimeSpan.FromSeconds(30.001),
                     TimeSpan.FromSeconds(-1),
                     TimeSpan.Zero
                 })
        {
            Validate(new OrganizationCacheOptions { FactoryTimeout = timeout }).ShouldBeFalse();
        }

        Validate(new OrganizationCacheOptions { FactoryTimeout = TimeSpan.FromSeconds(1) }).ShouldBeTrue();
        Validate(new OrganizationCacheOptions { FactoryTimeout = TimeSpan.FromSeconds(30) }).ShouldBeTrue();
    }

    [TestMethod]
    public void ValidationDiagnosticsNeverEchoRawSettingValues()
    {
        const string secretLikeSetting = "private-cache-secret-value";
        OrganizationCacheOptions options = new()
        {
            ApplicationNamespace = secretLikeSetting,
            TtlSeconds = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["short"] = 0,
                ["standard"] = 120,
                [secretLikeSetting] = 600
            }
        };

        ValidateOptionsResult result = validator.Validate(Options.DefaultName, options);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull();
        string diagnostics = string.Join(" ", result.Failures);
        diagnostics.ShouldNotContain(secretLikeSetting);
    }

    private bool Validate(OrganizationCacheOptions options) =>
        validator.Validate(Options.DefaultName, options).Succeeded;

    private static Dictionary<string, int> DefaultTtls() => new(StringComparer.Ordinal)
    {
        ["short"] = 30,
        ["standard"] = 120
    };
}
