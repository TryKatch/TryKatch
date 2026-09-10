using Trykatch.Infrastructure.Modules;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleCatalogTests
{
    [TestMethod]
    public void NonOrganizationResourcesCannotOptOutOfAccessPolicyValidation()
    {
        ModuleDescriptor descriptor = Module("reporting").Descriptor with
        {
            Capabilities = ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Platform,
            DataResources = [new("reports", "app", "reports", ModuleDataOwnership.Platform)]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void CatalogOrdersRequiredModulesBeforeDependents()
    {
        ModuleCatalog catalog = new([
            Module("reporting", ["projects"]),
            Module("projects")
        ]);

        catalog.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsMissingDependencies()
    {
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("requires missing module 'projects'");
    }

    [TestMethod]
    public void CatalogRejectsDependencyCycles()
    {
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([
            Module("projects", ["reporting"]),
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("dependency cycle");
    }

    [TestMethod]
    public void CatalogOrdersInstalledOptionalDependenciesWithoutRequiringAbsentOnes()
    {
        ModuleCatalog withoutOptional = new([
            Module("reporting", optionalDependencies: ["projects"])
        ]);
        withoutOptional.Descriptors.Select(module => module.Id).ShouldBe(["reporting"]);

        ModuleCatalog withOptional = new([
            Module("reporting", optionalDependencies: ["projects"]),
            Module("projects")
        ]);
        withOptional.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsDuplicateExtensionPointHosts()
    {
        ExtensionPointDescriptor point = new(
            "workspace.summary.after",
            "Test host.",
            ExtensionPointKind.UiSlot,
            ModuleCapabilities.Web);

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([
            Module("projects", extensionPoints: [point]),
            Module("reporting", extensionPoints: [point])
        ])).Message.ShouldContain("Duplicate Trykatch extension point id");
    }

    [TestMethod]
    public void ProjectsModuleRegistersItsApplicationAndDeclaresPermissionContributions()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddModules(configuration, [new ProjectsModule()]);

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(global::Trykatch.Modules.Projects.Application.ProjectUseCases));
        ProjectsModule module = new();
        module.Descriptor.Permissions.Select(permission => permission.Key)
            .ShouldBe(["projects.read", "projects.manage"]);
    }

    [TestMethod]
    public void CatalogRejectsUnsafeStateChangingAssistantTools()
    {
        ModuleDescriptor descriptor = Module("reporting").Descriptor with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("reporting_delete", "Reporting_Delete", "Delete a report.", AssistantToolRisk.Destructive, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(descriptor)]))
            .Message.ShouldContain("must require human confirmation");
    }

    [TestMethod]
    public void CatalogRejectsDuplicateAssistantToolNamesAcrossModules()
    {
        ModuleDescriptor first = Module("projects").Descriptor with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("trykatch_list_records", "Projects_List", "List projects.", AssistantToolRisk.ReadOnly, false)
            ]
        };
        ModuleDescriptor second = Module("reporting").Descriptor with
        {
            Capabilities = ModuleCapabilities.Api | ModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("trykatch_list_records", "Reporting_List", "List reports.", AssistantToolRisk.ReadOnly, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new StubModule(first), new StubModule(second)]))
            .Message.ShouldContain("Duplicate Trykatch assistant tool name");
    }

    private static StubModule Module(
        string id,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? optionalDependencies = null,
        IReadOnlyList<ExtensionPointDescriptor>? extensionPoints = null) =>
        new StubModule(new ModuleDescriptor(
            id,
            id,
            "1.0.0",
            $"{id} test module.",
            requires ?? [],
            optionalDependencies ?? [],
            ModuleCapabilities.Api | ModuleCapabilities.Web,
            extensionPoints ?? []));

    private sealed class StubModule(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
