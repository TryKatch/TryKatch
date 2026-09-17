using System.Text.Json.Nodes;
using System.Xml.Linq;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed partial class ModuleScaffolderTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreationReportsBackendAndOptionalFrontendPhases(bool includeWeb)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        List<ModuleCreationProgress> phases = [];
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner(), progress: phases.Add);
        scaffolder.Create(new("Invoicing", "Invoice", "invoices", "organization", null, includeWeb));

        phases.Select(phase => phase.Step).ShouldBe(includeWeb
            ? ["Waiting for the workspace lock", "Validating module names, contracts and workspace",
                "Rendering and validating module templates", "Adding module projects to the solution",
                "Registering and enabling the module", "Restoring .NET dependencies",
                "Updating the frontend dependency lock file", "Building the backend and generating OpenAPI",
                "Running generated architecture tests", "Running generated unit tests",
                "Installing frontend dependencies", "Generating the frontend API client",
                "Checking frontend types", "Running frontend tests", "Building the frontend",
                "Checking module health with doctor"]
            : ["Waiting for the workspace lock", "Validating module names, contracts and workspace",
                "Rendering and validating module templates", "Adding module projects to the solution",
                "Registering and enabling the module", "Restoring .NET dependencies",
                "Building the backend and generating OpenAPI", "Running generated architecture tests",
                "Running generated unit tests", "Checking module health with doctor"]);
        phases.Any(phase => phase.IsRollback).ShouldBeFalse();
    }

    [TestMethod]
    public void FailedCreationReportsRollbackAndRetainsTheUnderlyingError()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        List<ModuleCreationProgress> phases = [];
        ModuleScaffolder scaffolder = new(workspace.Root, new FailingRunner(), progress: phases.Add);
        Should.Throw<InvalidOperationException>(() => scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("simulated restore failure");
        phases[^1].ShouldBe(new("Restoring the original workspace", IsRollback: true));
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void CancellationReportsRollbackAndRemovesTheNewModule()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        using CancellationTokenSource cancellation = new();
        List<ModuleCreationProgress> phases = [];
        ModuleScaffolder scaffolder = new(workspace.Root, new CancellingRunner(cancellation), progress: phases.Add);
        Should.Throw<OperationCanceledException>(() => scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false), cancellation.Token));
        phases[^1].IsRollback.ShouldBeTrue();
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void CreateProducesAnEnabledOrganizationCrudModule()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());

        ModuleCreationResult result = scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization",
            "Organization invoice management.", IncludeWeb: false));

        result.ModuleId.ShouldBe("invoicing");
        result.IncludeWeb.ShouldBeFalse();
        result.Report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, result.Report.Errors));
        result.Report.Modules.Single(module => module.Id == "invoicing").Enabled.ShouldBeTrue();

        string moduleRoot = Path.Combine(workspace.Root, "src/Modules/Invoicing");
        File.Exists(Path.Combine(moduleRoot, "Kametal.Modules.Invoicing.Domain/InvoiceRecord.cs")).ShouldBeTrue();
        string moduleSource = File.ReadAllText(Path.Combine(moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModule.cs"));
        moduleSource.ShouldContain("ALTER TABLE app.invoices FORCE ROW LEVEL SECURITY");
        moduleSource.ShouldContain("CREATE POLICY invoices_organization_isolation");
        moduleSource.ShouldContain("WITH CHECK");
        string manifest = File.ReadAllText(Path.Combine(moduleRoot, "trykatch.module.json"));
        manifest.ShouldContain("\"ownership\": \"organization\"");
        manifest.ShouldNotContain("webPackage");
        string solution = File.ReadAllText(Path.Combine(workspace.Root, "Kametal.slnx"));
        solution.ShouldContain("/src/Modules/Invoicing/");
        solution.ShouldContain("/tests/Modules/Invoicing/");

        result.Endpoints.ShouldBe([
            "GET /api/v1/invoices",
            "GET /api/v1/invoices/page",
            "GET /api/v1/invoices/{id}",
            "POST /api/v1/invoices",
            "PUT /api/v1/invoices/{id}",
            "POST /api/v1/invoices/{id}/archive",
            "POST /api/v1/invoices/{id}/restore",
            "DELETE /api/v1/invoices/{id}"
        ]);
        result.Permissions.ShouldBe(["invoicing.read", "invoicing.manage"]);
        result.StartCommand.ShouldBe("trykatch start");

        string integrationEvents = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.IntegrationEvents/InvoiceIntegrationEvents.cs"));
        integrationEvents.ShouldContain("record InvoiceCreated(");
        integrationEvents.ShouldContain("record InvoiceUpdated(");
        integrationEvents.ShouldContain("record InvoiceArchived(");
        integrationEvents.ShouldContain("record InvoiceRestored(");
        integrationEvents.ShouldContain("record InvoiceDeletionRequested(");
        integrationEvents.ShouldNotContain("string Operation");
        File.ReadAllText(Path.Combine(
                moduleRoot, "Kametal.Modules.Invoicing.Application/InvoicingUseCases.cs"))
            .ShouldNotContain("context.Enqueue<object>");

        string endpoints = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Presentation/InvoicingEndpoints.cs"));
        foreach (string operationId in new[]
                 {
                     "Invoicing_List", "Invoicing_Get", "Invoicing_Create", "Invoicing_Update",
                     "Invoicing_Archive", "Invoicing_Restore", "Invoicing_RequestDeletion"
                 })
            endpoints.ShouldContain($".WithName(\"{operationId}\")");
        endpoints.ShouldContain("Task<Results<Ok<InvoiceDto[]>, ForbidHttpResult, ValidationProblem>>");
        endpoints.ShouldContain("Invoicing_Page");
        endpoints.ShouldContain("request.ExpectedVersion");
        endpoints.ShouldContain("[\"code\"] = \"stale_version\"");
        string store = File.ReadAllText(Path.Combine(moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoiceStore.cs"));
        store.ShouldContain(".Skip((request.Page - 1) * request.PageSize).Take(request.PageSize + 1)");
        store.ShouldContain(".ThenByDescending(record => record.Id)");
        store.ShouldContain("IgnoreQueryFilters([\"LifecycleVisibility\"])");
        store.ShouldNotContain("IgnoreQueryFilters()");
        store.ShouldContain("catch (DbUpdateConcurrencyException)");
        File.ReadAllText(Path.Combine(moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModelContributor.cs"))
            .ShouldContain("record.Version).IsConcurrencyToken()");
    }

    [TestMethod]
    public void WebGenerationRejectsAnOlderSdkBeforeWritingTheModule()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        JsonObject catalog = JsonNode.Parse(File.ReadAllText(catalogPath))!.AsObject();
        catalog["hostCapabilities"] = new JsonArray("business-blueprints-v1");
        File.WriteAllText(catalogPath, catalog.ToJsonString());
        byte[] before = File.ReadAllBytes(catalogPath);
        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner())
            .Create(new("Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true)))
            .Message.ShouldContain("typed-tables-v1");
        File.ReadAllBytes(catalogPath).ShouldBe(before);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void CreateWithWebProducesAndRegistersAReactPackage()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());

        ModuleCreationResult result = scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true));

        result.Report.IsHealthy.ShouldBeTrue(string.Join(Environment.NewLine, result.Report.Errors));
        string packagePath = Path.Combine(workspace.Root, "src/Modules/Invoicing/Web/package.json");
        File.ReadAllText(packagePath).ShouldContain("@kametal-modules/invoicing");
        File.ReadAllText(Path.Combine(workspace.Root, "web/apps/web/package.json"))
            .ShouldContain("@kametal-modules/invoicing");
        File.ReadAllText(Path.Combine(workspace.Root, "web/src/modules.ts"))
            .ShouldContain("invoicingModule");
        File.ReadAllText(Path.Combine(
            workspace.Root, "src/Modules/Invoicing/Kametal.Modules.Invoicing.Infrastructure/InvoicingModule.cs"))
            .ShouldContain("ModuleCapabilities.Api | ModuleCapabilities.Data | ModuleCapabilities.Web");
        string web = File.ReadAllText(Path.Combine(workspace.Root, "src/Modules/Invoicing/Web/src/index.tsx"));
        web.ShouldContain("defineTableExtensionPoint<InvoiceDto>");
        web.ShouldContain("useTableContributions(invoicingTable");
        web.ShouldContain("extensionPoints: [invoicingTable]");
        string webTests = File.ReadAllText(Path.Combine(workspace.Root, "src/Modules/Invoicing/Web/src/index.test.tsx"));
        webTests.ShouldContain("name: 'A'");
        webTests.ShouldContain("version: '0199ca9e-3870-7000-8000-000000000003'");
        webTests.ShouldContain("preserves entered values and retries only after an explicit version refresh");
        webTests.ShouldContain("does not reopen a cancelled editor");
        webTests.ShouldNotContain("__WEB_TEST_");
        File.ReadAllText(packagePath).ShouldContain("\"jsdom\": \"27.0.0\"");
    }

    [TestMethod]
    [DataRow("count:int:required", "count: 1,")]
    [DataRow("total:decimal:optional", "total: '1',")]
    [DataRow("sequence:long:required", "sequence: '1',")]
    [DataRow("enabled:bool:required", "enabled: false,")]
    [DataRow("enabled:bool:optional", "enabled: false,")]
    [DataRow("dueDate:date:required", "dueDate: '2026-09-16',")]
    [DataRow("issuedAt:datetime:optional", "issuedAt: '2026-09-16T12:00:00Z',")]
    [DataRow("reference:guid:required", "reference: '0199ca9e-3870-7000-8000-000000000002',")]
    [DataRow("stage:enum(Draft,Sent):required", "stage: 'Draft',")]
    [DataRow("label:string:required:max(1)", "label: 'A',")]
    public void WebConflictFixturesRespectTheDeclaredFieldTypes(string fields, string expected)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Metrics", "Metric", "metrics", "organization", null, IncludeWeb: true, FieldSpecification: fields));
        string tests = File.ReadAllText(Path.Combine(workspace.Root, "src/Modules/Metrics/Web/src/index.test.tsx"));
        tests.ShouldContain(expected);
        tests.ShouldNotContain("__WEB_TEST_");
    }

    [TestMethod]
    [DataRow("version:guid:required")]
    [DataRow("expectedVersion:guid:required")]
    [DataRow("search:string")]
    [DataRow("page:int")]
    [DataRow("refresh:bool")]
    [DataRow("refreshScope:int")]
    [DataRow("table:string")]
    public void PaginationConcurrencyAndExtensionNamesAreReserved(string fields) =>
        Should.Throw<ArgumentException>(() => ModuleFieldContract.Parse(fields));

    [TestMethod]
    public void WebFieldsCannotShadowTheirModulesTypedTablePoint()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        Should.Throw<ArgumentException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner())
            .Create(new("Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true, FieldSpecification: "invoicingTable:string")))
            .Message.ShouldContain("shadow");
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void FieldContractGeneratesTheSameBusinessShapeAcrossBackendDatabaseAndReact()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());

        scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true,
            FieldSpecification: "number:string:required:max(40),total:decimal:required,sequence:long:required,dueDate:date:required,issuedAt:datetime:optional,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)"));

        string moduleRoot = Path.Combine(workspace.Root, "src/Modules/Invoicing");
        string domain = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Domain/InvoiceRecord.cs"));
        domain.ShouldContain("public string Number { get; private set; } = string.Empty;");
        domain.ShouldContain("public decimal Total { get; private set; }");
        domain.ShouldContain("public DateOnly DueDate { get; private set; }");
        domain.ShouldContain("public InvoiceStatus Status { get; private set; }");
        domain.ShouldContain("public string? Notes { get; private set; }");
        domain.ShouldNotContain("public string Name");

        string useCases = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Application/InvoicingUseCases.cs"));
        useCases.ShouldContain("string? Number");
        useCases.ShouldContain("string? Total");
        useCases.ShouldContain("string? Sequence");
        useCases.ShouldContain("DateOnly? DueDate");
        useCases.ShouldContain("string? Status");
        useCases.ShouldContain("string Number");
        useCases.ShouldContain("string Total");
        useCases.ShouldContain("string Sequence");
        useCases.ShouldContain("DateOnly DueDate");
        useCases.ShouldContain("string Status");
        useCases.ShouldContain("Enum.Parse<InvoiceStatus>");
        useCases.ShouldContain("decimal.Parse(command.Total!, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture)");
        useCases.ShouldContain("long.Parse(command.Sequence!, NumberStyles.Integer, CultureInfo.InvariantCulture)");
        useCases.ShouldContain("record.Total.ToString(CultureInfo.InvariantCulture)");
        useCases.ShouldContain("NormalizeAuditDisplay(record.Number, record.Id)");

        string persistence = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModelContributor.cs"));
        persistence.ShouldContain("entity.Property(record => record.Number).HasMaxLength(40)");
        persistence.ShouldContain("entity.Property(record => record.Total).HasPrecision(18, 2)");
        persistence.ShouldContain("entity.Property(record => record.Status).HasConversion<string>()");

        string migration = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModule.cs"));
        migration.ShouldContain("\"Number\" character varying(40) NOT NULL");
        migration.ShouldContain("\"Total\" numeric(18, 2) NOT NULL");
        migration.ShouldContain("\"Sequence\" bigint NOT NULL");
        migration.ShouldContain("\"IssuedAt\" timestamp with time zone NULL");
        migration.ShouldContain("\"DueDate\" date NOT NULL");
        migration.ShouldContain("\"Status\" character varying(5) NOT NULL");
        migration.ShouldContain("\"Notes\" character varying(2000) NULL");

        string web = File.ReadAllText(Path.Combine(moduleRoot, "Web/src/index.tsx"));
        web.ShouldContain("const [number, setNumber] = useState('')");
        web.ShouldContain("const [total, setTotal] = useState('')");
        web.ShouldContain("<option value=\"Draft\">{t('fieldStatusDraft')}</option>");
        web.ShouldContain("total, sequence");
        web.ShouldContain("issuedAt: issuedAt === '' ? null : toUtcDateTime(issuedAt, editing?.issuedAt)");
        web.ShouldContain("toDateTimeLocal(record.issuedAt)");
        web.ShouldContain("type=\"datetime-local\" step=\"0.001\"");
        web.ShouldNotContain("Number(total)");
        web.ShouldNotContain("Number(sequence)");
        web.ShouldContain("record.number");

        string dateTimeTests = File.ReadAllText(Path.Combine(moduleRoot, "Web/src/dateTime.test.ts"));
        dateTimeTests.ShouldContain("preserves the exact original instant");

        string messages = File.ReadAllText(Path.Combine(moduleRoot, "Web/src/messages.ts"));
        messages.ShouldContain("fieldDueDate: 'Due date'");
        messages.ShouldContain("fieldDueDate: 'Date d’échéance'");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("organizationId:guid:required")]
    [DataRow("total:money:required")]
    [DataRow("status:enum(Draft,Draft):required")]
    [DataRow("name:string:required:optional")]
    [DataRow("notes:string:max(0)")]
    [DataRow("params:string:required")]
    [DataRow("archive:string:required")]
    [DataRow("save:string:required")]
    [DataRow("await:string:required")]
    [DataRow("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab:string:required")]
    public void InvalidFieldContractsAreRejectedBeforeWorkspaceMutation(string fields)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string originalCatalog = File.ReadAllText(catalogPath);

        Should.Throw<ArgumentException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true,
            FieldSpecification: fields)));

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void InvalidOrDuplicateRequestsDoNotChangeTheWorkspace()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string originalCatalog = File.ReadAllText(catalogPath);

        Should.Throw<ArgumentException>(() => scaffolder.Create(new(
            "Invoicing", "Invoice", "Invoice Items", "organization", null, IncludeWeb: false)));

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void RestoreFailureRollsBackEveryWorkspaceMutation()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string solutionPath = Path.Combine(workspace.Root, "Kametal.slnx");
        string apiProject = Path.Combine(workspace.Root, "src/API/Kametal.Api/Kametal.Api.csproj");
        string originalCatalog = File.ReadAllText(catalogPath);
        string originalSolution = File.ReadAllText(solutionPath);
        string originalApiProject = File.ReadAllText(apiProject);
        string existingGeneratedModel = Path.Combine(
            workspace.Root, "web/packages/api-client/src/generated/models/existing.ts");
        string originalGeneratedModel = File.ReadAllText(existingGeneratedModel);

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new FailingRunner()).Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("workspace was restored");

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        File.ReadAllText(solutionPath).ShouldBe(originalSolution);
        File.ReadAllText(apiProject).ShouldBe(originalApiProject);
        File.ReadAllText(existingGeneratedModel).ShouldBe(originalGeneratedModel);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "tests/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void CancellationDuringRestoreRollsBackEveryWorkspaceMutation()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        using CancellationTokenSource cancellation = new();
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string solutionPath = Path.Combine(workspace.Root, "Kametal.slnx");
        string originalCatalog = File.ReadAllText(catalogPath);
        string originalSolution = File.ReadAllText(solutionPath);

        Should.Throw<OperationCanceledException>(() => new ModuleScaffolder(
            workspace.Root,
            new CancellingRunner(cancellation)).Create(new(
                "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false),
                cancellation.Token));

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        File.ReadAllText(solutionPath).ShouldBe(originalSolution);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "tests/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void ProcessRunnerTerminatesTheActiveCommandWhenCanceled()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("The stalled command fixture uses the Unix shell.");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        Should.Throw<OperationCanceledException>(() => new ProcessWorkspaceCommandRunner().Run(
            "/bin/sh",
            ["-c", "sleep 60"],
            Path.GetTempPath(),
            cancellation.Token));

        (DateTimeOffset.UtcNow - startedAt).ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void RestoreFailurePreservesForeignLockFileCreatedDuringTheTransaction()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string foreignLock = Path.Combine(workspace.Root, "external-restore/packages.lock.json");

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(
            workspace.Root,
            new ForeignLockFileFailingRunner(foreignLock)).Create(new(
                "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)));

        File.ReadAllText(foreignLock).ShouldBe("foreign restore output\n");
    }

    [TestMethod]
    public void RestoreFailureDeletesOnlyLockFileReservedByTheGenerator()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string ownedLock = Path.Combine(workspace.Root, "src/API/Kametal.Api/packages.lock.json");

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(
            workspace.Root,
            new OwnedLockFileFailingRunner(ownedLock)).Create(new(
                "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)));

        File.Exists(ownedLock).ShouldBeFalse();
    }

    [TestMethod]
    public void BackendVerificationFailureRestoresApplicationSpecificOpenApiOutput()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string openApi = Path.Combine(workspace.Root, "web/packages/api-client/openapi/Kametal.Api.json");
        string originalOpenApi = File.ReadAllText(openApi);

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(
            workspace.Root, new BackendVerificationFailingRunner()).Create(new(
                "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("workspace was restored");

        File.ReadAllText(openApi).ShouldBe(originalOpenApi);
        File.Exists(Path.Combine(workspace.Root, "web/packages/api-client/openapi/Unexpected.Api.json")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void WebVerificationFailureRestoresGeneratedArtifactsAndPackageGraph()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string webPackage = Path.Combine(workspace.Root, "web/apps/web/package.json");
        string webLock = Path.Combine(workspace.Root, "web/pnpm-lock.yaml");
        string openApi = Path.Combine(workspace.Root, "web/packages/api-client/openapi/Kametal.Api.json");
        string existingModel = Path.Combine(workspace.Root, "web/packages/api-client/src/generated/models/existing.ts");
        string newModel = Path.Combine(workspace.Root, "web/packages/api-client/src/generated/models/invoiceDto.ts");
        string assistantContract = Path.Combine(workspace.Root, "docs/generated/assistant-contract.json");
        Dictionary<string, string> originals = new[] { webPackage, webLock, openApi, existingModel, assistantContract }
            .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new WebVerificationFailingRunner()).Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true)))
            .Message.ShouldContain("workspace was restored");

        foreach ((string path, string contents) in originals)
            File.ReadAllText(path).ShouldBe(contents, $"Expected {path} to be restored byte-for-byte.");
        File.Exists(newModel).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "tests/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void EquivalentRequestsProduceDeterministicApplicationSpecificSource()
    {
        using ScaffolderWorkspace first = ScaffolderWorkspace.Create(includeWeb: true);
        using ScaffolderWorkspace second = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleCreateRequest request = new(
            "Invoicing", "Invoice", "invoice_items", "organization", "Organization invoices.", IncludeWeb: true);

        new ModuleScaffolder(first.Root, new SuccessfulRunner()).Create(request);
        new ModuleScaffolder(second.Root, new SuccessfulRunner()).Create(request);

        Snapshot(first.Root, "src/Modules/Invoicing").ShouldBe(
            Snapshot(second.Root, "src/Modules/Invoicing"));
        string source = File.ReadAllText(Path.Combine(
            first.Root, "src/Modules/Invoicing/Kametal.Modules.Invoicing.Domain/InvoiceRecord.cs"));
        source.ShouldContain("namespace Kametal.Modules.Invoicing.Domain;");
        source.ShouldNotContain("Trykatch.Modules.Invoicing");
        File.ReadAllText(Path.Combine(first.Root, "src/Modules/Invoicing/Web/src/messages.ts"))
            .ShouldContain("fr:");
    }

    [TestMethod]
    public void GeneratedManifestMatchesTheCommittedGoldenContract()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Inventory", "Product", "inventory_items", "organization", null, IncludeWeb: true));

        string actual = File.ReadAllText(Path.Combine(workspace.Root, "src/Modules/Inventory/trykatch.module.json"));
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden/inventory.module.json"));
        actual.ShouldBe(expected);
    }

    [TestMethod]
    [DataRow("Web", "Invoice")]
    [DataRow("Invoicing", "Infrastructure")]
    public void ReservedNamesAreRejectedBeforeMutation(string module, string entity)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string originalCatalog = File.ReadAllText(Path.Combine(workspace.Root, "trykatch.modules.json"));

        Should.Throw<ArgumentException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            module, entity, "invoices", "organization", null, IncludeWeb: false)));

        File.ReadAllText(Path.Combine(workspace.Root, "trykatch.modules.json")).ShouldBe(originalCatalog);
    }

    [TestMethod]
    public void UnsafeResourceLengthAndMultilineDescriptionsAreRejectedBeforeMutation()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());

        Should.Throw<ArgumentException>(() => scaffolder.Create(new(
            "Invoicing", "Invoice", new string('a', 36), "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("35 characters");
        Should.Throw<ArgumentException>(() => scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", "First line\nSecond line", IncludeWeb: false)))
            .Message.ShouldContain("single line");

        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    [DataRow("order")]
    [DataRow("user")]
    public void PostgreSqlKeywordsAreRejectedBeforeMutation(string resource)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string originalCatalog = File.ReadAllText(catalogPath);

        Should.Throw<ArgumentException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Invoicing", "Invoice", resource, "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("PostgreSQL keyword");

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void ResourceRelationsAlreadyOwnedByAnotherModuleAreRejectedBeforeMutation()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string manifestPath = Path.Combine(workspace.Root, "src/Modules/Projects/trykatch.module.json");
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["capabilities"] = new JsonArray("api", "data");
        manifest["dataOwnership"] = new JsonObject
        {
            ["default"] = "organization",
            ["resources"] = new JsonArray(new JsonObject
            {
                ["name"] = "invoices",
                ["schema"] = "app",
                ["table"] = "invoices",
                ["ownership"] = "organization",
                ["entityType"] = "Kametal.Modules.Projects.Domain.Project",
                ["isolationPolicy"] = "invoices_organization_isolation"
            })
        };
        File.WriteAllText(manifestPath, manifest.ToJsonString(new() { WriteIndented = true }) + "\n");
        string catalogPath = Path.Combine(workspace.Root, "trykatch.modules.json");
        string originalCatalog = File.ReadAllText(catalogPath);

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("already declares data relation 'app.invoices'");

        File.ReadAllText(catalogPath).ShouldBe(originalCatalog);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
    }

    [TestMethod]
    public void SolutionEditFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.SolutionEdit, includeWeb: false);

    [TestMethod]
    public void RegistrationAndCatalogFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.RegistrationAndCatalogMutation, includeWeb: false);

    [TestMethod]
    public void RestorePhaseFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.Restore, includeWeb: false);

    [TestMethod]
    public void WebInstallPhaseFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.WebInstall, includeWeb: true);

    [TestMethod]
    public void WebVerificationPhaseFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.WebVerification, includeWeb: true);

    [TestMethod]
    public void DoctorValidationPhaseFailureRollsBackEveryMutation() =>
        AssertInjectedFailureRollsBack(ModuleCreationPhase.DoctorValidation, includeWeb: true);

    [TestMethod]
    public void SolutionOrderingDoesNotDependOnModuleCreationOrder()
    {
        using ScaffolderWorkspace first = ScaffolderWorkspace.Create(includeWeb: true);
        using ScaffolderWorkspace second = ScaffolderWorkspace.Create(includeWeb: true);

        CreateTwoModules(first.Root, "Zebra", "ZebraRecord", "zebra_records", "Alpha", "AlphaRecord", "alpha_records");
        CreateTwoModules(second.Root, "Alpha", "AlphaRecord", "alpha_records", "Zebra", "ZebraRecord", "zebra_records");

        File.ReadAllText(Path.Combine(first.Root, "Kametal.slnx"))
            .ShouldBe(File.ReadAllText(Path.Combine(second.Root, "Kametal.slnx")));
    }

    [TestMethod]
    public void SolutionOrderingPreservesRootCommentsAndProcessingInstructions()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string solutionPath = Path.Combine(workspace.Root, "Kametal.slnx");
        XDocument solution = XDocument.Load(solutionPath);
        XElement root = solution.Root!;
        root.Add(new XComment(" preserve this solution marker exactly "));
        root.Add(new XProcessingInstruction("trykatch", "preserve=true"));
        solution.Save(solutionPath);

        new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: false));

        XDocument updated = XDocument.Load(solutionPath);
        XNode[] rootNodes = updated.Root!.Nodes().ToArray();
        XComment comment = rootNodes.OfType<XComment>().Single();
        XProcessingInstruction instruction = rootNodes.OfType<XProcessingInstruction>().Single();
        comment.Value.ShouldBe(" preserve this solution marker exactly ");
        instruction.Target.ShouldBe("trykatch");
        instruction.Data.ShouldBe("preserve=true");
        Array.IndexOf(rootNodes, comment).ShouldBeLessThan(Array.IndexOf(rootNodes, instruction));
        Array.IndexOf(rootNodes, instruction).ShouldBeLessThan(
            Array.FindIndex(rootNodes, node => node is XElement));
    }

    [TestMethod]
    [DataRow("Aux", "Invoice")]
    [DataRow("Com1", "Invoice")]
    [DataRow("Invoicing", "Lpt9")]
    public void WindowsReservedNamesAreRejectedBeforeMutation(string module, string entity)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string catalog = File.ReadAllText(Path.Combine(workspace.Root, "trykatch.modules.json"));

        Should.Throw<ArgumentException>(() => new ModuleScaffolder(workspace.Root, new SuccessfulRunner()).Create(new(
            module, entity, "invoices", "organization", null, IncludeWeb: false)))
            .Message.ShouldContain("reserved");

        File.ReadAllText(Path.Combine(workspace.Root, "trykatch.modules.json")).ShouldBe(catalog);
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules", module)).ShouldBeFalse();
    }

    private static void AssertInjectedFailureRollsBack(ModuleCreationPhase phase, bool includeWeb)
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        string[] trackedPaths =
        [
            "Kametal.slnx",
            "trykatch.modules.json",
            "trykatch.modules.lock.json",
            "src/API/Kametal.Api/Kametal.Api.csproj",
            "src/API/Kametal.Migrator/Kametal.Migrator.csproj",
            "src/API/Kametal.Api/Modules/EnabledModules.cs",
            "src/API/Kametal.Migrator/Modules/EnabledModules.cs",
            "web/apps/web/package.json",
            "web/pnpm-lock.yaml",
            "web/src/modules.ts"
        ];
        Dictionary<string, byte[]?> before = trackedPaths.ToDictionary(
            relative => relative,
            relative => File.Exists(Path.Combine(workspace.Root, relative))
                ? File.ReadAllBytes(Path.Combine(workspace.Root, relative))
                : null,
            StringComparer.Ordinal);

        Should.Throw<InvalidOperationException>(() => new ModuleScaffolder(
            workspace.Root,
            new SuccessfulRunner(),
            new PhaseFailureInjector(phase)).Create(new(
                "Invoicing", "Invoice", "invoices", "organization", null, includeWeb)))
            .Message.ShouldContain("workspace was restored");

        foreach ((string relative, byte[]? expected) in before)
        {
            string path = Path.Combine(workspace.Root, relative);
            if (expected is null)
                File.Exists(path).ShouldBeFalse($"Expected {relative} to remain absent.");
            else
                File.ReadAllBytes(path).ShouldBe(expected, $"Expected {relative} to be restored byte-for-byte.");
        }
        Directory.Exists(Path.Combine(workspace.Root, "src/Modules/Invoicing")).ShouldBeFalse();
        Directory.Exists(Path.Combine(workspace.Root, "tests/Modules/Invoicing")).ShouldBeFalse();
    }

    private static void CreateTwoModules(
        string root,
        string firstModule,
        string firstEntity,
        string firstResource,
        string secondModule,
        string secondEntity,
        string secondResource)
    {
        ModuleScaffolder scaffolder = new(root, new SuccessfulRunner());
        scaffolder.Create(new(firstModule, firstEntity, firstResource, "organization", null, IncludeWeb: false));
        scaffolder.Create(new(secondModule, secondEntity, secondResource, "organization", null, IncludeWeb: false));
    }

    private static string Snapshot(string root, string relativeDirectory) => string.Join(
        "\n---\n",
        Directory.EnumerateFiles(Path.Combine(root, relativeDirectory), "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Path.Combine(root, relativeDirectory), path).Replace('\\', '/')
                + "\n" + File.ReadAllText(path)));

    private sealed class SuccessfulRunner : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            if (fileName == "pnpm" && arguments.Contains("install"))
                File.WriteAllText(Path.Combine(workingDirectory, "pnpm-lock.yaml"), "lockfileVersion: '9.0'\n");
            return new(0, string.Empty);
        }
    }

    private sealed class PhaseFailureInjector(ModuleCreationPhase phase) : IModuleCreationFailureInjector
    {
        public void ThrowIfRequested(ModuleCreationPhase currentPhase)
        {
            if (currentPhase == phase)
                throw new InvalidOperationException($"simulated {phase} failure");
        }
    }

    private sealed class FailingRunner : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory) =>
            new(17, "simulated restore failure");
    }

    private sealed class CancellingRunner(CancellationTokenSource cancellation) : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            cancellation.Cancel();
            return new(0, string.Empty);
        }
    }

    private sealed class ForeignLockFileFailingRunner(string foreignLock) : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(foreignLock)!);
            File.WriteAllText(foreignLock, "foreign restore output\n");
            return new(17, "simulated restore failure after a foreign restore wrote its lock file");
        }
    }

    private sealed class OwnedLockFileFailingRunner(string ownedLock) : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            File.Exists(ownedLock).ShouldBeTrue("The transaction must reserve missing project lock paths before restore.");
            File.WriteAllText(ownedLock, "generator restore output\n");
            return new(17, "simulated restore failure after writing a generator-owned lock file");
        }
    }

    private sealed class WebVerificationFailingRunner : IWorkspaceCommandRunner
    {
        private int _installCount;

        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            if (fileName == "pnpm" && arguments.Contains("install") && ++_installCount <= 2)
                File.WriteAllText(Path.Combine(workingDirectory, "pnpm-lock.yaml"), "mutated lock\n");
            if (fileName == "pnpm" && arguments.Contains("generate"))
            {
                string root = workingDirectory;
                File.WriteAllText(Path.Combine(root, "web/packages/api-client/openapi/Kametal.Api.json"), "mutated spec\n");
                File.WriteAllText(Path.Combine(root, "web/packages/api-client/src/generated/models/existing.ts"), "mutated model\n");
                File.WriteAllText(Path.Combine(root, "web/packages/api-client/src/generated/models/invoiceDto.ts"), "new model\n");
                File.WriteAllText(Path.Combine(root, "docs/generated/assistant-contract.json"), "mutated assistant contract\n");
            }

            return fileName == "pnpm" && arguments.Contains("typecheck")
                ? new(23, "simulated frontend verification failure")
                : new(0, string.Empty);
        }
    }

    private sealed class BackendVerificationFailingRunner : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory)
        {
            if (fileName == "dotnet" && arguments.Contains("build"))
            {
                File.WriteAllText(Path.Combine(
                    workingDirectory, "web/packages/api-client/openapi/Kametal.Api.json"), "mutated spec\n");
                File.WriteAllText(Path.Combine(
                    workingDirectory, "web/packages/api-client/openapi/Unexpected.Api.json"), "new spec\n");
            }

            return fileName == "dotnet" && arguments.Contains("test")
                ? new(29, "simulated generated test failure")
                : new(0, string.Empty);
        }
    }

    private sealed class ScaffolderWorkspace : IDisposable
    {
        private ScaffolderWorkspace(string root) => Root = root;
        public string Root { get; }

        public static ScaffolderWorkspace Create(bool includeWeb)
        {
            string root = Path.Combine(Path.GetTempPath(), $"trykatch-scaffolder-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(root, "src/API/Kametal.Api"));
            Directory.CreateDirectory(Path.Combine(root, "src/API/Kametal.Migrator"));
            Directory.CreateDirectory(Path.Combine(root, "src/Common/Kametal.Modules.Abstractions"));
            Directory.CreateDirectory(Path.Combine(root, "src/Common/Kametal.Modules.AspNetCore"));
            Directory.CreateDirectory(Path.Combine(root, "src/Modules/Projects/Kametal.Modules.Projects.Infrastructure"));
            File.WriteAllText(Path.Combine(root, "Kametal.slnx"), "<Solution />\n");
            File.WriteAllText(Path.Combine(root, "NuGet.Config"), "<configuration />\n");
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), "<Project />\n");
            File.WriteAllText(Path.Combine(root, "src/API/Kametal.Api/Kametal.Api.csproj"), "<Project />\n");
            File.WriteAllText(Path.Combine(root, "src/API/Kametal.Migrator/Kametal.Migrator.csproj"), "<Project />\n");
            File.WriteAllText(Path.Combine(root, "src/Common/Kametal.Modules.Abstractions/Kametal.Modules.Abstractions.csproj"), "<Project />\n");
            File.WriteAllText(Path.Combine(root, "src/Common/Kametal.Modules.AspNetCore/Kametal.Modules.AspNetCore.csproj"), "<Project />\n");
            string projectManifest = Path.Combine(root, "src/Modules/Projects/trykatch.module.json");
            File.WriteAllText(projectManifest, """
                {
                  "schemaVersion": 1, "id": "projects", "name": "Projects", "version": "1.0.0",
                  "description": "Projects", "publisher": "kametal",
                  "distribution": { "kind": "workspace", "license": "Apache-2.0" },
                  "compatibility": { "minimumHostVersion": "0.1.0", "maximumHostVersionExclusive": "1.0.0" },
                  "requires": [], "optionalDependencies": [], "capabilities": ["api"], "dataOwnership": null,
                  "artifacts": { "dotnetProject": "src/Modules/Projects/Kametal.Modules.Projects.Infrastructure/Kametal.Modules.Projects.Infrastructure.csproj", "webPackage": "" },
                  "entrypoints": { "dotnet": { "type": "Kametal.Modules.Projects.Infrastructure.ProjectsModule" }, "web": { "specifier": "", "export": "" } },
                  "contributions": { "permissions": [], "routes": [], "extensionPoints": [], "extensions": [], "assistantTools": [] }
                }
                """);
            File.WriteAllText(Path.Combine(root, "src/Modules/Projects/Kametal.Modules.Projects.Infrastructure/Kametal.Modules.Projects.Infrastructure.csproj"), "<Project />\n");
            File.WriteAllText(Path.Combine(root, "trykatch.modules.json"), """
                {
                  "schemaVersion": 1, "hostVersion": "0.1.0", "lockFile": "trykatch.modules.lock.json",
                  "hostCapabilities": ["business-blueprints-v1", "typed-tables-v1"],
                  "trustedPublishers": ["kametal"],
                  "outputs": {
                    "backend": "src/API/Kametal.Api/Modules/EnabledModules.cs", "backendNamespace": "Kametal.Api.Modules",
                    "dotnetModuleContractNamespace": "Kametal.Modules",
                    "migrator": "src/API/Kametal.Migrator/Modules/EnabledModules.cs", "migratorNamespace": "Kametal.Migrator.Modules",
                    "web": "web/src/modules.ts", "webModuleSdkSpecifier": "@kametal/module-sdk"
                  },
                  "modules": [{ "id": "projects", "manifest": "src/Modules/Projects/trykatch.module.json", "enabled": true }]
                }
                """);
            if (includeWeb)
            {
                Directory.CreateDirectory(Path.Combine(root, "web/apps/web"));
                Directory.CreateDirectory(Path.Combine(root, "web/src"));
                Directory.CreateDirectory(Path.Combine(root, "web/packages/api-client/openapi"));
                Directory.CreateDirectory(Path.Combine(root, "web/packages/api-client/src/generated/models"));
                Directory.CreateDirectory(Path.Combine(root, "docs/generated"));
                File.WriteAllText(Path.Combine(root, "web/apps/web/package.json"), "{ \"dependencies\": {} }\n");
                File.WriteAllText(Path.Combine(root, "web/package.json"), "{ \"name\": \"kametal-web\" }\n");
                File.WriteAllText(Path.Combine(root, "web/pnpm-lock.yaml"), "lockfileVersion: '9.0'\n");
                File.WriteAllText(Path.Combine(root, "web/packages/api-client/openapi/Kametal.Api.json"), "original spec\n");
                File.WriteAllText(Path.Combine(root, "web/packages/api-client/src/generated/models/existing.ts"), "original model\n");
                File.WriteAllText(Path.Combine(root, "docs/generated/assistant-contract.json"), "original assistant contract\n");
            }
            return new(root);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
