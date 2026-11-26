using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void ExplicitQuotaGrantMetadataIsValidatedAndIncludedInFacts()
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders"),
            QuotaResource("orders.storage-bytes", "Storage", "Logical bytes.", "bytes"));
        manifest["execution"]!["systemGrants"]![0]!["allowedQuotaResources"] = new JsonArray("orders.storage-bytes");
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement grants = workspace.Tool.Facts("orders").GetProperty("modules")[0]
            .GetProperty("execution").GetProperty("systemGrants");
        grants[0].GetProperty("allowedQuotaResources")[0].GetString().ShouldBe("orders.storage-bytes");
        grants[1].GetProperty("allowedQuotaResources").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    [DataRow("unknown")]
    [DataRow("foreign")]
    [DataRow("duplicate")]
    [DataRow("wildcard")]
    [DataRow("null-list")]
    [DataRow("null-key")]
    public void ManifestQuotaGrantRejectsUndeclaredOrUnboundedAuthority(string mutation)
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders"),
            QuotaResource("orders.storage-bytes", "Storage", "Logical bytes.", "bytes"));
        manifest["execution"]!["systemGrants"]![0]!["allowedQuotaResources"] = mutation switch
        {
            "unknown" => new JsonArray("orders.other"),
            "foreign" => new JsonArray("documents.storage-bytes"),
            "duplicate" => new JsonArray("orders.storage-bytes", "orders.storage-bytes"),
            "wildcard" => new JsonArray("*"),
            "null-list" => null,
            "null-key" => new JsonArray((JsonNode?)null),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation))
        };
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("quota", StringComparison.OrdinalIgnoreCase));
    }
}
