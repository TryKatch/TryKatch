using Microsoft.Extensions.Configuration;
using Shouldly;
using Trykatch.Infrastructure.Features;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class FeaturePolicyOptionsTests
{
    [TestMethod]
    public void HostingEmptyParentMarkersPreserveTypedChildrenButNotScalarValues()
    {
        IConfiguration configuration = Configuration(("Features", ""), ("Features:Defaults", ""),
            ("Features:Defaults:projects.reads", "false"), ("Features:Disabled", ""),
            ("Features:Disabled:0", "projects.writes"));
        var settings = FeaturePolicyOptions.CreateSettings(configuration, "Development");
        settings.Defaults["projects.reads"].ShouldBeFalse();
        settings.Disabled.Contains("projects.writes").ShouldBeTrue();
        configuration["Features"] = "true";
        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(configuration, "Development"));
        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(Configuration(("Features", "")), "Development"));
    }

    [TestMethod]
    public void MissingConfigurationReturnsEmptyPolicyAndNormalizesEnvironment()
    {
        var settings = FeaturePolicyOptions.CreateSettings(Configuration(), "StAgInG");

        settings.Environment.ShouldBe("staging");
        settings.Disabled.ShouldBeEmpty();
        settings.Defaults.ShouldBeEmpty();
    }

    [TestMethod]
    public void AcceptsMaximumLengthEnvironmentName()
    {
        string environment = new('A', 64);

        FeaturePolicyOptions.CreateSettings(Configuration(), environment)
            .Environment.ShouldBe(new string('a', 64));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow(" stage")]
    [DataRow("stage\nblue")]
    [DataRow("stage\n")]
    [DataRow("stágé")]
    [DataRow(".stage")]
    public void RejectsMalformedEnvironmentName(string? environment)
    {
        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(Configuration(), environment!));
    }

    [TestMethod]
    public void RejectsEnvironmentNameLongerThan64Characters()
    {
        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(Configuration(), new string('a', 65)));
    }

    [TestMethod]
    [DataRow("Features:Disabled:0", "Invalid_Key")]
    [DataRow("Features:Disabled:0", "feature\nname")]
    [DataRow("Features:Disabled:0", "projects.reads\n")]
    [DataRow("Features:Defaults:Invalid_Key", "true")]
    [DataRow("Features:Defaults:feature\nname", "true")]
    [DataRow("Features:Defaults:projects.reads\n", "true")]
    public void RejectsMalformedFeatureKeys(string path, string value)
    {
        IConfiguration configuration = Configuration((path, value));

        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(configuration, "production"));
    }

    [TestMethod]
    public void RejectsUnknownFeatureConfigurationProperties()
    {
        IConfiguration configuration = Configuration(("Features:Enabled:0", "projects.reads"));

        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(configuration, "production"));
    }

    [TestMethod]
    public void RejectsMalformedDefaultBoolean()
    {
        IConfiguration configuration = Configuration(("Features:Defaults:projects.reads", "sometimes"));

        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(configuration, "production"));
    }

    [TestMethod]
    public void RejectsDuplicateDisabledFeatureKeys()
    {
        IConfiguration configuration = Configuration(
            ("Features:Disabled:0", "projects.reads"),
            ("Features:Disabled:1", "projects.reads"));

        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(configuration, "production"));
    }

    [TestMethod]
    public void ReturnsImmutableOrdinalCollections()
    {
        IConfiguration configuration = Configuration(
            ("Features:Disabled:0", "projects.reads"),
            ("Features:Defaults:projects.web", "true"),
            ("Features:Defaults:projects.api-v2", "false"));

        var settings = FeaturePolicyOptions.CreateSettings(configuration, "production");

        settings.Disabled.Contains("projects.reads").ShouldBeTrue();
        settings.Disabled.Contains("Projects.reads").ShouldBeFalse();
        settings.Defaults["projects.web"].ShouldBeTrue();
        settings.Defaults["projects.api-v2"].ShouldBeFalse();
        settings.Defaults.ContainsKey("Projects.web").ShouldBeFalse();
        if (settings.Disabled is ISet<string> mutableSet)
            Should.Throw<NotSupportedException>(() => mutableSet.Add("projects.writes"));
        if (settings.Defaults is IDictionary<string, bool> mutableDictionary)
            Should.Throw<NotSupportedException>(() => mutableDictionary.Add("projects.writes", true));
        settings.Disabled.Contains("projects.writes").ShouldBeFalse();
        settings.Defaults.ContainsKey("projects.writes").ShouldBeFalse();
    }

    [TestMethod]
    public void ReturnedSettingsDoNotTrackLaterConfigurationChanges()
    {
        IConfiguration configuration = Configuration(("Features:Defaults:projects.reads", "true"));

        var settings = FeaturePolicyOptions.CreateSettings(configuration, "production");
        configuration["Features:Defaults:projects.reads"] = "false";

        settings.Defaults["projects.reads"].ShouldBeTrue();
    }

    [TestMethod]
    public void RejectsMoreThanTenThousandEntriesInEitherCollection()
    {
        var disabledValues = Enumerable.Range(0, 10_001)
            .ToDictionary(index => $"Features:Disabled:{index}", index => (string?)$"projects.feature{index}");
        IConfiguration disabledConfiguration = new ConfigurationBuilder().AddInMemoryCollection(disabledValues).Build();
        var defaultValues = Enumerable.Range(0, 10_001)
            .ToDictionary(index => $"Features:Defaults:projects.feature{index}", _ => (string?)"true");
        IConfiguration defaultsConfiguration = new ConfigurationBuilder().AddInMemoryCollection(defaultValues).Build();

        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(disabledConfiguration, "production"));
        Should.Throw<InvalidOperationException>(() => FeaturePolicyOptions.CreateSettings(defaultsConfiguration, "production"));
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => (string?)value.Value))
            .Build();
}
