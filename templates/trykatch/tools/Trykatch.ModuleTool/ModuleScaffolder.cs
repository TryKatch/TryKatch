using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Trykatch.ModuleTool;

public sealed record ModuleCreateRequest(
    string ModuleName,
    string EntityName,
    string ResourceName,
    string Ownership,
    string? Description,
    bool IncludeWeb,
    string? FieldSpecification = null);

public sealed record ModuleCreationResult(
    string ModuleId,
    bool IncludeWeb,
    IReadOnlyList<string> CreatedPaths,
    ModuleDoctorReport Report);

public interface IModuleScaffolder
{
    ModuleCreationResult Create(ModuleCreateRequest request);
}

public sealed class ModuleScaffolder : IModuleScaffolder
{
    private readonly ModuleWorkspace _workspace;

    public ModuleScaffolder(string root) : this(root, new ProcessWorkspaceCommandRunner()) { }

    internal ModuleScaffolder(string root, IWorkspaceCommandRunner commandRunner) =>
        _workspace = new(root, commandRunner);

    public ModuleCreationResult Create(ModuleCreateRequest request) =>
        _workspace.CreateScaffoldedModule(request);
}

public sealed partial class ModuleWorkspace
{
    private static readonly string[] GeneratedModuleLayers = ["Application", "Domain", "Infrastructure", "IntegrationEvents", "Presentation"];
    private static readonly string[] GeneratedTestKinds = ["ArchitectureTests", "UnitTests"];
    private static readonly Regex DotnetIdentifier = new(
        "^[A-Z][A-Za-z0-9]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ResourceIdentifier = new(
        "^[a-z][a-z0-9]*(?:_[a-z0-9]+)*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> ReservedTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Api", "Application", "ArchitectureTests", "Common", "Con", "Domain", "Infrastructure",
        "IntegrationEvents", "Migrator", "Module", "Modules", "Nul", "Prn", "Presentation",
        "Tests", "UnitTests", "Web"
    };
    private static readonly HashSet<string> PostgreSqlReservedIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "all", "analyse", "analyze", "and", "any", "array", "as", "asc", "asymmetric", "both",
        "case", "cast", "check", "collate", "column", "constraint", "create", "current_catalog",
        "current_date", "current_role", "current_time", "current_timestamp", "current_user", "default",
        "deferrable", "desc", "distinct", "do", "else", "end", "except", "false", "fetch", "for",
        "foreign", "freeze", "from", "full", "grant", "group", "having", "ilike", "in", "initially",
        "intersect", "into", "is", "isnull", "lateral", "leading", "like", "limit", "localtime",
        "localtimestamp", "natural", "not", "notnull", "null", "offset", "on", "only", "or", "order",
        "placing", "primary", "references", "returning", "select", "session_user", "similar", "some",
        "symmetric", "system_user", "table", "tablesample", "then", "to", "trailing", "true", "union",
        "unique", "user", "using", "variadic", "verbose", "when", "where", "window", "with"
    };

    internal ModuleCreationResult CreateScaffoldedModule(ModuleCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using IDisposable mutationLock = AcquirePackageMutationLock();

        List<string> errors = [];
        ModuleCatalogFile? catalog = ReadJson<ModuleCatalogFile>(_catalogPath, errors, "module catalog");
        if (catalog is null) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        ValidateCatalog(catalog, errors);
        List<LoadedModule> installedModules = LoadModules(catalog, errors);
        ValidateModules(catalog, installedModules, errors);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        ScaffoldNames names = ValidateCreateRequest(request, catalog, installedModules);
        string moduleRoot = ResolveInsideRoot(Path.Combine("src", "Modules", names.Module));
        string testRoot = ResolveInsideRoot(Path.Combine("tests", "Modules", names.Module));
        if (Directory.Exists(moduleRoot) || Directory.Exists(testRoot))
            throw new InvalidOperationException($"Module '{names.Module}' already has source or test directories. No files were changed.");

        string stagingRoot = ResolveInsideRoot(Path.Combine(".trykatch", "staging", Guid.NewGuid().ToString("N")));
        string stagedModuleRoot = Path.Combine(stagingRoot, "src", "Modules", names.Module);
        string stagedTestRoot = Path.Combine(stagingRoot, "tests", "Modules", names.Module);
        string solution = ResolveSolution();
        bool hasWebSurface = HasWebSurface();
        HashSet<string> baselineLockFiles = Directory
            .EnumerateFiles(_root, "packages.lock.json", SearchOption.AllDirectories)
            .ToHashSet(StringComparer.Ordinal);
        string openApiRoot = ResolveInsideRoot("web/packages/api-client/openapi");
        HashSet<string> baselineOpenApiFiles = hasWebSurface
            ? EnumerateFilesIfPresent(openApiRoot).ToHashSet(StringComparer.Ordinal)
            : [];
        string generatedClientRoot = ResolveInsideRoot("web/packages/api-client/src/generated");
        HashSet<string> baselineGeneratedWebFiles = request.IncludeWeb
            ? EnumerateFilesIfPresent(generatedClientRoot).ToHashSet(StringComparer.Ordinal)
            : [];
        HashSet<string> mutationPaths = MutationPaths(catalog, null, null, baselineLockFiles);
        mutationPaths.Add(solution);
        mutationPaths.UnionWith(baselineOpenApiFiles);
        if (request.IncludeWeb)
        {
            mutationPaths.UnionWith(baselineGeneratedWebFiles);
            mutationPaths.Add(ResolveInsideRoot("docs/generated/assistant-contract.json"));
        }
        Dictionary<string, byte[]?> originals = CapturePaths(mutationPaths);

        try
        {
            RenderModule(stagedModuleRoot, stagedTestRoot, names, request.IncludeWeb);
            ValidateStagedManifest(Path.Combine(stagedModuleRoot, "trykatch.module.json"), names, request.IncludeWeb);
            Directory.CreateDirectory(Path.GetDirectoryName(moduleRoot)!);
            Directory.CreateDirectory(Path.GetDirectoryName(testRoot)!);
            Directory.Move(stagedModuleRoot, moduleRoot);
            Directory.Move(stagedTestRoot, testRoot);
            AddGeneratedProjectsToSolution(solution, names);

            string relativeManifest = Path.GetRelativePath(_root, Path.Combine(moduleRoot, "trykatch.module.json"))
                .Replace(Path.DirectorySeparatorChar, '/');
            catalog.Modules.Add(new ModuleRegistration { Id = names.ModuleId, Manifest = relativeManifest, Enabled = true });
            List<LoadedModule> modules = LoadModules(catalog, errors);
            ValidateModules(catalog, modules, errors);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

            string infrastructureProject = Path.Combine(moduleRoot,
                $"{names.RootNamespace}.Modules.{names.Module}.Infrastructure",
                $"{names.RootNamespace}.Modules.{names.Module}.Infrastructure.csproj");
            AddProjectReference(
                ResolveHostProject(catalog.Outputs.Backend, catalog.Outputs.BackendNamespace, "API"),
                infrastructureProject);
            AddProjectReference(
                ResolveHostProject(catalog.Outputs.Migrator, catalog.Outputs.MigratorNamespace, "migrator"),
                infrastructureProject);
            if (request.IncludeWeb)
            {
                UpsertWebDependency(
                    ResolveInsideRoot("web/apps/web/package.json"),
                    names.WebPackage,
                    "workspace:*");
            }

            WriteGeneratedRegistries(catalog, modules);
            WriteAtomic(_catalogPath, JsonSerializer.Serialize(catalog, JsonOptions) + "\n");
            RestorePackageGraphs(catalog, request.IncludeWeb);
            VerifyGeneratedBackendWorkspace(solution, names);
            if (request.IncludeWeb)
                VerifyGeneratedWebWorkspace();

            ModuleDoctorReport report = Inspect();
            if (!report.IsHealthy)
                throw new InvalidOperationException(string.Join(Environment.NewLine, report.Errors));

            return new(names.ModuleId, request.IncludeWeb,
                [Path.GetRelativePath(_root, moduleRoot), Path.GetRelativePath(_root, testRoot)], report);
        }
        catch (Exception exception)
        {
            RestoreFiles(originals);
            DeleteNewLockFiles(baselineLockFiles);
            if (hasWebSurface)
                DeleteNewGeneratedWebFiles(openApiRoot, baselineOpenApiFiles);
            if (request.IncludeWeb)
                DeleteNewGeneratedWebFiles(generatedClientRoot, baselineGeneratedWebFiles);
            DeleteDirectoryIfPresent(moduleRoot);
            DeleteDirectoryIfPresent(testRoot);
            if (request.IncludeWeb)
                _ = _commandRunner.Run("pnpm", ["install", "--frozen-lockfile", "--ignore-scripts"], ResolveInsideRoot("web"));
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

    private ScaffoldNames ValidateCreateRequest(
        ModuleCreateRequest request,
        ModuleCatalogFile catalog,
        IReadOnlyCollection<LoadedModule> installedModules)
    {
        string module = request.ModuleName?.Trim() ?? string.Empty;
        string entity = request.EntityName?.Trim() ?? string.Empty;
        string resource = request.ResourceName?.Trim() ?? string.Empty;
        if (!DotnetIdentifier.IsMatch(module))
            throw new ArgumentException("Module name must be a PascalCase .NET identifier, for example 'Invoicing'.");
        if (!DotnetIdentifier.IsMatch(entity))
            throw new ArgumentException("Entity name must be a PascalCase .NET identifier, for example 'Invoice'.");
        if (module.Length > 64)
            throw new ArgumentException("Module name cannot exceed 64 characters.");
        if (entity.Length > 64)
            throw new ArgumentException("Entity name cannot exceed 64 characters.");
        if (ReservedTypeNames.Contains(module))
            throw new ArgumentException($"Module name '{module}' is reserved by the Trykatch host or filesystem.");
        if (ReservedTypeNames.Contains(entity))
            throw new ArgumentException($"Entity name '{entity}' is reserved by the Trykatch host or filesystem.");
        if (!ResourceIdentifier.IsMatch(resource))
            throw new ArgumentException("--resource must be a lower-case snake_case PostgreSQL identifier, for example 'invoices'.");
        if (PostgreSqlReservedIdentifiers.Contains(resource))
            throw new ArgumentException($"Resource name '{resource}' is a PostgreSQL keyword. Choose a descriptive plural name such as '{resource}_records'.");
        if (resource.Length > 35)
            throw new ArgumentException("--resource cannot exceed 35 characters because generated PostgreSQL index names are limited to 63 bytes.");
        if (!string.Equals(request.Ownership, "organization", StringComparison.Ordinal))
            throw new ArgumentException("Version 1 of 'module create' requires '--ownership organization'.");

        string moduleId = ToKebabCase(module);
        if (catalog.Modules.Any(candidate => string.Equals(candidate.Id, moduleId, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Trykatch module '{moduleId}' is already registered. No files were changed.");
        LoadedModule? relationOwner = installedModules.FirstOrDefault(candidate =>
            candidate.Manifest.DataOwnership?.Resources.Any(dataResource =>
                string.Equals(dataResource.Schema, "app", StringComparison.Ordinal)
                && string.Equals(dataResource.Table, resource, StringComparison.Ordinal)) == true);
        if (relationOwner is not null)
            throw new InvalidOperationException(
                $"Trykatch module '{relationOwner.Manifest.Id}' already declares data relation 'app.{resource}'. No files were changed.");
        if (!TryParseVersion(catalog.HostVersion, out Version? hostVersion)
            || hostVersion < new Version(0, 1)
            || hostVersion >= new Version(1, 0))
            throw new InvalidOperationException($"Module generation does not support Trykatch host version '{catalog.HostVersion}'. Supported range is [0.1.0, 1.0.0).");

        const string backendSuffix = ".Api.Modules";
        if (!catalog.Outputs.BackendNamespace.EndsWith(backendSuffix, StringComparison.Ordinal))
            throw new InvalidOperationException("The module catalog backend namespace does not identify the application root namespace.");
        string rootNamespace = catalog.Outputs.BackendNamespace[..^backendSuffix.Length];
        string publisher = ToKebabCase(rootNamespace.Replace(".", string.Empty, StringComparison.Ordinal));

        string npmScope = ResolveNpmScope(catalog.Outputs.WebModuleSdkSpecifier);
        if (request.IncludeWeb && (!HasWebSurface() || string.IsNullOrWhiteSpace(npmScope)))
            throw new InvalidOperationException("--with-web requires a generated React workspace and a scoped module SDK package.");

        string description = string.IsNullOrWhiteSpace(request.Description)
            ? $"Organization-owned {module} records."
            : request.Description.Trim();
        if (description.Length > 500)
            throw new ArgumentException("--description cannot exceed 500 characters.");
        if (description.Any(char.IsControl))
            throw new ArgumentException("--description must be a single line without control characters.");
        IReadOnlyList<ModuleFieldDefinition> fields = ModuleFieldContract.Parse(request.FieldSpecification);
        return new(rootNamespace, npmScope, publisher, module, moduleId, entity, resource, description,
            $"@{npmScope}-modules/{moduleId}", request.IncludeWeb, fields);
    }

    private static void RenderModule(string moduleRoot, string testRoot, ScaffoldNames names, bool includeWeb)
    {
        Directory.CreateDirectory(moduleRoot);
        Directory.CreateDirectory(testRoot);
        WriteTemplate("Entity.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Domain"), $"{names.Entity}Record.cs"), names);
        WriteTemplate("UseCases.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Application"), $"{names.Module}UseCases.cs"), names);
        WriteTemplate("Changed.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "IntegrationEvents"), $"{names.Entity}Changed.cs"), names);
        WriteTemplate("Endpoints.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Presentation"), $"{names.Module}Endpoints.cs"), names);
        WriteTemplate("Module.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Module}Module.cs"), names);
        WriteTemplate("ModelContributor.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Module}ModelContributor.cs"), names);
        WriteTemplate("Store.cs", Path.Combine(moduleRoot, ProjectDirectory(names, "Infrastructure"), $"{names.Entity}Store.cs"), names);
        WriteProjectFiles(moduleRoot, names);
        WriteManifest(moduleRoot, names, includeWeb);
        WriteReadme(moduleRoot, names, includeWeb);
        WriteTestProjects(testRoot, names);
        if (includeWeb) WriteWebFiles(moduleRoot, names);
    }

    private static string ProjectDirectory(ScaffoldNames names, string layer) =>
        $"{names.RootNamespace}.Modules.{names.Module}.{layer}";

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
                ["permissions"] = new JsonArray(names.ModuleId + ".read", names.ModuleId + ".manage"),
                ["routes"] = routes,
                ["extensionPoints"] = new JsonArray(),
                ["extensions"] = new JsonArray(),
                ["assistantTools"] = new JsonArray()
            }
        };
        WriteUtf8(Path.Combine(moduleRoot, "trykatch.module.json"), manifest.ToJsonString(JsonOptions) + "\n");
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
                private static readonly ReflectionAssembly Application = typeof({{names.Module}}UseCases).Assembly;
                private static readonly ReflectionAssembly Events = typeof({{names.Entity}}Changed).Assembly;
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
        WriteUtf8(Path.Combine(webRoot, "package.json"), package.ToJsonString(JsonOptions) + "\n");
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
            folder.Add(new XElement("Project", new XAttribute("Path", path)));
    }

    private void VerifyGeneratedBackendWorkspace(string solution, ScaffoldNames names)
    {
        EnsureCommandSucceeded(
            _commandRunner.Run("dotnet", ["build", solution, "--no-restore", "--no-incremental"], _root),
            "build the generated backend module");
        string testRoot = ResolveInsideRoot(Path.Combine("tests", "Modules", names.Module));
        foreach (string kind in GeneratedTestKinds)
        {
            string projectName = $"{names.RootNamespace}.Modules.{names.Module}.{kind}";
            string project = Path.Combine(testRoot, projectName, projectName + ".csproj");
            EnsureCommandSucceeded(
                _commandRunner.Run("dotnet", ["test", project, "--no-build", "--no-restore"], _root),
                $"run generated {kind.Replace("Tests", " tests", StringComparison.Ordinal).ToLowerInvariant()}");
        }
    }

    private void VerifyGeneratedWebWorkspace()
    {
        EnsureCommandSucceeded(_commandRunner.Run("pnpm", ["install", "--frozen-lockfile", "--ignore-scripts"], ResolveInsideRoot("web")),
            "install the generated web workspace");
        foreach (string operation in new[] { "generate", "typecheck", "test", "build" })
            EnsureCommandSucceeded(_commandRunner.Run("pnpm", ["--dir", "web", operation], _root), $"run 'pnpm {operation}'");
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

    private static void WriteTemplate(string templateName, string outputPath, ScaffoldNames names)
    {
        Assembly assembly = typeof(ModuleScaffolder).Assembly;
        string suffix = ".Scaffolding.Templates." + templateName + ".tpl";
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

    private static string ToKebabCase(string value) => Regex.Replace(value,
        "(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", "-").ToLowerInvariant();

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed record ScaffoldNames(
        string RootNamespace, string NpmScope, string Publisher, string Module, string ModuleId,
        string Entity, string Resource, string Description, string WebPackage, bool IncludeWeb,
        IReadOnlyList<ModuleFieldDefinition> Fields);
}
