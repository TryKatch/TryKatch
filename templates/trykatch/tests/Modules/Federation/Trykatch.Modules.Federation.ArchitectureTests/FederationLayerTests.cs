using ReflectionAssembly = System.Reflection.Assembly;
using Shouldly;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Trykatch.Modules;
using Trykatch.Modules.Federation.Application;
using Trykatch.Modules.Federation.Domain;
using Trykatch.Modules.Federation.Infrastructure;
using Trykatch.Modules.Federation.IntegrationEvents;
using Trykatch.Modules.Federation.Presentation;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Trykatch.Modules.Federation.ArchitectureTests;

[TestClass]
public sealed class FederationLayerTests
{
    private static readonly ReflectionAssembly Domain = typeof(FederationConnection).Assembly;
    private static readonly ReflectionAssembly Application = typeof(FederationConnectionService).Assembly;
    private static readonly ReflectionAssembly Events = typeof(AssemblyMarker).Assembly;
    private static readonly ReflectionAssembly Presentation = typeof(FederationEndpoints).Assembly;
    private static readonly ReflectionAssembly Infrastructure = typeof(FederationModule).Assembly;
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
        entrypoints.ShouldHaveSingleItem().ShouldBe(typeof(FederationModule));
        entrypoints[0].IsSealed.ShouldBeTrue();
    }

    [TestMethod]
    public void CompiledReferencesRespectTheApplicationAndPresentationSeams()
    {
        string[] applicationReferences = Application.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        applicationReferences.ShouldNotContain(reference => IsAdapterAssembly(reference));

        string[] presentationReferences = Presentation.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        presentationReferences.ShouldNotContain(reference =>
            reference == "Trykatch.Modules.Federation.Domain"
            || reference == "Trykatch.Modules.Federation.Infrastructure");
    }

    [TestMethod]
    public void IntegrationEventsRemainAnExplicitEmptyContractAssembly()
    {
        System.Type[] contracts = Events.ExportedTypes.ToArray();
        contracts.ShouldHaveSingleItem().ShouldBe(typeof(AssemblyMarker));
        contracts[0].Namespace.ShouldBe("Trykatch.Modules.Federation.IntegrationEvents");
    }

    [TestMethod]
    public void InfrastructureExportsOnlyItsEntrypointAndRequiredHostSeams()
    {
        Infrastructure.ExportedTypes
            .Where(type => type != typeof(FederationModule))
            .ShouldAllBe(type => typeof(IApplicationModelContributor).IsAssignableFrom(type));
    }

    private static bool IsAdapterAssembly(string reference) =>
        reference.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
        || reference.StartsWith("Microsoft.Extensions.Http", StringComparison.Ordinal)
        || reference.StartsWith("Npgsql", StringComparison.Ordinal)
        || reference.EndsWith(".Infrastructure", StringComparison.Ordinal)
        || reference.EndsWith(".Presentation", StringComparison.Ordinal);
}
