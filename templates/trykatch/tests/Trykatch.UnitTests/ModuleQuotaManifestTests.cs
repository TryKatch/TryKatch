using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void LegacyManifestWithoutQuotaResourcesRemainsValidAndFactsExposeEmptyQuotaMetadata()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders", includeExecution: false)]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement module = workspace.Tool.Facts("orders").GetProperty("modules")[0];
        module.GetProperty("quotaResources").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorFactsAndGeneratedRegistriesPreserveQuotaOwnerUnitAndMetadata()
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders", includeExecution: false),
            QuotaResource("orders.storage-bytes", "Document storage", "Logical uploaded bytes.", "bytes"),
            QuotaResource("orders.seats", "Seats", "Licensed seats.", "count"));
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement module = workspace.Tool.Facts("orders").GetProperty("modules")[0];
        module.GetProperty("id").GetString().ShouldBe("orders");
        JsonElement resources = module.GetProperty("quotaResources");
        resources.GetArrayLength().ShouldBe(2);
        resources[0].GetProperty("key").GetString().ShouldBe("orders.storage-bytes");
        resources[0].GetProperty("name").GetString().ShouldBe("Document storage");
        resources[0].GetProperty("description").GetString().ShouldBe("Logical uploaded bytes.");
        resources[0].GetProperty("unit").GetString().ShouldBe("bytes");
        resources[1].GetProperty("key").GetString().ShouldBe("orders.seats");
        resources[1].GetProperty("unit").GetString().ShouldBe("count");

        string backend = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedModules.cs"));
        string migrator = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedMigratorModules.cs"));
        const string storageDeclaration = "new(\"orders\", new(\"orders.storage-bytes\", \"Document storage\", \"Logical uploaded bytes.\", ModuleQuotaUnit.Bytes))";
        const string seatsDeclaration = "new(\"orders\", new(\"orders.seats\", \"Seats\", \"Licensed seats.\", ModuleQuotaUnit.Count))";
        backend.ShouldContain(storageDeclaration);
        backend.ShouldContain(seatsDeclaration);
        migrator.ShouldContain(storageDeclaration);
        migrator.ShouldContain(seatsDeclaration);
    }

    [TestMethod]
    public void DisablingQuotaOwnerRemovesRuntimeRegistryEntryButRetainsItsDeclarationInFacts()
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders", includeExecution: false),
            QuotaResource("orders.storage-bytes", "Document storage", "Logical uploaded bytes.", "bytes"));
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);
        workspace.Tool.Generate().IsHealthy.ShouldBeTrue();

        ModuleDoctorReport report = workspace.Tool.SetEnabled("orders", enabled: false, CancellationToken.None);

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement module = workspace.Tool.Facts("orders").GetProperty("modules")[0];
        module.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        module.GetProperty("quotaResources").GetArrayLength().ShouldBe(1);
        string backend = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedModules.cs"));
        string migrator = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedMigratorModules.cs"));
        backend.ShouldNotContain("new(\"orders\", new(\"orders.storage-bytes\"");
        migrator.ShouldNotContain("new(\"orders\", new(\"orders.storage-bytes\"");
        backend.ShouldNotContain("new OrdersModule()");
        migrator.ShouldNotContain("new OrdersModule()");
    }

    [TestMethod]
    [DataRow("malformed-key")]
    [DataRow("foreign-owner")]
    [DataRow("duplicate-key")]
    [DataRow("unknown-unit")]
    [DataRow("zero-unit")]
    [DataRow("blank-name")]
    [DataRow("name-too-long")]
    [DataRow("blank-description")]
    [DataRow("description-too-long")]
    [DataRow("too-many")]
    [DataRow("null-list")]
    [DataRow("null-entry")]
    [DataRow("key-too-long")]
    [DataRow("terminal-newline")]
    public void DoctorRejectsInvalidQuotaResourceManifestDeclarations(string mutation)
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders", includeExecution: false),
            QuotaResource("orders.storage-bytes", "Document storage", "Logical uploaded bytes.", "bytes"));
        ApplyQuotaMutation(manifest, mutation);
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("quota", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DoctorRejectsGeneratedQuotaRegistryMetadataDrift()
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders", includeExecution: false),
            QuotaResource("orders.storage-bytes", "Document storage", "Logical uploaded bytes.", "bytes"));
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);
        workspace.Tool.Generate().IsHealthy.ShouldBeTrue();
        string backendPath = Path.Combine(workspace.Root, "GeneratedModules.cs");
        File.WriteAllText(backendPath, File.ReadAllText(backendPath).Replace(
            "ModuleQuotaUnit.Bytes", "ModuleQuotaUnit.Count", StringComparison.Ordinal));

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("generated backend module registry", StringComparison.OrdinalIgnoreCase)
            && error.Contains("drifted", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void RenamedBackendOnlyQuotaRegistryUsesConfiguredContractNamespace()
    {
        JsonObject manifest = WithQuotaResources(Manifest("orders", includeExecution: false),
            QuotaResource("orders.storage-bytes", "Storage", "Logical bytes.", "bytes"));
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        JsonObject catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        catalog["outputs"]!["backendNamespace"] = "Horizon.Api.Modules";
        catalog["outputs"]!["migratorNamespace"] = "Horizon.Migrator.Modules";
        catalog["outputs"]!["dotnetModuleContractNamespace"] = "Horizon.Modules";
        File.WriteAllText(catalogPath, catalog.ToJsonString());

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        foreach (string filename in new[] { "GeneratedModules.cs", "GeneratedMigratorModules.cs" })
        {
            string generated = File.ReadAllText(Path.Combine(workspace.Root, filename));
            generated.ShouldContain("using Horizon.Modules;");
            generated.ShouldContain("ModuleQuotaRegistrationRules.ValidateManifest(All,");
            generated.ShouldContain("ModuleQuotaUnit.Bytes");
        }
    }

    [TestMethod]
    public void PublishedManifestSchemaIncludesBoundedClosedQuotaResourcesAndExplicitGrantList()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "schemas", "module-manifest.v1.schema.json")))
            directory = Directory.GetParent(directory)?.FullName;
        directory.ShouldNotBeNull();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!, "schemas", "module-manifest.v1.schema.json")));
        JsonElement root = schema.RootElement;
        root.GetProperty("properties").GetProperty("quotaResources").GetProperty("maxItems").GetInt32().ShouldBe(100);
        JsonElement resource = root.GetProperty("$defs").GetProperty("moduleQuotaResource");
        resource.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        resource.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["key", "name", "description", "unit"]);
        resource.GetProperty("properties").GetProperty("unit").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()).ShouldBe(["bytes", "count"]);
        JsonElement grants = root.GetProperty("$defs").GetProperty("systemGrant")
            .GetProperty("properties").GetProperty("allowedQuotaResources");
        grants.GetProperty("maxItems").GetInt32().ShouldBe(100);
        grants.GetProperty("uniqueItems").GetBoolean().ShouldBeTrue();
    }

    private static JsonObject WithQuotaResources(string source, params JsonObject[] resources)
    {
        JsonObject manifest = JsonNode.Parse(source)!.AsObject();
        manifest["quotaResources"] = new JsonArray(resources.Select(resource => (JsonNode?)resource.DeepClone()).ToArray());
        return manifest;
    }

    private static JsonObject QuotaResource(string key, string name, string description, string unit) => new()
    {
        ["key"] = key,
        ["name"] = name,
        ["description"] = description,
        ["unit"] = unit
    };

    private static void ApplyQuotaMutation(JsonObject manifest, string mutation)
    {
        JsonArray resources = manifest["quotaResources"]!.AsArray();
        JsonObject resource = resources[0]!.AsObject();
        switch (mutation)
        {
            case "malformed-key": resource["key"] = "Orders.storage-bytes"; break;
            case "foreign-owner": resource["key"] = "reports.storage-bytes"; break;
            case "duplicate-key": resources.Add(resource.DeepClone()); break;
            case "unknown-unit": resource["unit"] = "gigabytes"; break;
            case "zero-unit": resource["unit"] = "0"; break;
            case "null-list": manifest["quotaResources"] = null; break;
            case "null-entry": resources[0] = null; break;
            case "key-too-long": resource["key"] = "orders." + new string('a', 114); break;
            case "terminal-newline": resource["key"] = "orders.storage-bytes\n"; break;
            case "blank-name": resource["name"] = " "; break;
            case "name-too-long": resource["name"] = new string('n', 161); break;
            case "blank-description": resource["description"] = " "; break;
            case "description-too-long": resource["description"] = new string('d', 1001); break;
            case "too-many":
                manifest["quotaResources"] = new JsonArray(Enumerable.Range(0, 101)
                    .Select(index => (JsonNode?)QuotaResource($"orders.resource-{index}", "Resource", "Description.", "count")).ToArray());
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown quota manifest mutation.");
        }
    }
}
