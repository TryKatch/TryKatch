using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

public sealed partial class ModuleScaffolderTests
{
    [TestMethod]
    [DataRow("all", true)]
    [DataRow("any", false)]
    public void BlueprintAvailabilityPreservesPersistedPredicatesInsideMixedGuards(string operation, bool fieldIsRequired)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode action = node["workflow"]!["actions"]![2]!;
        action["guards"] = JsonNode.Parse("""
            [{"op":"all","code":"review_required","message":{"en":"Review required","fr":"Vérification requise"},"rules":[
              {"op":"eq","field":"documentsVerified","value":true,"code":"verified","message":{"en":"Verify documents","fr":"Vérifiez les documents"}},
              {"op":"notEmpty","input":"reason","code":"reason_required","message":{"en":"Enter a reason","fr":"Saisissez un motif"}}
            ]}]
            """);
        action["guards"]![0]!["op"] = operation;
        File.WriteAllText(path, node.ToJsonString());
        ModuleBlueprint blueprint = ModuleBlueprint.Load(path);
        string rendered = BlueprintRenderer.Render(blueprint, "Kametal", "shipment-receptions")["__DOMAIN_ACTIONS__"];
        string availability = rendered.Split("public bool CanReject() =>", StringSplitOptions.None)[1].Split(';')[0];
        availability.Contains("DocumentsVerified == true", StringComparison.Ordinal).ShouldBe(fieldIsRequired);
        availability.ShouldNotContain("reason");
        rendered.ShouldContain("!string.IsNullOrWhiteSpace(reason)");
    }

    [TestMethod]
    public void BlueprintValidationDoesNotMutateTheWorkspace()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        var baseline = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, File.ReadAllBytes);
        new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Validate(ShipmentRequest(path));
        Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).Order().ShouldBe(baseline.Keys.Order());
        foreach (var pair in baseline) File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
    }

    [TestMethod]
    public void BlueprintCreatesWorkflowVersionedEndpointsAndRetainsItsDefinition()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        ModuleCreationResult result = new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(ShipmentRequest(path));
        result.Permissions.ShouldContain("shipment-receptions.review");
        result.Endpoints.ShouldContain("POST /api/v1/shipment_receptions/{id}/actions/accept");
        string moduleRoot = Path.Combine(workspace.Root, "src/Modules/ShipmentReceptions");
        ModuleBlueprint.Load(Path.Combine(moduleRoot, "module.blueprint.json")).Module.ShouldBe("ShipmentReceptions");
        string workflow = File.ReadAllText(Path.Combine(moduleRoot, "Kametal.Modules.ShipmentReceptions.Domain/ShipmentReceptionWorkflow.cs"));
        workflow.ShouldContain("public void Accept(Guid expectedVersion, DateTimeOffset now)");
        workflow.ShouldContain("DocumentsVerified == true");
        workflow.ShouldContain("receivedWeight <= 0m");
        workflow.ShouldNotContain("__");
        string endpoints = File.ReadAllText(Path.Combine(moduleRoot, "Kametal.Modules.ShipmentReceptions.Presentation/ShipmentReceptionsEndpoints.cs"));
        endpoints.ShouldContain("JsonUnmappedMemberHandling.Disallow");
        endpoints.ShouldContain("request.ExpectedVersion");
    }

    [TestMethod]
    [DataRow("unknown-state")]
    [DataRow("unknown-field")]
    [DataRow("wrong-literal")]
    [DataRow("unsafe-assignment")]
    [DataRow("unknown-property")]
    [DataRow("unreachable")]
    [DataRow("invalid-label")]
    [DataRow("fractional-integer-bound")]
    [DataRow("conflicting-label")]
    [DataRow("availability-collision")]
    [DataRow("unknown-comparison-field")]
    public void InvalidBlueprintsFailBeforeModuleCreation(string scenario)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode workflow = node["workflow"]!;
        JsonNode action = workflow["actions"]![1]!;
        switch (scenario)
        {
            case "unknown-state": action["to"] = "Missing"; break;
            case "unknown-field": action["guards"]![0]!["field"] = "tenantId"; break;
            case "wrong-literal": action["guards"]![0]!["value"] = "true"; break;
            case "unsafe-assignment": action["assignments"] = new JsonObject { ["organizationId"] = "reason" }; break;
            case "unknown-property": node["runShell"] = "echo unsafe"; break;
            case "unreachable": workflow["states"]!.AsArray().Add(new JsonObject { ["name"] = "Lost", ["label"] = new JsonObject { ["en"] = "Lost", ["fr"] = "Perdu" } }); break;
            case "invalid-label": node["labels"]!["singular"]!["fr"] = ""; break;
            case "fractional-integer-bound": node["fields"]![1]!["kind"] = "int"; node["fields"]![1]!["minimum"] = 0.5m; break;
            case "conflicting-label": action["guards"]![0]!["code"] = "Draft"; break;
            case "availability-collision": workflow["actions"]![2]!["name"] = "CanAccept"; break;
            case "unknown-comparison-field": action["guards"]![0]!["compareToField"] = "missing"; break;
        }
        File.WriteAllText(path, node.ToJsonString());
        Exception exception = Should.Throw<Exception>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(ShipmentRequest(path)));
        (exception is ArgumentException or System.Text.Json.JsonException).ShouldBeTrue();
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/ShipmentReceptions")).ShouldBeFalse();
    }

    [TestMethod]
    public void BlueprintFullStackOutputIsDeterministicAndUsesFocusedHandlers()
    {
        using ScaffolderWorkspace first = ScaffolderWorkspace.Create(includeWeb: true);
        using ScaffolderWorkspace second = ScaffolderWorkspace.Create(includeWeb: true);
        new ModuleScaffolder(first.Root, new SuccessfulRunner()).Create(ShipmentRequest(WriteShipmentBlueprint(first.Root)) with { IncludeWeb = true });
        new ModuleScaffolder(second.Root, new SuccessfulRunner()).Create(ShipmentRequest(WriteShipmentBlueprint(second.Root)) with { IncludeWeb = true });
        Snapshot(first.Root, "src/Modules/ShipmentReceptions").ShouldBe(Snapshot(second.Root, "src/Modules/ShipmentReceptions"));
        string root = Path.Combine(first.Root, "src/Modules/ShipmentReceptions");
        File.ReadAllText(Path.Combine(root, "Kametal.Modules.ShipmentReceptions.Application/ShipmentReceptionsActions.cs"))
            .ShouldContain("class AcceptShipmentReceptionCommandHandler");
        File.ReadAllText(Path.Combine(root, "Kametal.Modules.ShipmentReceptions.Application/ShipmentReceptionQueries.cs"))
            .ShouldContain("class ListShipmentReceptionQueryHandler");
        File.ReadAllText(Path.Combine(root, "Web/src/index.tsx")).ShouldContain("expectedVersion: editing.version");
        File.ReadAllText(Path.Combine(root, "Web/src/index.tsx")).ShouldContain("description={t('actionDescription')}");
        File.ReadAllText(Path.Combine(root, "Web/src/workflow.ts")).ShouldContain("shipmentReceptionsAccept");
        string webTests = File.ReadAllText(Path.Combine(root, "Web/src/index.test.tsx"));
        webTests.ShouldContain("workflowState: \"Draft\"");
        webTests.ShouldContain("canEdit: true, availableActions: []");
        webTests.ShouldContain("shipmentReceptionsUpdate: (id: string, body: unknown)");
        webTests.ShouldContain("shipmentReceptionsAccept: vi.fn()");
        webTests.ShouldNotContain("__WEB_TEST_");
    }

    [TestMethod]
    public void BlueprintWebFailureRestoresEveryOriginalFileAndRemovesModule()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        var before = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new WebVerificationFailingRunner())
            .Create(ShipmentRequest(path) with { IncludeWeb = true }));
        foreach (var pair in before) File.ReadAllBytes(pair.Key).ShouldBe(pair.Value);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/ShipmentReceptions")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "tests/Modules/ShipmentReceptions")).ShouldBeFalse();
    }

    [TestMethod]
    public void BlueprintFieldComparisonIsRenderedAsTypedCode()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode guard = node["workflow"]!["actions"]![1]!["guards"]![0]!;
        guard["op"] = "lte";
        guard["field"] = "receivedWeight";
        guard.AsObject().Remove("value");
        guard["compareToField"] = "dispatchedWeight";
        File.WriteAllText(path, node.ToJsonString());
        new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(ShipmentRequest(path));
        File.ReadAllText(Path.Combine(workspace.Root, "src/Modules/ShipmentReceptions/Kametal.Modules.ShipmentReceptions.Domain/ShipmentReceptionWorkflow.cs"))
            .ShouldContain("ReceivedWeight <= DispatchedWeight");
    }

    private static ModuleCreateRequest ShipmentRequest(string path) => new("ShipmentReceptions", "ShipmentReception", "shipment_receptions", "organization", null, false, BlueprintPath: path);

    [TestMethod]
    public void BlueprintRejectsAnOlderHostBeforeWritingAnything()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string path = WriteShipmentBlueprint(workspace.Root);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        JsonObject catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        catalog.Remove("hostCapabilities");
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Validate(ShipmentRequest(path)))
            .Message.ShouldContain("Updating the CLI does not upgrade");
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/ShipmentReceptions")).ShouldBeFalse();
    }

    private static string WriteShipmentBlueprint(string root)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "blueprints/shipment-reception.json"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Shipment blueprint fixture is missing.");
        string path = Path.Combine(root, "shipment-reception.json");
        File.Copy(Path.Combine(directory.FullName, "blueprints/shipment-reception.json"), path);
        return path;
    }
}
