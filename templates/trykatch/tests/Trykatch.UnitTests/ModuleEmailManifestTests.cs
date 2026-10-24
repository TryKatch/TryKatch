using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleExecutionManifestTests
{
    [TestMethod]
    [DataRow("templates-null")]
    [DataRow("policies-null")]
    [DataRow("template-null-entry")]
    [DataRow("policy-null-entry")]
    [DataRow("template-reference-null-entry")]
    [DataRow("grant-reference-null-entry")]
    public void DoctorReportsMalformedEmailCollectionsWithoutCrashing(string mutation)
    {
        JsonObject manifest = EmailOwnerManifest("orders", "orders", "orders");
        JsonNode execution = manifest["execution"]!;
        switch (mutation)
        {
            case "templates-null": execution["emailTemplates"] = null; break;
            case "policies-null": execution["emailDisclosurePolicies"] = null; break;
            case "template-null-entry": execution["emailTemplates"]!.AsArray().Add((JsonNode?)null); break;
            case "policy-null-entry": execution["emailDisclosurePolicies"]!.AsArray().Add((JsonNode?)null); break;
            case "template-reference-null-entry":
                execution["emailDisclosurePolicies"]![0]!["allowedTemplates"]!.AsArray().Add((JsonNode?)null);
                break;
            case "grant-reference-null-entry":
                execution["systemGrants"]![0]!["allowedEmailDisclosurePolicies"]!.AsArray().Add((JsonNode?)null);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("email", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void LegacyManifestFactsExposeEmptyEmailMetadata()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders", includeExecution: false)]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("emailTemplates").GetArrayLength().ShouldBe(0);
        execution.GetProperty("emailDisclosurePolicies").GetArrayLength().ShouldBe(0);
        execution.GetProperty("systemGrants").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorAcceptsSameOwnerEmailTemplatePolicyAndReciprocalSystemConsent()
    {
        JsonObject manifest = EmailOwnerManifest("orders", producerOwner: "orders", templateOwner: "orders");
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
        JsonElement execution = workspace.Tool.Facts("orders").GetProperty("modules")[0].GetProperty("execution");
        execution.GetProperty("emailTemplates").GetArrayLength().ShouldBe(1);
        execution.GetProperty("emailDisclosurePolicies").GetArrayLength().ShouldBe(1);
        execution.GetProperty("systemGrants")[0].GetProperty("allowedEmailDisclosurePolicies").GetArrayLength().ShouldBe(1);
    }

    [TestMethod]
    public void ExistingSystemGrantFactsExposeEmptyEmailPolicyReferences()
    {
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([Manifest("orders")]);

        ModuleDoctorReport report = workspace.Tool.Generate();
        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));

        JsonElement grants = workspace.Tool.Facts("orders").GetProperty("modules")[0]
            .GetProperty("execution").GetProperty("systemGrants");
        grants[0].GetProperty("allowedEmailDisclosurePolicies").GetArrayLength().ShouldBe(0);
    }

    [TestMethod]
    public void DoctorAcceptsCrossOwnerTemplateAndProducerWithDeclaredDependency()
    {
        JsonObject source = EmailOwnerManifest("messages", producerOwner: "notifications", templateOwner: "notifications", addGrant: false);
        JsonObject producer = JsonNode.Parse(Manifest("notifications", required: ["messages"]))!.AsObject();
        producer["execution"]!["eventConsumers"]![0]!["acceptedEvents"]![0]!["eventKey"] = "notifications.recorded";
        AddTemplate(producer, "notifications");
        AddPolicyGrant(producer, "messages", "messages.disclose", 1);

        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([source.ToJsonString(), producer.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
    }

    [TestMethod]
    public void DoctorAllowsReciprocalReferencesToAbsentContributorsToRemainDormant()
    {
        JsonObject source = EmailOwnerManifest("messages", producerOwner: "absent-module", templateOwner: "absent-module");
        source["execution"]!["systemGrants"]![0]!["allowedEmailDisclosurePolicies"] = new JsonArray();
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([source.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Generate();

        report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, report.Errors));
    }

    [TestMethod]
    [DataRow("wrong-template-version", "unavailable installed template version")]
    [DataRow("template-reference-purpose-key-too-long", "purpose key")]
    [DataRow("foreign-source", "source resource")]
    [DataRow("unregistered-permission", "unregistered permission")]
    [DataRow("duplicate-template", "duplicate")]
    [DataRow("unknown-installed-template", "unavailable installed template version")]
    public void DoctorRejectsInvalidEmailManifestReferences(string mutation, string expectedError)
    {
        JsonObject owner = EmailOwnerManifest("messages", producerOwner: "messages", templateOwner: "messages");
        ApplyEmailMutation(owner, mutation);
        IReadOnlyList<string> manifests = mutation == "unknown-installed-template"
            ? [owner.ToJsonString(), JsonNode.Parse(Manifest("notifications", includeExecution: false))!.AsObject().ToJsonString()]
            : [owner.ToJsonString()];
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create(manifests);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains(expectedError, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DoctorRejectsEmailMetadataWithoutBackgroundWorkCapability()
    {
        JsonObject manifest = EmailOwnerManifest("orders", producerOwner: "orders", templateOwner: "orders");
        manifest["capabilities"] = new JsonArray("api", "data");
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([manifest.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("BackgroundWork", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DoctorRejectsEmailGrantWithMissingPolicy()
    {
        JsonObject producer = EmailOwnerManifest("orders", producerOwner: "orders", templateOwner: "orders");
        producer["execution"]!["systemGrants"]![0]!["allowedEmailDisclosurePolicies"]![0]!["policyKey"] = "orders.missing";
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([producer.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("unavailable email disclosure policy version", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void DoctorRejectsEmailGrantWithoutReciprocalProducerConsent()
    {
        JsonObject producer = EmailOwnerManifest("orders", producerOwner: "orders", templateOwner: "orders");
        producer["execution"]!["emailDisclosurePolicies"]![0]!["allowedSystemProducers"] = new JsonArray();
        using TemporaryWorkspace workspace = TemporaryWorkspace.Create([producer.ToJsonString()]);

        ModuleDoctorReport report = workspace.Tool.Inspect();

        report.IsHealthy.ShouldBeFalse();
        report.Errors.ShouldContain(error => error.Contains("reciprocal", StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject EmailOwnerManifest(string owner, string producerOwner, string templateOwner, bool addGrant = true)
    {
        JsonObject manifest = JsonNode.Parse(Manifest(owner))!.AsObject();
        manifest["execution"]!["eventConsumers"]![0]!["acceptedEvents"]![0]!["eventKey"] = $"{owner}.recorded";
        if (templateOwner == owner) AddTemplate(manifest, templateOwner);
        manifest["execution"]!["emailDisclosurePolicies"] = new JsonArray
        {
            new JsonObject
            {
                ["policyKey"] = $"{owner}.disclose",
                ["policyVersion"] = 1,
                ["allowedSourceResourceKeys"] = new JsonArray("records"),
                ["requiredUserPermissionKeys"] = new JsonArray($"{owner}.read"),
                ["allowedTemplates"] = new JsonArray(TemplateReference(templateOwner)),
                ["allowedSystemProducers"] = new JsonArray(new JsonObject
                {
                    ["producer"] = new JsonObject
                    {
                        ["moduleId"] = producerOwner,
                        ["operationKey"] = $"{producerOwner}.consume",
                        ["operationVersion"] = 1
                    },
                    ["template"] = TemplateReference(templateOwner)
                })
            }
        };
        if (addGrant) AddPolicyGrant(manifest, owner, $"{owner}.disclose", 1);
        return manifest;
    }

    private static void AddTemplate(JsonObject manifest, string owner) =>
        manifest["execution"]!["emailTemplates"] = new JsonArray(new JsonObject
        {
            ["templateKey"] = $"{owner}.notice",
            ["templateVersion"] = 1,
            ["maximumTemplateDataBytes"] = 4096
        });

    private static void AddPolicyGrant(JsonObject manifest, string owner, string policyKey, int version) =>
        manifest["execution"]!["systemGrants"]![0]!["allowedEmailDisclosurePolicies"] = new JsonArray(new JsonObject
        {
            ["ownerModuleId"] = owner,
            ["policyKey"] = policyKey,
            ["policyVersion"] = version
        });

    private static JsonObject TemplateReference(string owner) => new()
    {
        ["ownerModuleId"] = owner,
        ["templateKey"] = $"{owner}.notice",
        ["templateVersion"] = 1
    };

    private static void ApplyEmailMutation(JsonObject manifest, string mutation)
    {
        JsonObject execution = manifest["execution"]!.AsObject();
        switch (mutation)
        {
            case "wrong-template-version":
                execution["emailDisclosurePolicies"]![0]!["allowedTemplates"]![0]!["templateVersion"] = 2;
                break;
            case "template-reference-purpose-key-too-long":
                string longKey = new('a', 152);
                execution["emailTemplates"]![0]!["templateKey"] = longKey;
                execution["emailDisclosurePolicies"]![0]!["allowedTemplates"]![0]!["templateKey"] = longKey;
                execution["emailDisclosurePolicies"]![0]!["allowedSystemProducers"]![0]!["template"]!["templateKey"] = longKey;
                break;
            case "foreign-source":
                execution["emailDisclosurePolicies"]![0]!["allowedSourceResourceKeys"]![0] = "foreign";
                break;
            case "unregistered-permission":
                execution["emailDisclosurePolicies"]![0]!["requiredUserPermissionKeys"]![0] = "messages.missing";
                break;
            case "duplicate-template":
                execution["emailTemplates"]!.AsArray().Add(execution["emailTemplates"]![0]!.DeepClone());
                break;
            case "unknown-installed-template":
                execution["emailDisclosurePolicies"]![0]!["allowedTemplates"]![0]!["ownerModuleId"] = "notifications";
                execution["emailDisclosurePolicies"]![0]!["allowedTemplates"]![0]!["templateKey"] = "notifications.notice";
                execution["emailDisclosurePolicies"]![0]!["allowedSystemProducers"]![0]!["template"]!["ownerModuleId"] = "notifications";
                execution["emailDisclosurePolicies"]![0]!["allowedSystemProducers"]![0]!["template"]!["templateKey"] = "notifications.notice";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown email manifest mutation.");
        }
    }
}
