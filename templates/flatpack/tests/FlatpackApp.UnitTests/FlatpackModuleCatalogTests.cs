using FlatpackApp.Infrastructure.Modules;
using FlatpackApp.Modules;
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
