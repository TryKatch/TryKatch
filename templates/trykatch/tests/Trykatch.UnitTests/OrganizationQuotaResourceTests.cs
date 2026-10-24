using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaResourceTests
{
    [TestMethod]
    public void ExistingModulesWithoutQuotaDeclarationsRemainValid()
    {
        StubModule module = Module("legacy");

        ModuleCatalog catalog = new([module]);
        catalog.Descriptors.Single().QuotaResources.ShouldBeEmpty();
        ModuleQuotaRegistrationRules.ValidateManifest([module], []);
    }

    [TestMethod]
    public void CatalogAcceptsDeclaredBoundsAndOneHundredQuotaResources()
    {
        ModuleQuotaResourceDescriptor[] resources = Enumerable.Range(0, 100)
            .Select(index => Resource($"projects.resource-{index}"))
            .ToArray();
        resources[0] = Resource("projects." + new string('a', 111)) with
        {
            Name = new string('N', 160),
            Description = new string('d', 1000),
            Unit = ModuleQuotaUnit.Count
        };
        ModuleDescriptor descriptor = Module("projects").Descriptor with { QuotaResources = resources };

        ModuleDescriptor installed = new ModuleCatalog([new StubModule(descriptor)]).Descriptors.Single();

        installed.QuotaResources.Count.ShouldBe(100);
        installed.QuotaResources[0].Key.Length.ShouldBe(120);
        installed.QuotaResources[0].Name.Length.ShouldBe(160);
        installed.QuotaResources[0].Description.Length.ShouldBe(1000);
        installed.QuotaResources[0].Unit.ShouldBe(ModuleQuotaUnit.Count);
    }

    [TestMethod]
    [DataRow("malformed-key")]
    [DataRow("foreign-owner")]
    [DataRow("duplicate-key")]
    [DataRow("key-too-long")]
    [DataRow("blank-name")]
    [DataRow("name-too-long")]
    [DataRow("blank-description")]
    [DataRow("description-too-long")]
    [DataRow("zero-unit")]
    [DataRow("unknown-unit")]
    [DataRow("too-many")]
    public void CatalogRejectsInvalidQuotaResourceDeclarations(string mutation)
    {
        ModuleQuotaResourceDescriptor[] resources = [Resource("projects.storage-bytes")];
        resources = mutation switch
        {
            "malformed-key" => [Resource("projects.storage_bytes")],
            "foreign-owner" => [Resource("documents.storage-bytes")],
            "duplicate-key" => [Resource("projects.storage-bytes"), Resource("projects.storage-bytes")],
            "key-too-long" => [Resource("projects." + new string('a', 112))],
            "blank-name" => [Resource("projects.storage-bytes") with { Name = " " }],
            "name-too-long" => [Resource("projects.storage-bytes") with { Name = new string('n', 161) }],
            "blank-description" => [Resource("projects.storage-bytes") with { Description = " " }],
            "description-too-long" => [Resource("projects.storage-bytes") with { Description = new string('d', 1001) }],
            "zero-unit" => [Resource("projects.storage-bytes") with { Unit = (ModuleQuotaUnit)0 }],
            "unknown-unit" => [Resource("projects.storage-bytes") with { Unit = (ModuleQuotaUnit)999 }],
            "too-many" => Enumerable.Range(0, 101).Select(index => Resource($"projects.resource-{index}")).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown quota resource mutation.")
        };
        ModuleDescriptor descriptor = Module("projects").Descriptor with { QuotaResources = resources };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void GeneratedQuotaManifestExpectationsAcceptDeclaredResourcesAndReordering()
    {
        ModuleQuotaResourceDescriptor storage = Resource("projects.storage-bytes");
        ModuleQuotaResourceDescriptor seats = Resource("projects.seats") with { Unit = ModuleQuotaUnit.Count };
        StubModule module = new(Module("projects").Descriptor with { QuotaResources = [storage, seats] });

        ModuleQuotaRegistrationRules.ValidateManifest(
            [module],
            [new("projects", seats), new("projects", storage)]);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("key")]
    [DataRow("name")]
    [DataRow("description")]
    [DataRow("unit")]
    [DataRow("owner")]
    [DataRow("unknown-module")]
    [DataRow("duplicate")]
    public void GeneratedQuotaManifestExpectationsRejectDescriptorDrift(string mutation)
    {
        ModuleQuotaResourceDescriptor resource = Resource("projects.storage-bytes");
        StubModule module = new(Module("projects").Descriptor with { QuotaResources = [resource] });
        InstalledModuleQuotaResource expected = new("projects", resource);
        InstalledModuleQuotaResource[] expectations = mutation switch
        {
            "missing" => [],
            "extra" => [expected, new("projects", Resource("projects.seats"))],
            "key" => [new("projects", resource with { Key = "projects.other" })],
            "name" => [new("projects", resource with { Name = "Changed name" })],
            "description" => [new("projects", resource with { Description = "Changed description." })],
            "unit" => [new("projects", resource with { Unit = ModuleQuotaUnit.Count })],
            "owner" => [new("documents", resource)],
            "unknown-module" => [expected, new("reports", Resource("reports.exports"))],
            "duplicate" => [expected, expected],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown quota manifest drift.")
        };

        Should.Throw<InvalidOperationException>(() =>
            ModuleQuotaRegistrationRules.ValidateManifest([module], expectations));
    }

    private static ModuleQuotaResourceDescriptor Resource(string key) =>
        new(key, "Storage", "Logical uploaded bytes.", ModuleQuotaUnit.Bytes);

    private static StubModule Module(string id) => new(new ModuleDescriptor(
        id, id, "1.0.0", $"{id} test module.", [], [], ModuleCapabilities.Api, []));

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
