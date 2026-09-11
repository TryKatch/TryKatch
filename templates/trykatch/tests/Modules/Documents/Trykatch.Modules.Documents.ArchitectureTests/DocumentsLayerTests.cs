using ReflectionAssembly = System.Reflection.Assembly;
using Shouldly;
using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Application;
using Trykatch.Modules.Documents.Domain;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Documents.IntegrationEvents;
using Trykatch.Modules.Documents.Presentation;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Trykatch.Modules.Documents.ArchitectureTests;

[TestClass]
public sealed class DocumentsLayerTests
{
    private static readonly ReflectionAssembly Domain = typeof(DocumentRecord).Assembly;
    private static readonly ReflectionAssembly Application = typeof(DocumentsUseCases).Assembly;
    private static readonly ReflectionAssembly Events = typeof(DocumentChanged).Assembly;
    private static readonly ReflectionAssembly Presentation = typeof(DocumentsEndpoints).Assembly;
    private static readonly ReflectionAssembly Infrastructure = typeof(DocumentsModule).Assembly;
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
        entrypoints.ShouldHaveSingleItem().ShouldBe(typeof(DocumentsModule));
        entrypoints[0].IsSealed.ShouldBeTrue();
    }

    [TestMethod]
    public void CompiledReferencesRespectTheApplicationAndPresentationSeams()
    {
        string[] applicationReferences = Application.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        applicationReferences.ShouldNotContain(reference => IsAdapterAssembly(reference));

        string[] presentationReferences = Presentation.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();
        presentationReferences.ShouldNotContain(reference =>
            reference == "Trykatch.Modules.Documents.Domain"
            || reference == "Trykatch.Modules.Documents.Infrastructure");
    }

    [TestMethod]
    public void IntegrationEventsOwnOnlyStableNamedContracts()
    {
        System.Type[] contracts = Events.ExportedTypes.ToArray();
        contracts.ShouldHaveSingleItem().ShouldBe(typeof(DocumentChanged));
        contracts.ShouldAllBe(contract => contract.Namespace == "Trykatch.Modules.Documents.IntegrationEvents");
        contracts.ShouldAllBe(contract => contract.Name.EndsWith("Changed", StringComparison.Ordinal));
    }

    [TestMethod]
    public void InfrastructureExportsOnlyItsEntrypointAndRequiredHostSeams()
    {
        Infrastructure.ExportedTypes
            .Where(type => type != typeof(DocumentsModule))
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
