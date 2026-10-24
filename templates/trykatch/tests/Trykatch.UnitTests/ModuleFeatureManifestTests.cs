using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void ManifestWithoutFeaturesRemainsValidAndFactsExposeAnEmptyDeclarationList()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders", includeExecution: false)]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement module = workspace.Tool.Facts("orders").GetProperty("modules")[0];
        module.GetProperty("features").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorAndFactsExposeValidFeatureDeclarationsAndDefaultsWithoutChangingCapabilities()
    {
        JsonObject manifest = WithFeatures(Manifest("orders", includeExecution: false), [
            Feature("orders.reads", "Order reads", "View orders.", true),
            Feature("orders.writes", "Order writes", "Change orders.", false)
        ]);
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement module = workspace.Tool.Facts("orders").GetProperty("modules")[0];
        JsonElement features = module.GetProperty("features");
        features.GetArrayLength().ShouldBe(2);
        features[0].GetProperty("key").GetString().ShouldBe("orders.reads");
        features[0].GetProperty("name").GetString().ShouldBe("Order reads");
        features[0].GetProperty("description").GetString().ShouldBe("View orders.");
        features[0].GetProperty("defaultEnabled").GetBoolean().ShouldBeTrue();
        features[1].GetProperty("key").GetString().ShouldBe("orders.writes");
        features[1].GetProperty("defaultEnabled").GetBoolean().ShouldBeFalse();
        module.GetProperty("capabilities").GetArrayLength().ShouldBe(3);

        string backend = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedModules.cs"));
        string migrator = File.ReadAllText(Path.Combine(workspace.Root, "GeneratedMigratorModules.cs"));
        backend.ShouldContain("ModuleFeatureRegistrationRules.ValidateManifest(All,");
        backend.ShouldContain("new(\"orders\", new(\"orders.reads\", \"Order reads\", \"View orders.\", true))");
        migrator.ShouldContain("ModuleFeatureRegistrationRules.ValidateManifest(All,");
        migrator.ShouldContain("new(\"orders\", new(\"orders.writes\", \"Order writes\", \"Change orders.\", false))");
    }

    [TestMethod]
    [DataRow("missing-default")]
    [DataRow("nonboolean-default")]
    [DataRow("unknown-property")]
    [DataRow("malformed-key")]
    [DataRow("trailing-newline-key")]
    [DataRow("key-too-long")]
    [DataRow("foreign-owner")]
    [DataRow("duplicate-key")]
    [DataRow("null-feature-list")]
    [DataRow("null-feature")]
    [DataRow("too-many")]
    [DataRow("blank-name")]
    [DataRow("name-too-long")]
    [DataRow("blank-description")]
    [DataRow("description-too-long")]
    public void DoctorRejectsInvalidFeatureManifestDeclarations(string mutation)
    {
        JsonObject manifest = WithFeatures(Manifest("orders", includeExecution: false), [
            Feature("orders.reads", "Order reads", "View orders.", true)
        ]);
        ApplyFeatureMutation(manifest, mutation);
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeFalse();
        if (mutation is "malformed-key" or "trailing-newline-key" or "key-too-long" or "foreign-owner" or "duplicate-key"
            or "null-feature-list" or "null-feature" or "too-many" or "blank-name" or "name-too-long"
            or "blank-description" or "description-too-long")
            report.Errors.ShouldContain(error => error.Contains("feature", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ManifestSchemaDefinesClosedFeatureDeclarationShapeAndBounds()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "schemas", "module-manifest.v1.schema.json")))
            directory = Directory.GetParent(directory)?.FullName;
        directory.ShouldNotBeNull();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!, "schemas", "module-manifest.v1.schema.json")));

        JsonElement root = schema.RootElement;
        JsonElement featureList = root.GetProperty("properties").GetProperty("features");
        featureList.GetProperty("maxItems").GetInt32().ShouldBe(100);
        JsonElement feature = root.GetProperty("$defs").GetProperty("moduleFeature");
        feature.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        feature.GetProperty("required").EnumerateArray().Select(value => value.GetString())
            .ShouldBe(["key", "name", "description", "defaultEnabled"]);
        string featureKeyReference = feature.GetProperty("properties").GetProperty("key").GetProperty("$ref").GetString()!;
        featureKeyReference.ShouldBe("#/$defs/stableKey");
        JsonElement featureKey = root.GetProperty("$defs").GetProperty(featureKeyReference.Split('/').Last());
        featureKey.GetProperty("maxLength").GetInt32().ShouldBe(120);
        feature.GetProperty("properties").GetProperty("name").GetProperty("maxLength").GetInt32().ShouldBe(160);
        feature.GetProperty("properties").GetProperty("description").GetProperty("maxLength").GetInt32().ShouldBe(1000);
        feature.GetProperty("properties").GetProperty("defaultEnabled").GetProperty("type").GetString().ShouldBe("boolean");
    }

    private static JsonObject WithFeatures(string source, IEnumerable<JsonNode?> features)
    {
        JsonObject manifest = JsonNode.Parse(source)!.AsObject();
        manifest["features"] = new JsonArray(features.Select(feature => feature?.DeepClone()).ToArray());
        return manifest;
    }

    private static JsonObject Feature(string key, string name, string description, bool defaultEnabled) => new()
    {
        ["key"] = key,
        ["name"] = name,
        ["description"] = description,
        ["defaultEnabled"] = defaultEnabled
    };

    private static void ApplyFeatureMutation(JsonObject manifest, string mutation)
    {
        JsonArray features = manifest["features"]!.AsArray();
        JsonObject feature = features[0]!.AsObject();
        switch (mutation)
        {
            case "missing-default": feature.Remove("defaultEnabled"); break;
            case "nonboolean-default": feature["defaultEnabled"] = "enabled"; break;
            case "unknown-property": feature["unknown"] = true; break;
            case "malformed-key": feature["key"] = "Orders.Reads"; break;
            case "trailing-newline-key": feature["key"] = "orders.reads\n"; break;
            case "key-too-long": feature["key"] = "orders." + new string('a', 114); break;
            case "foreign-owner": feature["key"] = "reports.reads"; break;
            case "duplicate-key": features.Add(feature.DeepClone()); break;
            case "null-feature-list": manifest["features"] = null; break;
            case "null-feature": features[0] = null; break;
            case "too-many":
                manifest["features"] = new JsonArray(Enumerable.Range(0, 101)
                    .Select(index => (JsonNode?)Feature($"orders.feature-{index}", "Feature", "Description.", false)).ToArray());
                break;
            case "blank-name": feature["name"] = " "; break;
            case "name-too-long": feature["name"] = new string('a', 161); break;
            case "blank-description": feature["description"] = " "; break;
            case "description-too-long": feature["description"] = new string('a', 1001); break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown feature mutation.");
        }
    }
}
