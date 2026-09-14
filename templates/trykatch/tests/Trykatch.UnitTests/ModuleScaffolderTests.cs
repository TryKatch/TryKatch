using System.Text.Json.Nodes;
using Shouldly;
using Trykatch.ModuleTool;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleScaffolderTests
{
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
    }

    [TestMethod]
    public void FieldContractGeneratesTheSameBusinessShapeAcrossBackendDatabaseAndReact()
    {
        using ScaffolderWorkspace workspace = ScaffolderWorkspace.Create(includeWeb: true);
        ModuleScaffolder scaffolder = new(workspace.Root, new SuccessfulRunner());

        scaffolder.Create(new(
            "Invoicing", "Invoice", "invoices", "organization", null, IncludeWeb: true,
            FieldSpecification: "number:string:required:max(40),total:decimal:required,dueDate:date:required,status:enum(Draft,Sent,Paid):required,notes:string:optional:max(2000)"));

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
        useCases.ShouldContain("decimal? Total");
        useCases.ShouldContain("DateOnly? DueDate");
        useCases.ShouldContain("string? Status");
        useCases.ShouldContain("string Number");
        useCases.ShouldContain("decimal Total");
        useCases.ShouldContain("DateOnly DueDate");
        useCases.ShouldContain("string Status");
        useCases.ShouldContain("Enum.Parse<InvoiceStatus>");

        string persistence = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModelContributor.cs"));
        persistence.ShouldContain("entity.Property(record => record.Number).HasMaxLength(40)");
        persistence.ShouldContain("entity.Property(record => record.Total).HasPrecision(18, 2)");
        persistence.ShouldContain("entity.Property(record => record.Status).HasConversion<string>()");

        string migration = File.ReadAllText(Path.Combine(
            moduleRoot, "Kametal.Modules.Invoicing.Infrastructure/InvoicingModule.cs"));
        migration.ShouldContain("\"Number\" character varying(40) NOT NULL");
        migration.ShouldContain("\"Total\" numeric(18, 2) NOT NULL");
        migration.ShouldContain("\"DueDate\" date NOT NULL");
        migration.ShouldContain("\"Status\" character varying(5) NOT NULL");
        migration.ShouldContain("\"Notes\" character varying(2000) NULL");

        string web = File.ReadAllText(Path.Combine(moduleRoot, "Web/src/index.tsx"));
        web.ShouldContain("const [number, setNumber] = useState('')");
        web.ShouldContain("const [total, setTotal] = useState('')");
        web.ShouldContain("<option value=\"Draft\">{t('fieldStatusDraft')}</option>");
        web.ShouldContain("body: JSON.stringify({ number, total: Number(total), dueDate, status, notes: notes || null })");
        web.ShouldContain("record.number");

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

    private sealed class FailingRunner : IWorkspaceCommandRunner
    {
        public WorkspaceCommandResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory) =>
            new(17, "simulated restore failure");
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
