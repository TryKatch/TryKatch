using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void QuotaWorkflowFactsAndBothRegistriesPreserveInstalledPolicyIdentities()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([QuotaWorkflowManifest().ToJsonString()]);
        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement resource = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("quotaResources")[0];
        resource.GetProperty("uploadPolicy").GetProperty("requiredPermission").GetString().ShouldBe("orders.read");
        resource.GetProperty("inventorySource").GetProperty("sourceVersion").GetInt32().ShouldBe(1);
        resource.GetProperty("removalPolicy").GetProperty("policyKey").GetString().ShouldBe("orders.removal");
        foreach (string filename in new[] { "GeneratedModules.cs", "GeneratedMigratorModules.cs" })
        {
            string generated = File.ReadAllText(Path.Combine(workspace.Root, filename));
            generated.ShouldContain("UploadPolicy = new(\"orders\", \"orders.upload\", 1, \"orders.read\")");
            generated.ShouldContain("InventorySource = new(\"orders\", \"orders.inventory\", 1)");
            generated.ShouldContain("RemovalPolicy = new(\"orders\", \"orders.removal\", 1)");
        }
        workspace.Tool.SetEnabled("orders", enabled: false, CancellationToken.None).IsHealthy.ShouldBeTrue();
        workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("quotaResources")[0]
            .GetProperty("uploadPolicy").GetProperty("policyKey").GetString().ShouldBe("orders.upload");
        File.ReadAllText(Path.Combine(workspace.Root, "GeneratedModules.cs")).ShouldNotContain("orders.upload");
    }

    [TestMethod]
    [DataRow("upload-key")]
    [DataRow("upload-version")]
    [DataRow("upload-permission")]
    [DataRow("foreign-permission")]
    [DataRow("count-upload")]
    [DataRow("inventory-key")]
    [DataRow("inventory-version")]
    [DataRow("removal-key")]
    [DataRow("removal-version")]
    [DataRow("duplicate-upload")]
    [DataRow("duplicate-inventory")]
    [DataRow("duplicate-removal")]
    [DataRow("missing-upload-key")]
    [DataRow("missing-inventory-version")]
    [DataRow("missing-removal-key")]
    public void QuotaWorkflowManifestRejectsInvalidOrAmbiguousInstalledReferences(string mutation)
    {
        JsonObject manifest = QuotaWorkflowManifest();
        JsonObject resource = manifest["quotaResources"]![0]!.AsObject();
        switch (mutation)
        {
            case "upload-key": resource["uploadPolicy"]!["policyKey"] = "other.upload"; break;
            case "upload-version": resource["uploadPolicy"]!["policyVersion"] = 0; break;
            case "upload-permission": resource["uploadPolicy"]!["requiredPermission"] = "orders.unknown"; break;
            case "foreign-permission": resource["uploadPolicy"]!["requiredPermission"] = "other.read"; break;
            case "count-upload": resource["unit"] = "count"; break;
            case "inventory-key": resource["inventorySource"]!["sourceKey"] = new string('a', 121); break;
            case "inventory-version": resource["inventorySource"]!["sourceVersion"] = 0; break;
            case "removal-key": resource["removalPolicy"]!["policyKey"] = "orders.removal\n"; break;
            case "removal-version": resource["removalPolicy"]!["policyVersion"] = 0; break;
            case "missing-upload-key": resource["uploadPolicy"]!.AsObject().Remove("policyKey"); break;
            case "missing-inventory-version": resource["inventorySource"]!.AsObject().Remove("sourceVersion"); break;
            case "missing-removal-key": resource["removalPolicy"]!.AsObject().Remove("policyKey"); break;
            default:
                JsonObject duplicate = QuotaResource("orders.other-bytes", "Other", "Other bytes.", "bytes");
                string property = mutation switch
                {
                    "duplicate-upload" => "uploadPolicy",
                    "duplicate-inventory" => "inventorySource",
                    "duplicate-removal" => "removalPolicy",
                    _ => throw new ArgumentOutOfRangeException(nameof(mutation))
                };
                duplicate[property] = resource[property]!.DeepClone();
                manifest["quotaResources"]!.AsArray().Add(duplicate);
                break;
        }
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);
        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeFalse();
    }

    [TestMethod]
    public void QuotaWorkflowOptionalReferencesAreClosedAndVersionedInPublishedSchema()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "schemas", "module-manifest.v1.schema.json")))
            directory = Directory.GetParent(directory)?.FullName;
        directory.ShouldNotBeNull();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!, "schemas", "module-manifest.v1.schema.json")));
        JsonElement definitions = schema.RootElement.GetProperty("$defs");
        JsonElement properties = definitions.GetProperty("moduleQuotaResource").GetProperty("properties");
        foreach ((string property, string definition) in new[]
                 { ("uploadPolicy", "quotaUploadPolicy"), ("inventorySource", "quotaInventorySource"), ("removalPolicy", "quotaRemovalPolicy") })
        {
            properties.GetProperty(property).GetProperty("$ref").GetString().ShouldBe("#/$defs/" + definition);
            definitions.GetProperty(definition).GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        }
    }

    private static JsonObject QuotaWorkflowManifest()
    {
        JsonObject resource = QuotaResource("orders.storage-bytes", "Storage", "Logical bytes.", "bytes");
        resource["uploadPolicy"] = new JsonObject
            { ["policyKey"] = "orders.upload", ["policyVersion"] = 1, ["requiredPermission"] = "orders.read" };
        resource["inventorySource"] = new JsonObject { ["sourceKey"] = "orders.inventory", ["sourceVersion"] = 1 };
        resource["removalPolicy"] = new JsonObject { ["policyKey"] = "orders.removal", ["policyVersion"] = 1 };
        return WithQuotaResources(Manifest("orders", includeExecution: false), resource);
    }
}
