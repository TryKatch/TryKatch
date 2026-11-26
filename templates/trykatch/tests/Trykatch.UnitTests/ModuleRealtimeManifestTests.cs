using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void LegacyExecutionManifestFactsExposeAnEmptyRealtimePolicyCollection()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders")]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("realtimeAudiencePolicies").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorAndFactsAcceptAndExposeAReciprocallyConsentedRealtimePolicy()
    {
        JsonObject manifest = JsonNode.Parse(Manifest("orders"))!.AsObject();
        AddRealtimePolicy(manifest, [Producer("orders", "orders.consume")]);
        AddPolicyGrantReference(manifest, "orders");
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("realtimeAudiencePolicies").GetArrayLength().ShouldBe(1);
        execution.GetProperty("systemGrants")[0].GetProperty("allowedRealtimePolicies").GetArrayLength().ShouldBe(1);
    }

    [TestMethod]
    [DataRow("required")]
    [DataRow("optional")]
    public void DoctorAcceptsAConsentedCrossModuleRealtimeProducerWhenDependencyIsDeclared(string dependencyKind)
    {
        JsonObject owner = JsonNode.Parse(Manifest("orders"))!.AsObject();
        JsonObject producer = JsonNode.Parse(Manifest("reports", required: dependencyKind == "required" ? ["orders"] : []))!.AsObject();
        if (dependencyKind == "optional")
            producer["optionalDependencies"] = new JsonArray("orders");

        AddRealtimePolicy(owner, [Producer("reports", "reports.consume")]);
        AddRealtimePolicyGrantReference(producer, "orders");
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([owner.ToJsonString(), producer.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
    }

    [TestMethod]
    [DataRow("invalid-policy-version")]
    [DataRow("unknown-audience")]
    [DataRow("empty-audiences")]
    [DataRow("duplicate-policy")]
    [DataRow("too-many-policies")]
    [DataRow("null-policy")]
    [DataRow("null-policy-list")]
    [DataRow("null-events-list")]
    [DataRow("null-event-entry")]
    [DataRow("null-grant-reference")]
    [DataRow("unowned-resource")]
    [DataRow("unregistered-permission")]
    [DataRow("organization-without-permission")]
    [DataRow("resource-without-key")]
    [DataRow("missing-event")]
    [DataRow("foreign-event")]
    [DataRow("missing-event-version")]
    [DataRow("missing-producer")]
    [DataRow("non-committed-producer")]
    [DataRow("producer-without-grant-consent")]
    [DataRow("grant-without-reciprocal-policy-consent")]
    [DataRow("grant-policy-version-mismatch")]
    [DataRow("cross-module-producer-without-dependency")]
    public void DoctorRejectsInvalidRealtimeMetadata(string mutation)
    {
        JsonObject manifest = JsonNode.Parse(Manifest("orders"))!.AsObject();
        AddRealtimePolicy(manifest, [Producer("orders", "orders.consume")]);
        AddPolicyGrantReference(manifest, "orders");
        ApplyRealtimeMutation(manifest, mutation);

        IReadOnlyList<string> manifests = mutation == "cross-module-producer-without-dependency"
            ? CrossModuleWithoutDependency(manifest)
            : [manifest.ToJsonString()];
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create(manifests);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("realtime", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ManifestSchemaDeclaresRealtimePolicyAndGrantReferenceFields()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "schemas", "module-manifest.v1.schema.json")))
            directory = Directory.GetParent(directory)?.FullName;
        directory.ShouldNotBeNull();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(directory!, "schemas", "module-manifest.v1.schema.json")));

        schema.RootElement.GetProperty("$defs").GetProperty("execution").GetProperty("properties")
            .TryGetProperty("realtimeAudiencePolicies", out _).ShouldBeTrue();
        schema.RootElement.GetProperty("$defs").GetProperty("systemGrant").GetProperty("properties")
            .TryGetProperty("allowedRealtimePolicies", out _).ShouldBeTrue();
    }

    private static void ApplyRealtimeMutation(JsonObject manifest, string mutation)
    {
        JsonObject execution = manifest["execution"]!.AsObject();
        JsonArray policies = execution["realtimeAudiencePolicies"]!.AsArray();
        JsonObject policy = policies[0]!.AsObject();
        switch (mutation)
        {
            case "invalid-policy-version": policy["policyVersion"] = 0; break;
            case "unknown-audience": policy["allowedAudiences"]![0] = "Organization"; break;
            case "empty-audiences": policy["allowedAudiences"] = new JsonArray(); break;
            case "duplicate-policy": policies.Add(policy.DeepClone()); break;
            case "too-many-policies":
                execution["realtimeAudiencePolicies"] = new JsonArray(Enumerable.Range(0, 101)
                    .Select(index => (JsonNode?)new JsonObject
                    {
                        ["policyKey"] = $"orders.realtime-{index}",
                        ["policyVersion"] = 1,
                        ["allowedAudiences"] = new JsonArray("user"),
                        ["allowedResourceKeys"] = new JsonArray(),
                        ["requiredUserPermissionKeys"] = new JsonArray(),
                        ["allowedEvents"] = new JsonArray(new JsonObject { ["eventKey"] = "orders.recorded", ["schemaVersion"] = 1 }),
                        ["allowedSystemProducers"] = new JsonArray()
                    }).ToArray());
                break;
            case "null-policy": policies[0] = null; break;
            case "null-policy-list": execution["realtimeAudiencePolicies"] = null; break;
            case "null-events-list": policy["allowedEvents"] = null; break;
            case "null-event-entry": policy["allowedEvents"]![0] = null; break;
            case "null-grant-reference": execution["systemGrants"]![0]!["allowedRealtimePolicies"]![0] = null; break;
            case "unowned-resource": policy["allowedResourceKeys"] = new JsonArray("unknown"); break;
            case "unregistered-permission": policy["requiredUserPermissionKeys"] = new JsonArray("orders.unknown"); break;
            case "organization-without-permission": policy["requiredUserPermissionKeys"] = new JsonArray(); break;
            case "resource-without-key":
                policy["allowedAudiences"] = new JsonArray("resource");
                policy["allowedResourceKeys"] = new JsonArray();
                break;
            case "missing-event": policy["allowedEvents"]![0]!["eventKey"] = "orders.missing"; break;
            case "foreign-event": policy["allowedEvents"]![0]!["eventKey"] = "reports.recorded"; break;
            case "missing-event-version": policy["allowedEvents"]![0]!["schemaVersion"] = 2; break;
            case "missing-producer": policy["allowedSystemProducers"]![0]!["operationKey"] = "orders.missing"; break;
            case "non-committed-producer": policy["allowedSystemProducers"]![0]!["operationKey"] = "orders.create"; break;
            case "producer-without-grant-consent":
                execution["systemGrants"]![0]!["allowedRealtimePolicies"] = new JsonArray();
                break;
            case "grant-without-reciprocal-policy-consent": policy["allowedSystemProducers"] = new JsonArray(); break;
            case "grant-policy-version-mismatch": execution["systemGrants"]![0]!["allowedRealtimePolicies"]![0]!["policyVersion"] = 2; break;
            case "cross-module-producer-without-dependency":
                policy["allowedSystemProducers"]![0]!["moduleId"] = "reports";
                policy["allowedSystemProducers"]![0]!["operationKey"] = "reports.consume";
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown realtime mutation.");
        }
    }

    private static IReadOnlyList<string> CrossModuleWithoutDependency(JsonObject owner)
    {
        JsonObject producer = JsonNode.Parse(Manifest("reports"))!.AsObject();
        AddRealtimePolicyGrantReference(producer, "orders");
        return [owner.ToJsonString(), producer.ToJsonString()];
    }

    private static void AddRealtimePolicy(JsonObject manifest, IReadOnlyList<JsonObject> producers)
    {
        JsonObject execution = manifest["execution"]!.AsObject();
        execution["realtimeAudiencePolicies"] = new JsonArray(new JsonObject
        {
            ["policyKey"] = "orders.realtime-read",
            ["policyVersion"] = 1,
            ["allowedAudiences"] = new JsonArray("organization", "user", "resource"),
            ["allowedResourceKeys"] = new JsonArray("records"),
            ["requiredUserPermissionKeys"] = new JsonArray("orders.read"),
            ["allowedEvents"] = new JsonArray(new JsonObject { ["eventKey"] = "orders.recorded", ["schemaVersion"] = 1 }),
            ["allowedSystemProducers"] = new JsonArray(producers.Select(producer => (JsonNode?)producer.DeepClone()).ToArray())
        });
    }

    private static void AddPolicyGrantReference(JsonObject manifest, string ownerModuleId) =>
        AddRealtimePolicyGrantReference(manifest, ownerModuleId);

    private static void AddRealtimePolicyGrantReference(JsonObject manifest, string ownerModuleId)
    {
        manifest["execution"]!["systemGrants"]![0]!["allowedRealtimePolicies"] = new JsonArray(new JsonObject
        {
            ["ownerModuleId"] = ownerModuleId,
            ["policyKey"] = "orders.realtime-read",
            ["policyVersion"] = 1
        });
    }

    private static JsonObject Producer(string moduleId, string operationKey) => new()
    {
        ["moduleId"] = moduleId,
        ["operationKey"] = operationKey,
        ["operationVersion"] = 1
    };
}
