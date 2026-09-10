using TrykatchApp.Infrastructure.Modules;
using TrykatchApp.Modules;
using TrykatchApp.Modules.AspNetCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class TrykatchModuleCatalogTests
{
    [TestMethod]
    public void NonOrganizationResourcesCannotOptOutOfAccessPolicyValidation()
    {
        TrykatchModuleDescriptor descriptor = Module("reporting").Descriptor with
        {
            Capabilities = TrykatchModuleCapabilities.Data,
            DefaultDataOwnership = TrykatchDataOwnership.Platform,
            DataResources = [new("reports", "app", "reports", TrykatchDataOwnership.Platform)]
        };

        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([new StubModule(descriptor)]));
    }

    [TestMethod]
    public void CatalogOrdersRequiredModulesBeforeDependents()
    {
        TrykatchModuleCatalog catalog = new([
            Module("reporting", ["projects"]),
            Module("projects")
        ]);

        catalog.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsMissingDependencies()
    {
        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("requires missing module 'projects'");
    }

    [TestMethod]
    public void CatalogRejectsDependencyCycles()
    {
        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([
            Module("projects", ["reporting"]),
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("dependency cycle");
    }

    [TestMethod]
    public void CatalogOrdersInstalledOptionalDependenciesWithoutRequiringAbsentOnes()
    {
        TrykatchModuleCatalog withoutOptional = new([
            Module("reporting", optionalDependencies: ["projects"])
        ]);
        withoutOptional.Descriptors.Select(module => module.Id).ShouldBe(["reporting"]);

        TrykatchModuleCatalog withOptional = new([
            Module("reporting", optionalDependencies: ["projects"]),
            Module("projects")
        ]);
        withOptional.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsDuplicateExtensionPointHosts()
    {
        TrykatchExtensionPointDescriptor point = new(
            "workspace.summary.after",
            "Test host.",
            TrykatchExtensionPointKind.UiSlot,
            TrykatchModuleCapabilities.Web);

        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([
            Module("projects", extensionPoints: [point]),
            Module("reporting", extensionPoints: [point])
        ])).Message.ShouldContain("Duplicate Trykatch extension point id");
    }

    [TestMethod]
    public void ProjectsModuleRegistersItsApplicationAndDeclaresPermissionContributions()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddTrykatchModules(configuration, [new ProjectsModule()]);

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(global::TrykatchApp.Application.Projects.ProjectUseCases));
        ProjectsModule module = new();
        module.Descriptor.Permissions.Select(permission => permission.Key)
            .ShouldBe(["projects.read", "projects.manage"]);
    }

    [TestMethod]
    public void CatalogRejectsUnsafeStateChangingAssistantTools()
    {
        TrykatchModuleDescriptor descriptor = Module("reporting").Descriptor with
        {
            Capabilities = TrykatchModuleCapabilities.Api | TrykatchModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("reporting_delete", "Reporting_Delete", "Delete a report.", TrykatchAssistantToolRisk.Destructive, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([new StubModule(descriptor)]))
            .Message.ShouldContain("must require human confirmation");
    }

    [TestMethod]
    public void CatalogRejectsDuplicateAssistantToolNamesAcrossModules()
    {
        TrykatchModuleDescriptor first = Module("projects").Descriptor with
        {
            Capabilities = TrykatchModuleCapabilities.Api | TrykatchModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("trykatch_list_records", "Projects_List", "List projects.", TrykatchAssistantToolRisk.ReadOnly, false)
            ]
        };
        TrykatchModuleDescriptor second = Module("reporting").Descriptor with
        {
            Capabilities = TrykatchModuleCapabilities.Api | TrykatchModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("trykatch_list_records", "Reporting_List", "List reports.", TrykatchAssistantToolRisk.ReadOnly, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new TrykatchModuleCatalog([new StubModule(first), new StubModule(second)]))
            .Message.ShouldContain("Duplicate Trykatch assistant tool name");
    }

    private static StubModule Module(
        string id,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? optionalDependencies = null,
        IReadOnlyList<TrykatchExtensionPointDescriptor>? extensionPoints = null) =>
        new StubModule(new TrykatchModuleDescriptor(
            id,
            id,
            "1.0.0",
            $"{id} test module.",
            requires ?? [],
            optionalDependencies ?? [],
            TrykatchModuleCapabilities.Api | TrykatchModuleCapabilities.Web,
            extensionPoints ?? []));

    private sealed class StubModule(TrykatchModuleDescriptor descriptor) : ITrykatchModule
    {
        public TrykatchModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
