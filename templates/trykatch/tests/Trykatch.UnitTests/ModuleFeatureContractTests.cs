using Trykatch.Modules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleFeatureContractTests
{
    [TestMethod]
    public void LegacyModuleHasNoFeatureDeclarationsByDefault()
    {
        ModuleDescriptor descriptor = Module("projects").Descriptor;

        descriptor.Features.ShouldBeEmpty();
        new ModuleCatalog([new StubModule(descriptor)]).Descriptors.Single().Features.ShouldBeEmpty();
    }

    [TestMethod]
    public void CatalogAcceptsDeclaredFeaturesWithExplicitTrueAndFalseDefaults()
    {
        ModuleDescriptor descriptor = Module("projects").Descriptor with
        {
            Features =
            [
                new("projects.reads", "Project reads", "View projects.", true),
                new("projects.writes", "Project writes", "Change projects.", false)
            ]
        };

        ModuleDescriptor installed = new ModuleCatalog([new StubModule(descriptor)]).Descriptors.Single();

        installed.Features.Select(feature => feature.DefaultEnabled).ShouldBe([true, false]);
    }

    [TestMethod]
    [DataRow("malformed-key")]
    [DataRow("foreign-owner")]
    [DataRow("duplicate-key")]
    [DataRow("null-entry")]
    [DataRow("too-many")]
    [DataRow("key-too-long")]
    [DataRow("blank-name")]
    [DataRow("name-too-long")]
    [DataRow("blank-description")]
    [DataRow("description-too-long")]
    public void CatalogRejectsInvalidFeatureDeclarations(string mutation)
    {
        ModuleFeatureDescriptor[] features = mutation switch
        {
            "malformed-key" => [new("Projects.Reads", "Reads", "View projects.", true)],
            "foreign-owner" => [new("reports.reads", "Reads", "View projects.", true)],
            "duplicate-key" => [new("projects.reads", "Reads", "View projects.", true), new("projects.reads", "Reads", "View projects.", false)],
            "null-entry" => [null!],
            "too-many" => Enumerable.Range(0, 101).Select(index => Feature($"projects.feature-{index}")).ToArray(),
            "key-too-long" => [Feature("projects." + new string('a', 120))],
            "blank-name" => [new("projects.reads", " ", "View projects.", true)],
            "name-too-long" => [new("projects.reads", new string('a', 161), "View projects.", true)],
            "blank-description" => [new("projects.reads", "Reads", " ", true)],
            "description-too-long" => [new("projects.reads", "Reads", new string('a', 1001), true)],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown feature mutation.")
        };
        ModuleDescriptor descriptor = Module("projects").Descriptor with { Features = features };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void CatalogAcceptsTheMaximumDeclaredFeatureCount()
    {
        ModuleDescriptor descriptor = Module("projects").Descriptor with
        {
            Features = Enumerable.Range(0, 100).Select(index => Feature($"projects.feature-{index}")).ToArray()
        };

        new ModuleCatalog([new StubModule(descriptor)]).Descriptors.Single().Features.Count.ShouldBe(100);
    }

    [TestMethod]
    public void CatalogRejectsNullFeatureMetadata()
    {
        ModuleDescriptor descriptor = Module("projects").Descriptor with { Features = null! };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void GeneratedManifestExpectationsAcceptLegacyModulesAndDeclarationReordering()
    {
        ModuleFeatureRegistrationRules.ValidateManifest([Module("legacy")], []);
        ModuleFeatureDescriptor reads = new("projects.reads", "Reads", "View projects.", true);
        ModuleFeatureDescriptor writes = new("projects.writes", "Writes", "Change projects.", false);
        ModuleDescriptor descriptor = Module("projects").Descriptor with { Features = [reads, writes] };

        ModuleFeatureRegistrationRules.ValidateManifest([new StubModule(descriptor)],
            [new("projects", writes), new("projects", reads)]);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("key")]
    [DataRow("name")]
    [DataRow("description")]
    [DataRow("default")]
    [DataRow("owner")]
    [DataRow("unknown-module")]
    [DataRow("duplicate")]
    [DataRow("null-entry")]
    [DataRow("null-feature")]
    public void GeneratedManifestExpectationsRejectDescriptorDrift(string mutation)
    {
        ModuleFeatureDescriptor feature = Feature("projects.reads");
        ModuleDescriptor descriptor = Module("projects").Descriptor with { Features = [feature] };
        InstalledModuleFeature expected = new("projects", feature);
        InstalledModuleFeature[] expectations = mutation switch
        {
            "missing" => [],
            "extra" => [expected, new("projects", Feature("projects.writes"))],
            "key" => [new("projects", feature with { Key = "projects.other" })],
            "name" => [new("projects", feature with { Name = "Changed name" })],
            "description" => [new("projects", feature with { Description = "Changed description." })],
            "default" => [new("projects", feature with { DefaultEnabled = !feature.DefaultEnabled })],
            "owner" => [new("reports", feature)],
            "unknown-module" => [expected, new("reports", Feature("reports.reads"))],
            "duplicate" => [expected, expected],
            "null-entry" => [null!],
            "null-feature" => [new("projects", null!)],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown manifest drift.")
        };

        Should.Throw<InvalidOperationException>(() =>
            ModuleFeatureRegistrationRules.ValidateManifest([new StubModule(descriptor)], expectations));
    }

    private static ModuleFeatureDescriptor Feature(string key) => new(key, "Feature", "Feature description.", false);

    private static StubModule Module(string id) => new(new ModuleDescriptor(
        id, id, "1.0.0", $"{id} test module.", [], [], ModuleCapabilities.Api, []));

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
