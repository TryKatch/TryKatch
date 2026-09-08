using FlatpackApp.Infrastructure.Modules;
using FlatpackApp.Modules;
using FlatpackApp.Modules.AspNetCore;
using FlatpackApp.Modules.GettingStarted;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace FlatpackApp.UnitTests;

[TestClass]
public sealed class FlatpackModuleCatalogTests
{
    [TestMethod]
    public void CatalogOrdersRequiredModulesBeforeDependents()
    {
        FlatpackModuleCatalog catalog = new([
            Module("reporting", ["projects"]),
            Module("projects")
        ]);

        catalog.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsMissingDependencies()
    {
        Should.Throw<InvalidOperationException>(() => new FlatpackModuleCatalog([
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("requires missing module 'projects'");
    }

    [TestMethod]
    public void CatalogRejectsDependencyCycles()
    {
        Should.Throw<InvalidOperationException>(() => new FlatpackModuleCatalog([
            Module("projects", ["reporting"]),
            Module("reporting", ["projects"])
        ])).Message.ShouldContain("dependency cycle");
    }

    [TestMethod]
    public void CatalogOrdersInstalledOptionalDependenciesWithoutRequiringAbsentOnes()
    {
        FlatpackModuleCatalog withoutOptional = new([
            Module("reporting", optionalDependencies: ["projects"])
        ]);
        withoutOptional.Descriptors.Select(module => module.Id).ShouldBe(["reporting"]);

        FlatpackModuleCatalog withOptional = new([
            Module("reporting", optionalDependencies: ["projects"]),
            Module("projects")
        ]);
        withOptional.Descriptors.Select(module => module.Id).ShouldBe(["projects", "reporting"]);
    }

    [TestMethod]
    public void CatalogRejectsDuplicateExtensionPointHosts()
    {
        FlatpackExtensionPointDescriptor point = new(
            "workspace.summary.after",
            "Test host.",
            FlatpackExtensionPointKind.UiSlot,
            FlatpackModuleCapabilities.Web);

        Should.Throw<InvalidOperationException>(() => new FlatpackModuleCatalog([
            Module("projects", extensionPoints: [point]),
            Module("reporting", extensionPoints: [point])
        ])).Message.ShouldContain("Duplicate Flatpack extension point id");
    }

    [TestMethod]
    public void ProjectsModuleRegistersItsApplicationAndPermissionContributions()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddFlatpackModules(configuration, [new ProjectsModule()]);

        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(FlatpackApp.Application.Projects.ProjectUseCases));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(FlatpackApp.Application.Authorization.IPermissionDefinitionProvider)
            && descriptor.ImplementationType == typeof(FlatpackApp.Application.Projects.ProjectPermissionDefinitionProvider));
    }

    [TestMethod]
    public void ReferenceModuleComposesAfterProjectsAndRegistersItsHostAdapters()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        FlatpackModuleCatalog catalog = services.AddFlatpackModules(configuration,
            [new GettingStartedModule(), new ProjectsModule()]);

        catalog.Descriptors.Select(module => module.Id).ShouldBe(["projects", "getting-started"]);
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(IFlatpackOrganizationEndpointContributor));
        services.ShouldContain(descriptor => descriptor.ServiceType == typeof(FlatpackApp.Application.Authorization.IPermissionDefinitionProvider));
    }

    [TestMethod]
    public void CatalogRejectsUnsafeStateChangingAssistantTools()
    {
        FlatpackModuleDescriptor descriptor = Module("reporting").Descriptor with
        {
            Capabilities = FlatpackModuleCapabilities.Api | FlatpackModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("reporting_delete", "Reporting_Delete", "Delete a report.", FlatpackAssistantToolRisk.Destructive, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new FlatpackModuleCatalog([new StubModule(descriptor)]))
            .Message.ShouldContain("must require human confirmation");
    }

    [TestMethod]
    public void CatalogRejectsDuplicateAssistantToolNamesAcrossModules()
    {
        FlatpackModuleDescriptor first = Module("projects").Descriptor with
        {
            Capabilities = FlatpackModuleCapabilities.Api | FlatpackModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("flatpack_list_records", "Projects_List", "List projects.", FlatpackAssistantToolRisk.ReadOnly, false)
            ]
        };
        FlatpackModuleDescriptor second = Module("reporting").Descriptor with
        {
            Capabilities = FlatpackModuleCapabilities.Api | FlatpackModuleCapabilities.Assistant,
            AssistantTools =
            [
                new("flatpack_list_records", "Reporting_List", "List reports.", FlatpackAssistantToolRisk.ReadOnly, false)
            ]
        };

        Should.Throw<InvalidOperationException>(() => new FlatpackModuleCatalog([new StubModule(first), new StubModule(second)]))
            .Message.ShouldContain("Duplicate Flatpack assistant tool name");
    }

    private static StubModule Module(
        string id,
        IReadOnlyList<string>? requires = null,
        IReadOnlyList<string>? optionalDependencies = null,
        IReadOnlyList<FlatpackExtensionPointDescriptor>? extensionPoints = null) =>
        new StubModule(new FlatpackModuleDescriptor(
            id,
            id,
            "1.0.0",
            $"{id} test module.",
            requires ?? [],
            optionalDependencies ?? [],
            FlatpackModuleCapabilities.Api | FlatpackModuleCapabilities.Web,
            extensionPoints ?? []));

    private sealed class StubModule(FlatpackModuleDescriptor descriptor) : IFlatpackModule
    {
        public FlatpackModuleDescriptor Descriptor { get; } = descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
