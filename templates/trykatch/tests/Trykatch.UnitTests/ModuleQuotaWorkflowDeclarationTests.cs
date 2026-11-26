using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleQuotaWorkflowDeclarationTests
{
    [TestMethod]
    public void ResourcesWithoutWorkflowDeclarationsAndValidWorkflowDeclarationsRemainValid()
    {
        ModuleDescriptor module = Descriptor([
            Resource("documents.storage-bytes"),
            Resource("documents.exports-bytes") with
            {
                UploadPolicy = UploadPolicy(),
                InventorySource = InventorySource(),
                RemovalPolicy = RemovalPolicy()
            }
        ], [Permission("documents.manage")]);

        ModuleQuotaRegistrationRules.Validate([module]);
    }

    [TestMethod]
    public void WorkflowReferencesRequireMatchingOwnerStableKeyAndPositiveVersion()
    {
        ModuleQuotaResourceDescriptor[] invalidResources =
        [
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(owner: "billing")
            },
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(key: "")
            },
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(key: "billing.upload")
            },
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(key: "documents." + new string('x', 120))
            },
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(version: 0)
            },
            Resource("documents.storage-bytes") with
            {
                UploadPolicy = UploadPolicy(owner: null!)
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(owner: "billing")
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(key: "")
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(key: "documents.inventory_bad")
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(key: "billing.inventory")
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(key: "documents." + new string('x', 120))
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(version: 0)
            },
            Resource("documents.storage-bytes") with
            {
                InventorySource = InventorySource(owner: null!)
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(owner: "billing")
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(key: "")
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(key: "documents.bad_key")
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(key: "billing.removal")
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(key: "documents." + new string('x', 120))
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(version: 0)
            },
            Resource("documents.storage-bytes") with
            {
                RemovalPolicy = RemovalPolicy(owner: null!)
            }
        ];

        foreach (ModuleQuotaResourceDescriptor resource in invalidResources)
            Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate(
                [Descriptor(resource, [Permission("documents.manage")])]));
    }

    [TestMethod]
    public void UploadPoliciesRequireBytesAndAnOwnedDeclaredPermission()
    {
        ModuleQuotaResourceDescriptor countUpload = Resource("documents.upload-count") with
        {
            Unit = ModuleQuotaUnit.Count,
            UploadPolicy = UploadPolicy()
        };
        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate([Descriptor(countUpload,
            [Permission("documents.manage")])]));

        ModuleQuotaResourceDescriptor undeclared = Resource("documents.storage-bytes") with
        {
            UploadPolicy = UploadPolicy(permission: "documents.manage")
        };
        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate([Descriptor(undeclared)]));

        ModuleQuotaResourceDescriptor foreign = Resource("documents.storage-bytes") with
        {
            UploadPolicy = UploadPolicy(permission: "billing.manage")
        };
        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate([Descriptor(foreign,
            [Permission("billing.manage")])]));

        ModuleQuotaRegistrationRules.Validate([Descriptor(undeclared, [Permission("documents.manage")])]);
    }

    [TestMethod]
    public void UploadPolicyInventorySourceAndRemovalPolicyIdentitiesAreUniquePerModule()
    {
        ModuleQuotaResourceDescriptor[] duplicateUpload =
        [
            Resource("documents.storage-bytes") with { UploadPolicy = UploadPolicy() },
            Resource("documents.exports-bytes") with { UploadPolicy = UploadPolicy() }
        ];
        ModuleQuotaResourceDescriptor[] duplicateInventory =
        [
            Resource("documents.storage-bytes") with { InventorySource = InventorySource() },
            Resource("documents.exports-bytes") with { InventorySource = InventorySource() }
        ];
        ModuleQuotaResourceDescriptor[] duplicateRemoval =
        [
            Resource("documents.storage-bytes") with { RemovalPolicy = RemovalPolicy() },
            Resource("documents.exports-bytes") with { RemovalPolicy = RemovalPolicy() }
        ];

        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate(
            [Descriptor(duplicateUpload, [Permission("documents.manage")])]));
        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate([Descriptor(duplicateInventory)]));
        Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.Validate([Descriptor(duplicateRemoval)]));
    }

    [TestMethod]
    public void ManifestExpectationsDetectOptionalWorkflowReferenceDrift()
    {
        ModuleQuotaResourceDescriptor declared = Resource("documents.storage-bytes") with
        {
            UploadPolicy = UploadPolicy(),
            InventorySource = InventorySource(),
            RemovalPolicy = RemovalPolicy()
        };
        IModule module = new StubModule(Descriptor(declared, [Permission("documents.manage")]));
        ModuleQuotaRegistrationRules.ValidateManifest(
            [module], [new InstalledModuleQuotaResource("documents", declared)]);
        ModuleQuotaResourceDescriptor[] drifted =
        [
            declared with { UploadPolicy = UploadPolicy(key: "documents.other-upload") },
            declared with { InventorySource = InventorySource(key: "documents.other-source") },
            declared with { RemovalPolicy = RemovalPolicy(key: "documents.other-removal") }
        ];

        foreach (ModuleQuotaResourceDescriptor expectedResource in drifted)
        {
            Should.Throw<InvalidOperationException>(() => ModuleQuotaRegistrationRules.ValidateManifest(
                [module], [new InstalledModuleQuotaResource("documents", expectedResource)]));
        }
    }

    private static ModuleQuotaResourceDescriptor Resource(string key) =>
        new(key, "Document storage", "Logical uploaded bytes.", ModuleQuotaUnit.Bytes);

    private static ModuleDescriptor Descriptor(ModuleQuotaResourceDescriptor resource,
        IReadOnlyList<PermissionDescriptor>? permissions = null) => Descriptor([resource], permissions);

    private static ModuleDescriptor Descriptor(ModuleQuotaResourceDescriptor[] resources,
        IReadOnlyList<PermissionDescriptor>? permissions = null) => new(
            "documents", "Documents", "1.0.0", "Documents test module.", [], [], ModuleCapabilities.Api, [])
        {
            QuotaResources = resources,
            Permissions = permissions ?? []
        };

    private static PermissionDescriptor Permission(string key) => new(key, "Manage documents", "Manage documents.");

    private static ModuleUploadPolicyDescriptor UploadPolicy(string owner = "documents", string key = "documents.upload",
        int version = 1, string permission = "documents.manage") => new(owner, key, version, permission);

    private static ModuleQuotaInventorySourceRef InventorySource(string owner = "documents",
        string key = "documents.inventory", int version = 1) => new(owner, key, version);

    private static ModuleUsageRemovalPolicyRef RemovalPolicy(string owner = "documents",
        string key = "documents.removal", int version = 1) => new(owner, key, version);

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
