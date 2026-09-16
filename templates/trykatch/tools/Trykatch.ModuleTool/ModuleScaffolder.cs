using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Trykatch.ModuleTool;

public sealed record ModuleCreateRequest(
    string ModuleName,
    string EntityName,
    string ResourceName,
    string Ownership,
    string? Description,
    bool IncludeWeb,
    string? FieldSpecification = null,
    string? BlueprintPath = null);

public sealed record ModuleCreationResult(
    string ModuleId,
    bool IncludeWeb,
    IReadOnlyList<string> CreatedPaths,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<string> Permissions,
    string StartCommand,
    ModuleDoctorReport Report);

internal sealed record ModuleCreationProgress(string Step, bool IsRollback = false);

public interface IModuleScaffolder
{
    ModuleCreationResult Create(ModuleCreateRequest request, CancellationToken cancellationToken = default);
}

internal enum ModuleCreationPhase
{
    SolutionEdit,
    RegistrationAndCatalogMutation,
    Restore,
    WebInstall,
    WebVerification,
    DoctorValidation
}

internal interface IModuleCreationFailureInjector
{
    void ThrowIfRequested(ModuleCreationPhase phase);
}

internal sealed class NoModuleCreationFailureInjector : IModuleCreationFailureInjector
{
    public void ThrowIfRequested(ModuleCreationPhase phase) { }
}

public sealed partial class ModuleScaffolder : IModuleScaffolder
{
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly IWorkspaceCommandRunner _commandRunner;
    private readonly IModuleCreationFailureInjector _failureInjector;
    private readonly ModuleWorkspace _workspace;
    private readonly Action<ModuleCreationProgress>? _progress;

    public ModuleScaffolder(string root) : this(root, new ProcessWorkspaceCommandRunner()) { }

    internal ModuleScaffolder(
        string root,
        IWorkspaceCommandRunner commandRunner,
        IModuleCreationFailureInjector? failureInjector = null,
        Action<ModuleCreationProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(commandRunner);
        _root = ModuleWorkspace.NormalizeWorkspaceRoot(root);
        _catalogPath = Path.Combine(_root, "try" + "katch.modules.json");
        _commandRunner = commandRunner;
        _failureInjector = failureInjector ?? new NoModuleCreationFailureInjector();
        _workspace = new(_root, commandRunner);
        _progress = progress;
    }

    public ModuleCreationResult Create(
        ModuleCreateRequest request,
        CancellationToken cancellationToken = default) =>
        CreateScaffoldedModule(request, cancellationToken);

    public void Validate(ModuleCreateRequest request)
    {
        List<string> errors = [];
        ModuleCatalogFile? catalog = _workspace.ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        _workspace.ValidateCatalog(catalog, errors);
        List<ModuleWorkspace.LoadedModule> modules = _workspace.LoadModules(catalog, errors);
        _workspace.ValidateModules(catalog, modules, errors);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        ScaffoldNames names = ValidateCreateRequest(request, catalog, modules);
        foreach (string prefix in new[] { "src", "tests" })
            if (Directory.Exists(_workspace.ResolveInsideRoot(Path.Combine(prefix, "Modules", names.Module))))
                throw new InvalidOperationException($"Module '{names.Module}' already has source or test directories. No files were changed.");
    }
}

public sealed partial class ModuleScaffolder
{
    private static readonly string[] GeneratedModuleLayers = ["Application", "Domain", "Infrastructure", "IntegrationEvents", "Presentation"];
    private static readonly string[] GeneratedTestKinds = ["ArchitectureTests", "UnitTests"];
    internal ModuleCreationResult CreateScaffoldedModule(
        ModuleCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress("Waiting for the workspace lock");
        using IDisposable mutationLock = _workspace.AcquirePackageMutationLock(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        ReportProgress("Validating module names, contracts and workspace");
        List<string> errors = [];
        ModuleCatalogFile? catalog = _workspace.ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        _workspace.ValidateCatalog(catalog, errors);
        List<ModuleWorkspace.LoadedModule> installedModules = _workspace.LoadModules(catalog, errors);
        _workspace.ValidateModules(catalog, installedModules, errors);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        ScaffoldNames names = ValidateCreateRequest(request, catalog, installedModules);
        string moduleRoot = _workspace.ResolveInsideRoot(Path.Combine("src", "Modules", names.Module));
        string testRoot = _workspace.ResolveInsideRoot(Path.Combine("tests", "Modules", names.Module));
        if (Directory.Exists(moduleRoot) || Directory.Exists(testRoot))
            throw new InvalidOperationException($"Module '{names.Module}' already has source or test directories. No files were changed.");

        string stagingRoot = _workspace.ResolveInsideRoot(Path.Combine(".trykatch", "staging", Guid.NewGuid().ToString("N")));
        string stagedModuleRoot = Path.Combine(stagingRoot, "src", "Modules", names.Module);
        string stagedTestRoot = Path.Combine(stagingRoot, "tests", "Modules", names.Module);
        string solution = _workspace.ResolveSolution();
        bool hasWebSurface = _workspace.HasWebSurface();
        using PackageLockFileOwnership packageLocks = _workspace.ReservePackageLockFiles();
        string openApiRoot = _workspace.ResolveInsideRoot("web/packages/api-client/openapi");
        HashSet<string> baselineOpenApiFiles = hasWebSurface
            ? EnumerateFilesIfPresent(openApiRoot).ToHashSet(StringComparer.Ordinal)
            : [];
        string generatedClientRoot = _workspace.ResolveInsideRoot("web/packages/api-client/src/generated");
        HashSet<string> baselineGeneratedWebFiles = request.IncludeWeb
            ? EnumerateFilesIfPresent(generatedClientRoot).ToHashSet(StringComparer.Ordinal)
            : [];
        HashSet<string> mutationPaths = _workspace.MutationPaths(catalog, null, null, packageLocks.ExistingFiles);
        mutationPaths.Add(solution);
        mutationPaths.UnionWith(baselineOpenApiFiles);
        if (request.IncludeWeb)
        {
            mutationPaths.UnionWith(baselineGeneratedWebFiles);
            mutationPaths.Add(_workspace.ResolveInsideRoot("docs/generated/assistant-contract.json"));
        }
        Dictionary<string, byte[]?> originals = ModuleWorkspace.CapturePaths(mutationPaths);

        try
        {
            ReportProgress("Rendering and validating module templates");
            cancellationToken.ThrowIfCancellationRequested();
            RenderModule(stagedModuleRoot, stagedTestRoot, names, request.IncludeWeb);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStagedManifest(Path.Combine(stagedModuleRoot, "trykatch.module.json"), names, request.IncludeWeb);
            string relativeManifest = Path.GetRelativePath(_root, Path.Combine(moduleRoot, "trykatch.module.json"))
                .Replace(Path.DirectorySeparatorChar, '/');
            catalog.Modules.Add(new ModuleRegistration { Id = names.ModuleId, Manifest = relativeManifest, Enabled = true });
            ValidateProjectedCatalog(catalog, installedModules, stagedModuleRoot, names, errors);
            if (errors.Count > 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            ReportProgress("Adding module projects to the solution");
            Directory.CreateDirectory(Path.GetDirectoryName(moduleRoot)!);
            Directory.CreateDirectory(Path.GetDirectoryName(testRoot)!);
            Directory.Move(stagedModuleRoot, moduleRoot);
            Directory.Move(stagedTestRoot, testRoot);
            AddGeneratedProjectsToSolution(solution, names);
            _failureInjector.ThrowIfRequested(ModuleCreationPhase.SolutionEdit);

            List<ModuleWorkspace.LoadedModule> modules = _workspace.LoadModules(catalog, errors);
            _workspace.ValidateModules(catalog, modules, errors);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            ReportProgress("Registering and enabling the module");
            string infrastructureProject = Path.Combine(moduleRoot,
                $"{names.RootNamespace}.Modules.{names.Module}.Infrastructure",
                $"{names.RootNamespace}.Modules.{names.Module}.Infrastructure.csproj");
            ModuleWorkspace.AddProjectReference(
                _workspace.ResolveHostProject(catalog.Outputs.Backend, catalog.Outputs.BackendNamespace, "API"),
                infrastructureProject);
            ModuleWorkspace.AddProjectReference(
                _workspace.ResolveHostProject(catalog.Outputs.Migrator, catalog.Outputs.MigratorNamespace, "migrator"),
                infrastructureProject);
            if (request.IncludeWeb)
            {
                ModuleWorkspace.UpsertWebDependency(
                    _workspace.ResolveInsideRoot("web/apps/web/package.json"),
                    names.WebPackage,
                    "workspace:*");
            }

            _workspace.WriteGeneratedRegistries(catalog, modules);
            ModuleWorkspace.WriteAtomic(_catalogPath, JsonSerializer.Serialize(catalog, ModuleWorkspace.SerializerOptions) + "\n");
            _failureInjector.ThrowIfRequested(ModuleCreationPhase.RegistrationAndCatalogMutation);
            _workspace.RestorePackageGraphs(catalog, request.IncludeWeb,
                progress: step => ReportProgress(step), cancellationToken: cancellationToken);
            _failureInjector.ThrowIfRequested(ModuleCreationPhase.Restore);
            VerifyGeneratedBackendWorkspace(solution, names, cancellationToken);
            if (request.IncludeWeb)
                VerifyGeneratedWebWorkspace(cancellationToken);

            ReportProgress("Checking module health with doctor");
            _failureInjector.ThrowIfRequested(ModuleCreationPhase.DoctorValidation);
            cancellationToken.ThrowIfCancellationRequested();
            ModuleDoctorReport report = _workspace.Inspect();
            if (!report.IsHealthy)
                throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));
            packageLocks.Complete();

            return new(
                names.ModuleId,
                request.IncludeWeb,
                [Path.GetRelativePath(_root, moduleRoot), Path.GetRelativePath(_root, testRoot)],
                [.. GeneratedEndpoints(names.Resource), .. names.Blueprint?.Workflow.Actions.Select(a => $"POST /api/v1/{names.Resource}/{{id}}/actions/{ScaffoldingNameRules.ToKebabCase(a.Name)}") ?? []],
                [names.ModuleId + ".read", names.ModuleId + ".manage", .. names.Blueprint?.Workflow.Actions.Select(a => names.ModuleId + "." + a.Permission).Distinct() ?? []],
                "trykatch start",
                report);
        }
        catch (Exception exception)
        {
            ReportProgress("Restoring the original workspace", isRollback: true);
            ModuleWorkspace.RestoreFiles(originals);
            if (hasWebSurface)
                DeleteNewGeneratedWebFiles(openApiRoot, baselineOpenApiFiles);
            if (request.IncludeWeb)
                DeleteNewGeneratedWebFiles(generatedClientRoot, baselineGeneratedWebFiles);
            DeleteDirectoryIfPresent(moduleRoot);
            DeleteDirectoryIfPresent(testRoot);
            if (request.IncludeWeb)
                _ = _commandRunner.Run(
                    "pnpm",
                    ["install", "--frozen-lockfile", "--ignore-scripts"],
                    _workspace.ResolveInsideRoot("web"),
                    CancellationToken.None);
            if (exception is OperationCanceledException)
                throw;
            throw new InvalidOperationException($"Module creation failed and the workspace was restored: {exception.Message}", exception);
        }
        finally
        {
            DeleteDirectoryIfPresent(stagingRoot);
            string stagingParent = Path.GetDirectoryName(stagingRoot)!;
            if (Directory.Exists(stagingParent) && !Directory.EnumerateFileSystemEntries(stagingParent).Any())
                Directory.Delete(stagingParent);
        }
    }

    private void ReportProgress(string step, bool isRollback = false) =>
        _progress?.Invoke(new(step, isRollback));

    private ScaffoldNames ValidateCreateRequest(
        ModuleCreateRequest request,
        ModuleCatalogFile catalog,
        IReadOnlyCollection<ModuleWorkspace.LoadedModule> installedModules)
    {
        ModuleName moduleName = ModuleName.Parse(request.ModuleName);
        EntityName entityName = EntityName.Parse(request.EntityName);
        ResourceName resourceName = ResourceName.Parse(request.ResourceName);
        string module = moduleName.Value;
        string entity = entityName.Value;
        string resource = resourceName.Value;
        if (!string.Equals(request.Ownership, "organization", StringComparison.Ordinal))
            throw new ArgumentException("Version 1 of 'module create' requires '--ownership organization'.");

        ModuleId validatedModuleId = ModuleId.From(moduleName);
        string moduleId = validatedModuleId.Value;
        if (catalog.Modules.Any(candidate => string.Equals(candidate.Id, moduleId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Trykatch module '{moduleId}' is already registered. No files were changed.");
        ModuleWorkspace.LoadedModule? relationOwner = installedModules.FirstOrDefault(candidate =>
            candidate.Manifest.DataOwnership?.Resources.Any(dataResource =>
                string.Equals(dataResource.Schema, "app", StringComparison.Ordinal)
                && string.Equals(dataResource.Table, resource, StringComparison.Ordinal)) == true);
        if (relationOwner is not null)
            throw new InvalidOperationException(
                $"Trykatch module '{relationOwner.Manifest.Id}' already declares data relation 'app.{resource}'. No files were changed.");
        if (!ModuleWorkspace.TryParseVersion(catalog.HostVersion, out Version? hostVersion)
            || hostVersion < new Version(0, 1)
            || hostVersion >= new Version(1, 0))
            throw new InvalidOperationException($"Module generation does not support Trykatch host version '{catalog.HostVersion}'. Supported range is [0.1.0, 1.0.0).");

        const string backendSuffix = ".Api.Modules";
        if (!catalog.Outputs.BackendNamespace.EndsWith(backendSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("The module catalog backend namespace does not identify the application root namespace.");
        ApplicationNamespace applicationNamespace = ApplicationNamespace.Parse(
            catalog.Outputs.BackendNamespace[..^backendSuffix.Length]);
        string rootNamespace = applicationNamespace.Value;
        string publisher = PublisherId.From(applicationNamespace).Value;

        string npmScope = ResolveNpmScope(catalog.Outputs.WebModuleSdkSpecifier);
        if (request.IncludeWeb && (!_workspace.HasWebSurface() || string.IsNullOrWhiteSpace(npmScope)))
            throw new InvalidOperationException("--with-web requires a generated React workspace and a scoped module SDK package.");

        string description = string.IsNullOrWhiteSpace(request.Description)
            ? $"Organization-owned {module} records."
            : request.Description.Trim();
        if (description.Length > 500)
            throw new ArgumentException("--description cannot exceed 500 characters.");
        if (description.Any(char.IsControl))
            throw new ArgumentException("--description must be a single line without control characters.");
        ModuleBlueprint? blueprint = request.BlueprintPath is null ? null : ModuleBlueprint.Load(request.BlueprintPath);
        if (blueprint is not null && catalog.HostCapabilities?.Contains("business-blueprints-v1", StringComparer.Ordinal) != true)
            throw new InvalidOperationException("This application does not support business-blueprints-v1. Updating the CLI does not upgrade an existing application's host or React SDK. Use a new coordinated template release or follow the blueprint host upgrade guide. No files were changed.");
        if (blueprint is not null && (blueprint.Module != module || blueprint.Entity != entity || blueprint.Resource != resource || blueprint.Ownership != request.Ownership))
            throw new ArgumentException("Blueprint identity must match the requested module.");
        if (blueprint is not null && request.FieldSpecification is not null)
            throw new ArgumentException("--fields cannot be combined with --blueprint.");
        IReadOnlyList<ModuleFieldDefinition> fields = blueprint?.Definitions ?? ModuleFieldContract.Parse(request.FieldSpecification);
        return new(rootNamespace, npmScope, publisher, module, moduleId, entity, resource, description,
            $"@{npmScope}-modules/{moduleId}", request.IncludeWeb, fields, blueprint);
    }

    private static void RenderModule(string moduleRoot, string testRoot, ScaffoldNames names, bool includeWeb)
    {
        Directory.CreateDirectory(moduleRoot);
        Directory.CreateDirectory(testRoot);
        WriteTemplate("Entity.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Domain"), $"{names.Entity}Record.cs"), names);
        WriteTemplate("UseCases.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Blueprint is null ? $"{names.Module}UseCases.cs" : $"{names.Entity}Contracts.cs"), names);
        WriteTemplate("IntegrationEvents.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "IntegrationEvents"), $"{names.Entity}IntegrationEvents.cs"), names);
        WriteTemplate("Endpoints.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Presentation"), $"{names.Module}Endpoints.cs"), names);
        WriteTemplate("Module.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Module}Module.cs"), names);
        WriteTemplate("ModelContributor.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Module}ModelContributor.cs"), names);
        WriteTemplate("Store.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Entity}Store.cs"), names);
        WriteProjectFiles(moduleRoot, names);
        WriteManifest(moduleRoot, names, includeWeb);
        WriteReadme(moduleRoot, names, includeWeb);
        WriteTestProjects(testRoot, names);
        if (includeWeb) WriteWebFiles(moduleRoot, names);
        if (names.Blueprint is not null)
        {
            WriteTemplate("BlueprintReadme.md", Path.Combine(moduleRoot, "README.md"), names);
            WriteUtf8(Path.Combine(moduleRoot, "module.blueprint.json"), JsonSerializer.Serialize(names.Blueprint, ModuleBlueprint.JsonOptions));
            WriteTemplate("BlueprintWorkflow.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Domain"), names.Entity + "Workflow.cs"), names);
            WriteTemplate("BlueprintActions.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Module + "Actions.cs"), names);
            WriteTemplate("BlueprintCrudCommands.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Entity + "Commands.cs"), names);
            WriteTemplate("BlueprintQueries.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Entity + "Queries.cs"), names);
            WriteTemplate("BlueprintReadModel.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Entity + "ReadModel.cs"), names);
            WriteTemplate("BlueprintOperations.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), names.Entity + "Operations.cs"), names);
            WriteTemplate("BlueprintActionEndpoints.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Presentation"), names.Module + "Actions.cs"), names);
            WriteTemplate("BlueprintWorkflowTests.cs", Path.Combine(testRoot, $"{names.RootNamespace}.Modules.{names.Module}.UnitTests", names.Entity + "WorkflowTests.cs"), names);
            if (includeWeb)
            {
                WriteTemplate("BlueprintWebWorkflow.ts", Path.Combine(moduleRoot, "Web/src/workflow.ts"), names);
                WriteTemplate("BlueprintWebWorkflowTests.ts", Path.Combine(moduleRoot, "Web/src/workflow.test.ts"), names);
            }
        }
    }

    private static string ProjectDirectory(ScaffoldNames names, string layer) =>
        $"{names.RootNamespace}.Modules.{names.Module}.{layer}";

    private static IReadOnlyList<string> GeneratedEndpoints(string resource) =>
    [
        $"GET /api/v1/{resource}",
        $"GET /api/v1/{resource}/{{id}}",
        $"POST /api/v1/{resource}",
        $"PUT /api/v1/{resource}/{{id}}",
        $"POST /api/v1/{resource}/{{id}}/archive",
        $"POST /api/v1/{resource}/{{id}}/restore",
        $"DELETE /api/v1/{resource}/{{id}}"
    ];

    private static void WriteProjectFiles(string moduleRoot, ScaffoldNames names)
    {
        string prefix = $"{names.RootNamespace}.Modules.{names.Module}";
        WriteUtf8(Path.Combine(moduleRoot, ProjectDirectory(names, "Domain"), prefix + ".Domain.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../../../Common/{{names.RootNamespace}}.Modules.Abstractions/{{names.RootNamespace}}.Modules.Abstractions.csproj" /></ItemGroup>
            </Project>
            """);
        WriteUtf8(Path.Combine(moduleRoot, ProjectDirectory(names, "IntegrationEvents"), prefix + ".IntegrationEvents.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        WriteUtf8(Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), prefix + ".Application.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="../../../Common/{{names.RootNamespace}}.Modules.Abstractions/{{names.RootNamespace}}.Modules.Abstractions.csproj" />
                <ProjectReference Include="../{{prefix}}.Domain/{{prefix}}.Domain.csproj" />
                <ProjectReference Include="../{{prefix}}.IntegrationEvents/{{prefix}}.IntegrationEvents.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteUtf8(Path.Combine(moduleRoot, ProjectDirectory(names, "Presentation"), prefix + ".Presentation.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../../../Common/{{names.RootNamespace}}.Modules.AspNetCore/{{names.RootNamespace}}.Modules.AspNetCore.csproj" />
                <ProjectReference Include="../{{prefix}}.Application/{{prefix}}.Application.csproj" />
              </ItemGroup>
            </Project>
            """);
        WriteUtf8(Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), prefix + ".Infrastructure.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageId>{{prefix}}</PackageId><Version>1.0.0</Version>
                <Description>{{System.Security.SecurityElement.Escape(names.Description)}}</Description>
                <PackageLicenseExpression>Apache-2.0</PackageLicenseExpression><IsPackable>true</IsPackable>
                <IsCompositeModulePackage>true</IsCompositeModulePackage>
              </PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /><PackageReference Include="Microsoft.EntityFrameworkCore.Relational" /></ItemGroup>
              <ItemGroup>
                <ProjectReference Include="../../../Common/{{names.RootNamespace}}.Modules.Abstractions/{{names.RootNamespace}}.Modules.Abstractions.csproj" PrivateAssets="all" />
                <ProjectReference Include="../../../Common/{{names.RootNamespace}}.Modules.AspNetCore/{{names.RootNamespace}}.Modules.AspNetCore.csproj" PrivateAssets="all" />
                <ProjectReference Include="../{{prefix}}.Application/{{prefix}}.Application.csproj" PrivateAssets="all" />
                <ProjectReference Include="../{{prefix}}.Domain/{{prefix}}.Domain.csproj" PrivateAssets="all" />
                <ProjectReference Include="../{{prefix}}.IntegrationEvents/{{prefix}}.IntegrationEvents.csproj" PrivateAssets="all" />
                <ProjectReference Include="../{{prefix}}.Presentation/{{prefix}}.Presentation.csproj" PrivateAssets="all" />
              </ItemGroup>
              <ItemGroup><None Include="../try&#x6B;atch.module.json" Pack="true" PackagePath="content/try&#x6B;atch.module.json" /></ItemGroup>
            </Project>
            """);
    }

    private static void WriteManifest(string moduleRoot, ScaffoldNames names, bool includeWeb)
    {
        string infrastructureProject = $"src/Modules/{names.Module}/{ProjectDirectory(names, "Infrastructure")}/{ProjectDirectory(names, "Infrastructure")}.csproj";
        JsonObject artifacts = new() { ["dotnetProject"] = infrastructureProject };
        JsonObject entrypoints = new() { ["dotnet"] = new JsonObject { ["type"] = $"{names.RootNamespace}.Modules.{names.Module}.Infrastructure.{names.Module}Module" } };
        JsonArray capabilities = ["api", "data"];
        JsonArray routes = [];
        if (includeWeb)
        {
            capabilities.Add("web");
            artifacts["webPackage"] = $"src/Modules/{names.Module}/Web/package.json";
            entrypoints["web"] = new JsonObject { ["specifier"] = names.WebPackage, ["export"] = ToCamelCase(names.Module) + "Module" };
            routes.Add(new JsonObject { ["id"] = names.ModuleId + ".list", ["path"] = "/" + names.Resource });
        }
        JsonObject manifest = new()
        {
            ["$schema"] = "https://trykatch.net/schemas/module-manifest.v1.json",
            ["schemaVersion"] = 1,
            ["id"] = names.ModuleId,
            ["name"] = names.Module,
            ["version"] = "1.0.0",
            ["description"] = names.Description,
            ["publisher"] = names.Publisher,
            ["distribution"] = new JsonObject { ["kind"] = "workspace", ["license"] = "Apache-2.0" },
            ["compatibility"] = new JsonObject { ["minimumHostVersion"] = "0.1.0", ["maximumHostVersionExclusive"] = "1.0.0" },
            ["requires"] = new JsonArray(),
            ["optionalDependencies"] = new JsonArray(),
            ["capabilities"] = capabilities,
            ["dataOwnership"] = new JsonObject
            {
                ["default"] = "organization",
                ["resources"] = new JsonArray(new JsonObject
                {
                    ["name"] = names.Resource,
                    ["schema"] = "app",
                    ["table"] = names.Resource,
                    ["ownership"] = "organization",
                    ["entityType"] = $"{names.RootNamespace}.Modules.{names.Module}.Domain.{names.Entity}Record",
                    ["isolationPolicy"] = names.Resource + "_organization_isolation"
                })
            },
            ["artifacts"] = artifacts,
            ["entrypoints"] = entrypoints,
            ["contributions"] = new JsonObject
            {
                ["permissions"] = new JsonArray(new[] { names.ModuleId + ".read", names.ModuleId + ".manage" }
                    .Concat(names.Blueprint?.Workflow.Actions.Select(a => names.ModuleId + "." + a.Permission).Distinct() ?? [])
                    .Select(value => JsonValue.Create(value)).ToArray()),
                ["routes"] = routes,
                ["extensionPoints"] = new JsonArray(),
                ["extensions"] = new JsonArray(),
                ["assistantTools"] = new JsonArray()
            }
        };
        WriteUtf8(Path.Combine(moduleRoot, "trykatch.module.json"), manifest.ToJsonString(ModuleWorkspace.SerializerOptions) + "\n");
    }

    private static void WriteReadme(string moduleRoot, ScaffoldNames names, bool includeWeb) =>
        WriteUtf8(Path.Combine(moduleRoot, "README.md"), $$"""
            # {{names.Module}} module

            {{names.Description}}

            This generated module owns `app.{{names.Resource}}`, enforces organization isolation through EF Core and PostgreSQL RLS, and exposes `/api/v1/{{names.Resource}}`.
            {{(includeWeb ? "The React contribution is available at `/" + names.Resource + "`." : "Add a web contribution later when the backend contract is stable.")}}
            """);

    private static void WriteTestProjects(string testRoot, ScaffoldNames names)
    {
        string prefix = $"{names.RootNamespace}.Modules.{names.Module}";
        string unitDirectory = Path.Combine(testRoot, prefix + ".UnitTests");
        string architectureDirectory = Path.Combine(testRoot, prefix + ".ArchitectureTests");
        WriteUtf8(Path.Combine(unitDirectory, prefix + ".UnitTests.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="MSTest" /><PackageReference Include="Shouldly" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Domain/{{prefix}}.Domain.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Infrastructure/{{prefix}}.Infrastructure.csproj" />
              <Compile Include="../../../ArchitectureTestSettings.cs" Link="MSTestSettings.cs" />
            </ItemGroup></Project>
            """);
        WriteUtf8(Path.Combine(unitDirectory, names.Module + "ModuleTests.cs"), $$"""
            using Shouldly;
            using {{names.RootNamespace}}.Modules;
            using {{prefix}}.Domain;
            using {{prefix}}.Infrastructure;

            namespace {{prefix}}.UnitTests;

            [TestClass]
            public sealed class {{names.Module}}ModuleTests
            {
                [TestMethod]
                public void DescriptorResolvesTheOrganizationOwnedEntity()
                {
                    {{names.Module}}Module module = new();
                    module.Descriptor.Id.ShouldBe("{{names.ModuleId}}");
                    module.Descriptor.Capabilities.ShouldBe({{(names.IncludeWeb ? "ModuleCapabilities.Api | ModuleCapabilities.Data | ModuleCapabilities.Web" : "ModuleCapabilities.Api | ModuleCapabilities.Data")}});
                    module.Descriptor.DataResources.Single().EntityType.ShouldBe(typeof({{names.Entity}}Record).FullName);
                }
            }
            """);
        WriteUtf8(Path.Combine(architectureDirectory, prefix + ".ArchitectureTests.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk"><ItemGroup>
              <PackageReference Include="MSTest" /><PackageReference Include="Shouldly" /><PackageReference Include="TngTech.ArchUnitNET" />
              <ProjectReference Include="../../../../src/Common/{{names.RootNamespace}}.Modules.Abstractions/{{names.RootNamespace}}.Modules.Abstractions.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Domain/{{prefix}}.Domain.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Application/{{prefix}}.Application.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.IntegrationEvents/{{prefix}}.IntegrationEvents.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Presentation/{{prefix}}.Presentation.csproj" />
              <ProjectReference Include="../../../../src/Modules/{{names.Module}}/{{prefix}}.Infrastructure/{{prefix}}.Infrastructure.csproj" />
              <Compile Include="../../../ArchitectureTestSettings.cs" Link="MSTestSettings.cs" />
            </ItemGroup></Project>
            """);
        WriteUtf8(Path.Combine(architectureDirectory, names.Module + "LayerTests.cs"), ArchitectureTestSource(names));
    }

    private static string ArchitectureTestSource(ScaffoldNames names)
    {
        string prefix = $"{names.RootNamespace}.Modules.{names.Module}";
        return $$"""
            using ReflectionAssembly = System.Reflection.Assembly;
            using ArchUnitNET.Domain;
            using ArchUnitNET.Fluent;
            using ArchUnitNET.Loader;
            using Shouldly;
            using {{names.RootNamespace}}.Modules;
            using {{prefix}}.Application;
            using {{prefix}}.Domain;
            using {{prefix}}.Infrastructure;
            using {{prefix}}.IntegrationEvents;
            using {{prefix}}.Presentation;
            using static ArchUnitNET.Fluent.ArchRuleDefinition;

            namespace {{prefix}}.ArchitectureTests;

            [TestClass]
            public sealed class {{names.Module}}LayerTests
            {
                private static readonly ReflectionAssembly Domain = typeof({{names.Entity}}Record).Assembly;
                private static readonly ReflectionAssembly Application = typeof({{(names.Blueprint is null ? names.Module + "UseCases" : "Create" + names.Entity + "CommandHandler")}}).Assembly;
                private static readonly ReflectionAssembly Events = typeof({{names.Entity}}Created).Assembly;
                private static readonly ReflectionAssembly Presentation = typeof({{names.Module}}Endpoints).Assembly;
                private static readonly ReflectionAssembly Infrastructure = typeof({{names.Module}}Module).Assembly;
                private static readonly Architecture Architecture = new ArchLoader().LoadAssemblies(Domain, Application, Events, Presentation, Infrastructure).Build();

                [TestMethod]
                public void LayerDependenciesPointInward()
                {
                    Types().That().ResideInAssembly(Domain).Should().NotDependOnAny(Types().That().ResideInAssembly(Application)).HasNoViolations(Architecture).ShouldBeTrue();
                    Types().That().ResideInAssembly(Presentation).Should().NotDependOnAny(Types().That().ResideInAssembly(Infrastructure)).HasNoViolations(Architecture).ShouldBeTrue();
                    Types().That().ResideInAssembly(Events).Should().NotDependOnAny(Types().That().ResideInAssembly(Infrastructure)).HasNoViolations(Architecture).ShouldBeTrue();
                }

                [TestMethod]
                public void InfrastructureHasOnePublicSealedModuleEntrypoint()
                {
                    System.Type[] entrypoints = Infrastructure.ExportedTypes.Where(type => typeof(IModule).IsAssignableFrom(type) && !type.IsAbstract).ToArray();
                    entrypoints.ShouldHaveSingleItem().ShouldBe(typeof({{names.Module}}Module));
                    entrypoints[0].IsSealed.ShouldBeTrue();
                }
            }
            """;
    }

    private static void WriteWebFiles(string moduleRoot, ScaffoldNames names)
    {
        string webRoot = Path.Combine(moduleRoot, "Web");
        WriteTemplate("WebIndex.tsx", Path.Combine(webRoot, "src", "index.tsx"), names);
        WriteTemplate("WebIndex.test.tsx", Path.Combine(webRoot, "src", "index.test.tsx"), names);
        WriteTemplate("WebDateTime.ts", Path.Combine(webRoot, "src", "dateTime.ts"), names);
        WriteTemplate("WebDateTime.test.ts", Path.Combine(webRoot, "src", "dateTime.test.ts"), names);
        WriteTemplate("WebMessages.ts", Path.Combine(webRoot, "src", "messages.ts"), names);
        JsonObject package = new()
        {
            ["name"] = names.WebPackage,
            ["version"] = "1.0.0",
            ["type"] = "module",
            ["exports"] = new JsonObject { ["."] = "./src/index.tsx" },
            ["scripts"] = new JsonObject { ["build"] = "tsc -b", ["test"] = "vitest run", ["typecheck"] = "tsc -b --pretty false" },
            ["dependencies"] = new JsonObject
            {
                [$"@{names.NpmScope}/api-client"] = "workspace:*",
                [$"@{names.NpmScope}/module-sdk"] = "workspace:*",
                [$"@{names.NpmScope}/ui"] = "workspace:*",
                ["@tanstack/react-query"] = "5.102.8",
                ["lucide-react"] = "1.42.0",
                ["react"] = "19.2.8"
            },
            ["devDependencies"] = new JsonObject
            {
                ["@types/react"] = "19.2.14",
                ["@types/react-dom"] = "19.2.3",
                ["react-dom"] = "19.2.8",
                ["typescript"] = "6.0.3",
                ["vitest"] = "5.0.0"
            },
            ["peerDependencies"] = new JsonObject { ["react"] = "^19.0.0" }
        };
        WriteUtf8(Path.Combine(webRoot, "package.json"), package.ToJsonString(ModuleWorkspace.SerializerOptions) + "\n");
        WriteUtf8(Path.Combine(webRoot, "tsconfig.json"), """
            {
              "compilerOptions": { "target": "ES2023", "module": "ESNext", "moduleResolution": "Bundler", "strict": true,
                "skipLibCheck": true, "jsx": "react-jsx", "declaration": true, "emitDeclarationOnly": true, "rootDir": "src", "outDir": "dist" },
              "include": ["src"]
            }
            """);
    }

    private static void AddGeneratedProjectsToSolution(string solutionPath, ScaffoldNames names)
    {
        XDocument document = XDocument.Load(solutionPath);
        XElement root = document.Root ?? throw new InvalidOperationException("The solution file has no root element.");
        string prefix = $"{names.RootNamespace}.Modules.{names.Module}";
        AddSolutionFolder(root, $"/src/Modules/{names.Module}/",
            GeneratedModuleLayers.Select(layer =>
                $"src/Modules/{names.Module}/{prefix}.{layer}/{prefix}.{layer}.csproj"));
        AddSolutionFolder(root, $"/tests/Modules/{names.Module}/",
            GeneratedTestKinds.Select(kind =>
                $"tests/Modules/{names.Module}/{prefix}.{kind}/{prefix}.{kind}.csproj"));
        NormalizeSolutionOrdering(root);
        WriteUtf8(solutionPath, document.ToString());
    }

    private static void AddSolutionFolder(XElement solution, string name, IEnumerable<string> projectPaths)
    {
        XElement? folder = solution.Elements("Folder").SingleOrDefault(element => (string?)element.Attribute("Name") == name);
        if (folder is null)
        {
            folder = new XElement("Folder", new XAttribute("Name", name));
            solution.Add(folder);
        }
        foreach (string path in projectPaths.Order(StringComparer.Ordinal))
        {
            if (!folder.Elements("Project").Any(project =>
                    string.Equals((string?)project.Attribute("Path"), path, StringComparison.Ordinal)))
                folder.Add(new XElement("Project", new XAttribute("Path", path)));
        }
    }

    private static void NormalizeSolutionOrdering(XElement solution)
    {
        foreach (XElement folder in solution.Elements("Folder"))
        {
            XElement[] projects = folder.Elements("Project")
                .OrderBy(project => (string?)project.Attribute("Path"), StringComparer.Ordinal)
                .ToArray();
            folder.Elements("Project").Remove();
            folder.Add(projects);
        }

        XElement[] elementSlots = solution.Elements().ToArray();
        XElement[] orderedElements = elementSlots
            .OrderBy(element => element.Name.LocalName, StringComparer.Ordinal)
            .ThenBy(element => (string?)element.Attribute("Name") ?? (string?)element.Attribute("Path"), StringComparer.Ordinal)
            .Select(element => new XElement(element))
            .ToArray();
        for (int index = 0; index < elementSlots.Length; index++)
            elementSlots[index].ReplaceWith(orderedElements[index]);
    }

    private void VerifyGeneratedBackendWorkspace(
        string solution,
        ScaffoldNames names,
        CancellationToken cancellationToken)
    {
        ReportProgress("Building the backend and generating OpenAPI");
        EnsureCommandSucceeded(
            _commandRunner.Run("dotnet", ["build", solution, "--no-restore", "--no-incremental"], _root, cancellationToken),
            "build the generated backend module");
        string testRoot = _workspace.ResolveInsideRoot(Path.Combine("tests", "Modules", names.Module));
        foreach (string kind in GeneratedTestKinds)
        {
            ReportProgress(kind == "ArchitectureTests"
                ? "Running generated architecture tests"
                : "Running generated unit tests");
            string projectName = $"{names.RootNamespace}.Modules.{names.Module}.{kind}";
            string project = Path.Combine(testRoot, projectName, projectName + ".csproj");
            EnsureCommandSucceeded(
                _commandRunner.Run("dotnet", ["test", project, "--no-build", "--no-restore"], _root, cancellationToken),
                $"run generated {kind.Replace("Tests", " tests", StringComparison.Ordinal).ToLowerInvariant()}");
        }
    }

    private void VerifyGeneratedWebWorkspace(CancellationToken cancellationToken)
    {
        ReportProgress("Installing frontend dependencies");
        EnsureCommandSucceeded(_commandRunner.Run("pnpm", ["install", "--frozen-lockfile", "--ignore-scripts"], _workspace.ResolveInsideRoot("web"), cancellationToken),
            "install the generated web workspace");
        _failureInjector.ThrowIfRequested(ModuleCreationPhase.WebInstall);
        foreach (string operation in new[] { "generate", "typecheck", "test", "build" })
        {
            ReportProgress(operation switch
            {
                "generate" => "Generating the frontend API client",
                "typecheck" => "Checking frontend types",
                "test" => "Running frontend tests",
                _ => "Building the frontend"
            });
            EnsureCommandSucceeded(_commandRunner.Run("pnpm", ["--dir", "web", operation], _root, cancellationToken), $"run 'pnpm {operation}'");
        }
        _failureInjector.ThrowIfRequested(ModuleCreationPhase.WebVerification);
    }

    private static void EnsureCommandSucceeded(WorkspaceCommandResult result, string operation)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not {operation}.{Environment.NewLine}{result.Output.Trim()}");
    }

    private static IEnumerable<string> EnumerateFilesIfPresent(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            : [];

    private static void DeleteNewGeneratedWebFiles(string directory, HashSet<string> baselineFiles)
    {
        foreach (string path in EnumerateFilesIfPresent(directory).Where(path => !baselineFiles.Contains(path)))
            File.Delete(path);
    }

    private static void ValidateStagedManifest(string manifestPath, ScaffoldNames names, bool includeWeb)
    {
        JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidOperationException("The rendered module manifest is empty.");
        if ((manifest["id"]?.GetValue<string>() ?? string.Empty) != names.ModuleId)
            throw new InvalidOperationException("The rendered module manifest identity is inconsistent.");
        bool declaresWeb = manifest["capabilities"]?.AsArray().Any(value => value?.GetValue<string>() == "web") == true;
        if (declaresWeb != includeWeb)
            throw new InvalidOperationException("The rendered module manifest has an inconsistent web capability.");
    }

    private void ValidateProjectedCatalog(
        ModuleCatalogFile catalog,
        IReadOnlyCollection<ModuleWorkspace.LoadedModule> installedModules,
        string stagedModuleRoot,
        ScaffoldNames names,
        List<string> errors)
    {
        string manifestPath = Path.Combine(stagedModuleRoot, "trykatch.module.json");
        ModuleManifest? manifest = _workspace.ReadJson<ModuleManifest>(manifestPath, errors, "rendered module manifest");
        if (manifest is null)
            return;

        ModuleRegistration registration = catalog.Modules.Single(module =>
            string.Equals(module.Id, names.ModuleId, StringComparison.Ordinal));
        List<ModuleWorkspace.LoadedModule> projectedModules = [.. installedModules, new(
            registration,
            manifest,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant(),
            "template-normalized-sha256")];

        _workspace.ValidateCatalog(catalog, errors);
        _workspace.ValidateModules(catalog, projectedModules, errors, requireCommittedWorkspaceArtifacts: false);
        ValidateProjectedArtifact(manifest.Id, manifest.Artifacts.DotnetProject, stagedModuleRoot, names, "dotnet project", errors);
        bool declaresWeb = manifest.Capabilities.Contains("web", StringComparer.Ordinal);
        if (declaresWeb)
            ValidateProjectedArtifact(manifest.Id, manifest.Artifacts.WebPackage, stagedModuleRoot, names, "web package", errors);
    }

    private static void ValidateProjectedArtifact(
        string moduleId,
        string artifactPath,
        string stagedModuleRoot,
        ScaffoldNames names,
        string subject,
        List<string> errors)
    {
        string expectedPrefix = $"src/Modules/{names.Module}/";
        if (!artifactPath.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            errors.Add($"Rendered module '{moduleId}' {subject} must be beneath '{expectedPrefix}'.");
            return;
        }

        string stagedArtifact = Path.Combine(
            stagedModuleRoot,
            artifactPath[expectedPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(stagedArtifact))
            errors.Add($"Rendered module '{moduleId}' {subject} '{artifactPath}' is missing from the staging tree.");
    }

    private static void WriteTemplate(string templateName, string outputPath, ScaffoldNames names)
    {
        Assembly assembly = typeof(ModuleScaffolder).Assembly;
        string selectedTemplate = names.Blueprint is not null && BlueprintRenderer.Replaces(templateName) ? "Blueprint" + templateName : templateName;
        string suffix = ".Scaffolding.Templates." + selectedTemplate + ".tpl";
        string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded scaffold template '{templateName}' is unavailable.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        string contents = reader.ReadToEnd()
            .Replace("__ROOT_NAMESPACE__", names.RootNamespace, StringComparison.Ordinal)
            .Replace("__MODULE_ID__", names.ModuleId, StringComparison.Ordinal)
            .Replace("__MODULE_CAMEL__", ToCamelCase(names.Module), StringComparison.Ordinal)
            .Replace("__MODULE__", names.Module, StringComparison.Ordinal)
            .Replace("__ENTITY_CAMEL__", ToCamelCase(names.Entity), StringComparison.Ordinal)
            .Replace("__ENTITY_LOWER__", names.Entity.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace("__ENTITY__", names.Entity, StringComparison.Ordinal)
            .Replace("__RESOURCE__", names.Resource, StringComparison.Ordinal)
            .Replace("__PUBLISHER__", names.Publisher, StringComparison.Ordinal)
            .Replace("__NPM_SCOPE__", names.NpmScope, StringComparison.Ordinal)
            .Replace("__MODULE_CAPABILITIES__", names.IncludeWeb
                ? "ModuleCapabilities.Api | ModuleCapabilities.Data | ModuleCapabilities.Web"
                : "ModuleCapabilities.Api | ModuleCapabilities.Data", StringComparison.Ordinal)
            .Replace("__DESCRIPTION__", EscapeDescription(templateName, names.Description), StringComparison.Ordinal);
        foreach ((string token, string value) in ModuleFieldRenderer.Render(names.Entity, names.Fields))
            contents = contents.Replace(token, value, StringComparison.Ordinal);
        if (names.Blueprint is not null)
        {
            foreach ((string token, string value) in BlueprintRenderer.Render(names.Blueprint, names.RootNamespace, names.ModuleId))
                contents = contents.Replace(token, value, StringComparison.Ordinal);
        }
        WriteUtf8(outputPath, contents);
    }

    private static string EscapeDescription(string templateName, string description) => templateName switch
    {
        "Module.cs" => description.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal),
        "WebIndex.tsx" => description.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal),
        _ => description
    };

    private static void WriteUtf8(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents.EndsWith('\n') ? contents : contents + "\n", new UTF8Encoding(false));
    }

    private static string ResolveNpmScope(string moduleSdkSpecifier)
    {
        if (!moduleSdkSpecifier.StartsWith('@') || !moduleSdkSpecifier.EndsWith("/module-sdk", StringComparison.Ordinal)) return string.Empty;
        return moduleSdkSpecifier[1..^"/module-sdk".Length];
    }

    private static string ToCamelCase(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record ScaffoldNames(
        string RootNamespace, string NpmScope, string Publisher, string Module, string ModuleId,
        string Entity, string Resource, string Description, string WebPackage, bool IncludeWeb,
        IReadOnlyList<ModuleFieldDefinition> Fields, ModuleBlueprint? Blueprint = null);
}
