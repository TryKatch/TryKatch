using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Quotas;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class QuotaWorkflowRegistryTests
{
    [TestMethod]
    public void LegacyResourcesNeedNoBindingsAndDoNotResolveWorkflows()
    {
        ModuleDescriptor descriptor = Descriptor(Resource("documents.storage-bytes"));
        QuotaWorkflowRegistry registry = new(new ModuleCatalog([new StubModule(descriptor)]), [], []);

        registry.TryGetUpload(UploadRef(), out InstalledModuleQuotaResource? upload).ShouldBeFalse();
        upload.ShouldBeNull();
        registry.TryGetInventory("documents.storage-bytes", out ModuleQuotaInventorySourceBinding? inventory)
            .ShouldBeFalse();
        inventory.ShouldBeNull();
        registry.TryGetRemoval("documents.storage-bytes", out ModuleUsageRemovalPolicyBinding? removal)
            .ShouldBeFalse();
        removal.ShouldBeNull();
    }

    [TestMethod]
    public void ResolvesOnlyExactInstalledUploadInventoryAndRemovalDeclarations()
    {
        ModuleQuotaResourceDescriptor declared = Resource("documents.storage-bytes") with
        {
            UploadPolicy = new("documents", "documents.upload", 3, "documents.manage"),
            InventorySource = new("documents", "documents.inventory", 4),
            RemovalPolicy = new("documents", "documents.removal", 5)
        };
        ModuleQuotaResourceDescriptor second = Resource("documents.exports-bytes");
        ModuleDescriptor descriptor = Descriptor([declared, second]);
        MutableInventorySource inventoryAdapter = new();
        MutableRemovalPolicy removalAdapter = new();
        ModuleQuotaInventorySourceBinding inventoryBinding = new(declared.InventorySource!, inventoryAdapter);
        ModuleUsageRemovalPolicyBinding removalBinding = new(declared.RemovalPolicy!, removalAdapter);
        QuotaWorkflowRegistry registry = new(new ModuleCatalog([new StubModule(descriptor)]),
            [inventoryBinding], [removalBinding]);

        registry.TryGetUpload(new("documents", "documents.upload", 3),
            out InstalledModuleQuotaResource? upload).ShouldBeTrue();
        upload.ShouldBe(new InstalledModuleQuotaResource("documents", declared));
        registry.TryGetUpload(new("documents", "documents.upload", 2), out _).ShouldBeFalse();
        registry.TryGetUpload(new("billing", "documents.upload", 3), out _).ShouldBeFalse();

        registry.TryGetInventory("documents.storage-bytes", out ModuleQuotaInventorySourceBinding? resolvedInventory)
            .ShouldBeTrue();
        resolvedInventory.ShouldBeSameAs(inventoryBinding);
        registry.TryGetInventory("documents.exports-bytes", out _).ShouldBeFalse();
        registry.TryGetInventory("documents.unknown-bytes", out _).ShouldBeFalse();

        registry.TryGetRemoval("documents.storage-bytes", out ModuleUsageRemovalPolicyBinding? resolvedRemoval)
            .ShouldBeTrue();
        resolvedRemoval.ShouldBeSameAs(removalBinding);
        registry.TryGetRemoval("documents.exports-bytes", out _).ShouldBeFalse();
        registry.TryGetRemoval("documents.unknown-bytes", out _).ShouldBeFalse();
    }

    [TestMethod]
    public void RejectsMissingNullDuplicateAndMismatchedInventoryBindingsAtConstruction()
    {
        ModuleQuotaInventorySourceRef declared = new("documents", "documents.inventory", 1);
        ModuleCatalog catalog = Catalog(Resource("documents.storage-bytes") with { InventorySource = declared });
        MutableInventorySource adapter = new();

        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [null!], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [new(declared, null!)], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [new(declared with { SourceVersion = 2 }, adapter)], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [new(declared with { OwnerModuleId = "billing" }, adapter)], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [new(declared with { SourceKey = "documents.other" }, adapter)], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog,
            [new(declared, adapter), new(declared, new MutableInventorySource())], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(Catalog(Resource(
                "documents.storage-bytes")), [new(declared, adapter)], []));
    }

    [TestMethod]
    public void RejectsMissingNullDuplicateAndMismatchedRemovalBindingsAtConstruction()
    {
        ModuleUsageRemovalPolicyRef declared = new("documents", "documents.removal", 1);
        ModuleCatalog catalog = Catalog(Resource("documents.storage-bytes") with { RemovalPolicy = declared });
        MutableRemovalPolicy adapter = new();

        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [], []));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [], [null!]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [],
            [new(declared, null!)]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [],
            [new(declared with { PolicyVersion = 2 }, adapter)]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [],
            [new(declared with { OwnerModuleId = "billing" }, adapter)]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [],
            [new(declared with { PolicyKey = "documents.other" }, adapter)]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(catalog, [],
            [new(declared, adapter), new(declared, new MutableRemovalPolicy())]));
        Should.Throw<InvalidOperationException>(() => new QuotaWorkflowRegistry(Catalog(Resource(
                "documents.storage-bytes")), [], [new(declared, adapter)]));
    }

    [TestMethod]
    public void UploadLookupDoesNotResolveDisabledOrVersionDriftedDeclarations()
    {
        ModuleQuotaResourceDescriptor declared = Resource("documents.storage-bytes") with
        {
            UploadPolicy = new("documents", "documents.upload", 2, "documents.manage")
        };
        QuotaWorkflowRegistry installed = new(Catalog(declared), [], []);
        installed.TryGetUpload(new("documents", "documents.upload", 2), out _).ShouldBeTrue();

        QuotaWorkflowRegistry ownerNotInstalled = new(new ModuleCatalog([]), [], []);
        ownerNotInstalled.TryGetUpload(new("documents", "documents.upload", 2), out _).ShouldBeFalse();
        ownerNotInstalled.TryGetUpload(new("documents", "documents.upload", 1), out _).ShouldBeFalse();
    }

    private static ModuleCatalog Catalog(params ModuleQuotaResourceDescriptor[] resources) =>
        new([new StubModule(Descriptor(resources))]);

    private static ModuleDescriptor Descriptor(params ModuleQuotaResourceDescriptor[] resources) => new(
        "documents", "Documents", "1.0.0", "Documents quota fixture.", [], [], ModuleCapabilities.Api, [])
    {
        QuotaResources = resources,
        Permissions = [new("documents.manage", "Manage documents", "Manage documents.")]
    };

    private static ModuleQuotaResourceDescriptor Resource(string key) =>
        new(key, "Document storage", "Logical uploaded bytes.", ModuleQuotaUnit.Bytes);

    private static ModuleUploadPolicyRef UploadRef() => new("documents", "documents.upload", 1);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class MutableInventorySource : IModuleQuotaInventorySource
    {
        public Task<ModuleQuotaInventoryPage> ReadPageAsync(string? cursor, int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ModuleQuotaInventoryPage([], null, true));
    }

    private sealed class MutableRemovalPolicy : IModuleUsageRemovalPolicy
    {
        public Task<bool> ConfirmAsync(ModuleUsageRemovalContext context,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
