using ReflectionAssembly = System.Reflection.Assembly;
using Shouldly;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Trykatch.Modules;
using Trykatch.Modules.Projects.Application;
using Trykatch.Modules.Projects.Domain;
using Trykatch.Modules.Projects.Infrastructure;
using Trykatch.Modules.Projects.IntegrationEvents;
using Trykatch.Modules.Projects.Presentation;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Trykatch.Modules.Projects.ArchitectureTests;

[TestClass]
public sealed class ProjectsLayerTests
{
    private static readonly ReflectionAssembly Domain = typeof(Project).Assembly;
    private static readonly ReflectionAssembly Application = typeof(ProjectUseCases).Assembly;
    private static readonly ReflectionAssembly Events = typeof(ProjectChanged).Assembly;
    private static readonly ReflectionAssembly Presentation = typeof(ProjectsEndpoints).Assembly;
    private static readonly ReflectionAssembly Infrastructure = typeof(ProjectsModule).Assembly;
    private static readonly Architecture Architecture = new ArchLoader().LoadAssemblies(Domain, Application, Events, Presentation, Infrastructure).Build();

    [TestMethod]
    public void LayerDependenciesPointInward()
    {
        Types().That().ResideInAssembly(Domain).Should().NotDependOnAny(Types().That().ResideInAssembly(Application)).HasNoViolations(Architecture).ShouldBeTrue();
        Types().That().ResideInAssembly(Domain).Should().NotDependOnAny(Types().That().ResideInAssembly(Presentation)).HasNoViolations(Architecture).ShouldBeTrue();
        Types().That().ResideInAssembly(Domain).Should().NotDependOnAny(Types().That().ResideInAssembly(Infrastructure)).HasNoViolations(Architecture).ShouldBeTrue();
        Types().That().ResideInAssembly(Presentation).Should().NotDependOnAny(Types().That().ResideInAssembly(Infrastructure)).HasNoViolations(Architecture).ShouldBeTrue();
        Types().That().ResideInAssembly(Events).Should().NotDependOnAny(Types().That().ResideInAssembly(Application)).HasNoViolations(Architecture).ShouldBeTrue();
        Types().That().ResideInAssembly(Events).Should().NotDependOnAny(Types().That().ResideInAssembly(Infrastructure)).HasNoViolations(Architecture).ShouldBeTrue();
    }

    [TestMethod]
    public void InfrastructureHasOnePublicSealedModuleEntrypoint()
    {
        System.Type[] entrypoints = Infrastructure.ExportedTypes.Where(type => typeof(IModule).IsAssignableFrom(type) && type is { IsAbstract: false }).ToArray();
        entrypoints.ShouldHaveSingleItem().ShouldBe(typeof(ProjectsModule));
        entrypoints[0].IsSealed.ShouldBeTrue();
    }
}
