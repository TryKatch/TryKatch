using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class OrganizationQuotaGrantTests
{
    [TestMethod]
    public void BusinessResourceGrantDoesNotImplicitlyGrantQuotaAuthority()
    {
        ModuleDescriptor descriptor = Descriptor();

        ModuleCatalog catalog = new([new StubModule(descriptor)]);

        catalog.Descriptors.Single().SystemGrants.Single().AllowedQuotaResources.ShouldBeEmpty();
    }

    [TestMethod]
    public void ExplicitOwnedQuotaResourceCanBeGrantedSeparately()
    {
        ModuleDescriptor descriptor = Descriptor();
        ModuleSystemGrantDescriptor grant = descriptor.SystemGrants.Single() with
        {
            AllowedQuotaResources = ["documents.storage-bytes"]
        };

        ModuleCatalog catalog = new([new StubModule(descriptor with { SystemGrants = [grant] })]);

        catalog.Descriptors.Single().SystemGrants.Single().AllowedQuotaResources
            .ShouldBe(["documents.storage-bytes"]);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("foreign")]
    [DataRow("duplicate")]
    [DataRow("wildcard")]
    [DataRow("null-list")]
    [DataRow("null-key")]
    [DataRow("terminal-newline")]
    public void QuotaGrantRejectsUndeclaredOrUnboundedAuthority(string mutation)
    {
        ModuleDescriptor descriptor = Descriptor();
        IReadOnlyList<string> keys = mutation switch
        {
            "unknown" => ["documents.other"],
            "foreign" => ["projects.storage-bytes"],
            "duplicate" => ["documents.storage-bytes", "documents.storage-bytes"],
            "wildcard" => ["*"],
            "null-list" => null!,
            "null-key" => [null!],
            "terminal-newline" => ["documents.storage-bytes\n"],
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        ModuleSystemGrantDescriptor grant = descriptor.SystemGrants.Single() with { AllowedQuotaResources = keys };

        Should.Throw<InvalidOperationException>(() =>
            new ModuleCatalog([new StubModule(descriptor with { SystemGrants = [grant] })]));
    }

    private static ModuleDescriptor Descriptor() => new(
        "documents", "Documents", "1.0.0", "Quota grant tests.", [], [],
        ModuleCapabilities.Api | ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        QuotaResources = [new("documents.storage-bytes", "Document storage", "Logical uploaded bytes.", ModuleQuotaUnit.Bytes)],
        DataResources = [new("documents", "app", "documents", ModuleDataOwnership.Organization, IsolationPolicy: "documents-rls")],
        Operations = [new("documents", "documents.maintain", 1, ModuleExecutionKind.ScheduledSystem,
            "documents.maintain-payload", 1, [], "documents", "documents.maintenance", 1024)],
        SystemGrants = [new("documents.maintenance", 1, "documents",
            Guid.Parse("506c8ad7-4be0-45e7-8adc-ff7b5c343fd0"), ["documents.maintain"], ["documents"])]
    };

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
