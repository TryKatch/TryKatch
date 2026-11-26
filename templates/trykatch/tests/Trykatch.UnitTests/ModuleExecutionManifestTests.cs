using System.Text.Json.Nodes;
using System.Text.Json;
using Trykatch.ModuleTool;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    public void SchemaVersionOneManifestWithoutExecutionRemainsValidAndFactsExposeEmptyMetadata()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders", includeExecution: false)]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("operations").GetArrayLength().ShouldBe(0);
        execution.GetProperty("systemGrants").GetArrayLength().ShouldBe(0);
        execution.GetProperty("integrationEvents").GetArrayLength().ShouldBe(0);
        execution.GetProperty("eventConsumers").GetArrayLength().ShouldBe(0);
        execution.GetProperty("jobs").GetArrayLength().ShouldBe(0);
        execution.GetProperty("recurringSchedules").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorAndFactsAcceptACompleteExecutionManifestWithoutLoadingModuleCode()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders")]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("operations").GetArrayLength().ShouldBe(3);
        execution.GetProperty("systemGrants").GetArrayLength().ShouldBe(2);
        execution.GetProperty("integrationEvents").GetArrayLength().ShouldBe(1);
        execution.GetProperty("eventConsumers").GetArrayLength().ShouldBe(1);
        execution.GetProperty("jobs").GetArrayLength().ShouldBe(1);
        execution.GetProperty("recurringSchedules").GetArrayLength().ShouldBe(1);
        execution.GetProperty("integrationEvents")[0].TryGetProperty("payloadType", out _).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("no-background-capability", "without the BackgroundWork capability")]
    [DataRow("unknown-kind", "unknown execution kind")]
    [DataRow("payload-too-large", "payload limit")]
    [DataRow("missing-permission", "unregistered permission")]
    [DataRow("missing-resource", "undeclared resource")]
    [DataRow("missing-system-grant", "unregistered system grant")]
    [DataRow("missing-event", "uninstalled event schema")]
    [DataRow("missing-job-operation", "does not resolve to a compatible")]
    [DataRow("invalid-schedule-kind", "unknown misfire policy")]
    public void DoctorRejectsInvalidExecutionMetadata(string mutation, string expectedError)
    {
        JsonObject manifest = JsonNode.Parse(Manifest("orders"))!.AsObject();
        ApplyMutation(manifest, mutation);
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("event-version")]
    [DataRow("consumer")]
    [DataRow("job")]
    [DataRow("schedule")]
    public void DoctorRejectsDuplicateStableExecutionKeysAcrossEnabledModules(string duplicate)
    {
        JsonObject first = JsonNode.Parse(Manifest("orders"))!.AsObject();
        JsonObject second = JsonNode.Parse(Manifest("reports", required: ["orders"]))!.AsObject();
        DuplicateExecutionKey(second, duplicate);
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([first.ToJsonString(), second.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    private static void ApplyMutation(JsonObject manifest, string mutation)
    {
        JsonObject execution = manifest["execution"]!.AsObject();
        switch (mutation)
        {
            case "no-background-capability":
                manifest["capabilities"] = new JsonArray("api", "data");
                break;
            case "unknown-kind":
                execution["operations"]![1]!["executionKind"] = "mystery";
                break;
            case "payload-too-large":
                execution["operations"]![0]!["maximumPayloadBytes"] = 1_048_577;
                break;
            case "missing-permission":
                execution["operations"]![0]!["requiredPermissionKeys"] = new JsonArray("orders.unknown");
                break;
            case "missing-resource":
                execution["systemGrants"]![0]!["allowedResourceKeys"] = new JsonArray("orders.unknown");
                break;
            case "missing-system-grant":
                execution["operations"]![1]!["systemGrantKey"] = "orders.unknown";
                break;
            case "missing-event":
                execution["eventConsumers"]![0]!["acceptedEvents"]![0]!["eventKey"] = "orders.unknown";
                break;
            case "missing-job-operation":
                execution["jobs"]![0]!["operationKey"] = "orders.unknown";
                break;
            case "invalid-schedule-kind":
                execution["recurringSchedules"]![0]!["misfirePolicy"] = "maybe";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown test mutation.");
        }
    }

    private static void DuplicateExecutionKey(JsonObject manifest, string duplicate)
    {
        JsonObject execution = manifest["execution"]!.AsObject();
        switch (duplicate)
        {
            case "operation":
                execution["operations"]![0]!["operationKey"] = "orders.create";
                break;
            case "event-version":
                execution["integrationEvents"]![0]!["eventKey"] = "orders.recorded";
                break;
            case "consumer":
                execution["eventConsumers"]![0]!["consumerKey"] = "orders.recorded-consumer";
                break;
            case "job":
                execution["jobs"]![0]!["jobKey"] = "orders.expire-job";
                break;
            case "schedule":
                execution["recurringSchedules"]![0]!["scheduleKey"] = "orders.expire-daily";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(duplicate), duplicate, "Unknown duplicate key.");
        }
    }

    private static string Manifest(string id, bool includeExecution = true, IReadOnlyList<string>? required = null)
    {
        string execution = includeExecution
            ? Execution(id, id == "reports" ? "orders.recorded" : "orders.recorded")
            : string.Empty;
        string requiredJson = string.Join(",", (required ?? []).Select(value => JsonSerializer.Serialize(value)));
        return $$"""
            {
              "schemaVersion": 1,
              "id": {{JsonSerializer.Serialize(id)}},
              "name": {{JsonSerializer.Serialize(id)}},
              "version": "1.0.0",
              "description": "Manifest validation fixture.",
              "publisher": "trykatch",
              "distribution": { "kind": "workspace", "license": "Apache-2.0" },
              "compatibility": { "minimumHostVersion": "0.1.0", "maximumHostVersionExclusive": "1.0.0" },
              "requires": [{{requiredJson}}],
              "optionalDependencies": [],
              "capabilities": ["api", "data", "background-work"],
              "dataOwnership": {
                "default": "infrastructure",
                "resources": [{ "name": "records", "schema": "infrastructure", "table": "records", "ownership": "infrastructure", "accessRule": "host-only" }]
              },
              "artifacts": { "dotnetProject": "src/{{id}}.csproj" },
              "entrypoints": { "dotnet": { "type": "{{id}}.Module" } },
              "contributions": {
                "permissions": ["{{id}}.read"],
                "routes": [],
                "extensionPoints": [],
                "extensions": [],
                "assistantTools": []
              }{{(includeExecution ? "," : "")}}
              {{(includeExecution ? $"\"execution\": {execution}" : "")}}
            }
            """;
    }

    private static string Execution(string id, string eventKey) => $$"""
        {
          "operations": [
            {
              "operationKey": "{{id}}.create",
              "operationVersion": 1,
              "executionKind": "user-command",
              "payloadSchemaKey": "{{id}}.create-payload",
              "payloadSchemaVersion": 1,
              "requiredPermissionKeys": ["{{id}}.read"],
              "resourcePolicyKey": "records",
              "systemGrantKey": null,
              "maximumPayloadBytes": 1024
            },
            {
              "operationKey": "{{id}}.consume",
              "operationVersion": 1,
              "executionKind": "committed-event",
              "payloadSchemaKey": "{{id}}.event-payload",
              "payloadSchemaVersion": 1,
              "requiredPermissionKeys": [],
              "resourcePolicyKey": "records",
              "systemGrantKey": "{{id}}.event-grant",
              "maximumPayloadBytes": 4096
            },
            {
              "operationKey": "{{id}}.expire",
              "operationVersion": 1,
              "executionKind": "scheduled-system",
              "payloadSchemaKey": "{{id}}.expire-payload",
              "payloadSchemaVersion": 1,
              "requiredPermissionKeys": [],
              "resourcePolicyKey": "records",
              "systemGrantKey": "{{id}}.expiration-grant",
              "maximumPayloadBytes": 1024
            }
          ],
          "systemGrants": [
            { "grantKey": "{{id}}.event-grant", "grantVersion": 1, "auditActorId": "11111111-1111-1111-1111-111111111111", "allowedOperationKeys": ["{{id}}.consume"], "allowedResourceKeys": ["records"] },
            { "grantKey": "{{id}}.expiration-grant", "grantVersion": 1, "auditActorId": "22222222-2222-2222-2222-222222222222", "allowedOperationKeys": ["{{id}}.expire"], "allowedResourceKeys": ["records"] }
          ],
          "integrationEvents": [
            { "eventKey": "{{id}}.recorded", "schemaVersion": 1, "maximumPayloadBytes": 4096 }
          ],
          "eventConsumers": [
            {
              "consumerKey": "{{id}}.recorded-consumer",
              "acceptedEvents": [{ "eventKey": "{{eventKey}}", "schemaVersion": 1 }],
              "operationKey": "{{id}}.consume",
              "operationVersion": 1,
              "systemGrantKey": "{{id}}.event-grant"
            }
          ],
          "jobs": [
            {
              "jobKey": "{{id}}.expire-job",
              "jobVersion": 1,
              "operationKey": "{{id}}.expire",
              "operationVersion": 1,
              "executionKind": "scheduled-system",
              "payloadSchemaKey": "{{id}}.expire-payload",
              "payloadSchemaVersion": 1,
              "maximumPayloadBytes": 512
            }
          ],
          "recurringSchedules": [
            { "scheduleKey": "{{id}}.expire-daily", "jobKey": "{{id}}.expire-job", "jobVersion": 1, "utcSchedule": "0 0 * * *", "misfirePolicy": "catch-up", "systemGrantKey": "{{id}}.expiration-grant" }
          ]
        }
        """;

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string root)
        {
            Root = root;
            Tool = new ModuleWorkspace(root);
        }

        public string Root { get; }
        public ModuleWorkspace Tool { get; }

        public static TemporaryWorkspace Create(IReadOnlyList<string> manifests)
        {
            string root = Path.Combine(Path.GetTempPath(), $"trykatch-execution-manifest-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "manifests"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            for (int index = 0; index < manifests.Count; index++)
            {
                using JsonDocument document = JsonDocument.Parse(manifests[index]);
                string id = document.RootElement.GetProperty("id").GetString()!;
                File.WriteAllText(Path.Combine(root, "manifests", $"{id}.json"), manifests[index]);
                File.WriteAllText(Path.Combine(root, "src", $"{id}.csproj"), "<Project />");
            }

            string registrations = string.Join(",\n", manifests.Select(manifest =>
            {
                using JsonDocument document = JsonDocument.Parse(manifest);
                string id = document.RootElement.GetProperty("id").GetString()!;
                return $$"""{ "id": "{{id}}", "manifest": "manifests/{{id}}.json", "enabled": true }""";
            }));
            File.WriteAllText(Path.Combine(root, "trykatch.modules.json"), $$"""
                {
                  "schemaVersion": 1,
                  "hostVersion": "0.1.0",
                  "lockFile": "trykatch.modules.lock.json",
                  "trustedPublishers": ["trykatch"],
                  "outputs": {
                    "backend": "GeneratedModules.cs",
                    "backendNamespace": "Trykatch.Api.Modules",
                    "migrator": "GeneratedMigratorModules.cs",
                    "migratorNamespace": "Trykatch.Migrator.Modules"
                  },
                  "modules": [{{registrations}}]
                }
                """ + "\n");

            return new(root);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
