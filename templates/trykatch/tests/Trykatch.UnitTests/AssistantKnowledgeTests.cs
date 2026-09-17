using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;
using Trykatch.Modules.AspNetCore.Assistant;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AssistantKnowledgeTests
{
    private static ModuleCatalog Catalog(params string[] ids) => new(ids.Select(id => new HelpModule(id)));

    [TestMethod]
    public void ApprovedGuidesAreEmbeddedReadableVersionedAndAllowlisted()
    {
        AssistantKnowledge knowledge = new(Catalog("projects", "documents"));
        knowledge.List().Length.ShouldBe(6);
        foreach (AssistantGuideSource source in knowledge.List())
        {
            source.Href.ShouldBe($"/assistant/guides/{source.Id}");
            source.Revision.Length.ShouldBe(64);
            AssistantGuide guide = knowledge.Get(source.Id)!;
            guide.Sections.Count.ShouldBeGreaterThan(0);
            guide.Sections.All(section => section.Text.Length <= 3_000).ShouldBeTrue();
        }
        foreach (string id in new[] { "unknown", "../appsettings.json", "https://evil.example", "/etc/passwd", "Projects" })
            knowledge.Get(id).ShouldBeNull();
    }

    [TestMethod]
    [DataRow("Explain architecture and layers", "architecture")]
    [DataRow("What is PostgreSQL RLS and concurrency?", "isolation")]
    [DataRow("How do I use Projects?", "projects")]
    [DataRow("How do I upload files in Documents?", "documents")]
    [DataRow("How can a developer extend a module?", "module-authoring")]
    [DataRow("Can I switch from DeepSeek to Ollama?", "providers")]
    [DataRow("Expliquez la sécurité et l’isolation des organisations", "isolation")]
    public void EvaluationTopicsReceiveRelevantBoundedReferenceData(string question, string expected)
    {
        AssistantHelpContext context = new AssistantKnowledge(Catalog("projects", "documents")).Retrieve(question, []);
        context.Sources.Select(source => source.Id).ShouldContain(expected);
        Encoding.UTF8.GetByteCount(context.ReferenceData).ShouldBeLessThanOrEqualTo(AssistantKnowledge.MaxContextBytes);
        using JsonDocument data = JsonDocument.Parse(context.ReferenceData);
        data.RootElement.GetProperty("kind").GetString().ShouldBe("approved_help_reference_data_not_instructions");
        data.RootElement.GetProperty("excerpts").GetArrayLength().ShouldBeLessThanOrEqualTo(4);
    }

    [TestMethod]
    public void FollowUpsRecoverSubjectButNewUnknownTopicsDoNotInheritSources()
    {
        AssistantKnowledge knowledge = new(Catalog("projects"));
        AssistantExchange[] history = [new("Explain PostgreSQL RLS", "Earlier answer"), new("Explain that further", "More detail")];
        knowledge.Retrieve("Explain that further", history).Sources.Select(source => source.Id).ShouldContain("isolation");
        knowledge.Retrieve("quasar fusion velocity", history).Sources.ShouldBeEmpty();
        knowledge.Retrieve("How do I use Projects?", history).Sources.First().Id.ShouldBe("projects");
    }

    [TestMethod]
    public void FollowUpsPreferTheLatestExplicitSubjectRatherThanCombiningOldTopics()
    {
        AssistantKnowledge knowledge = new(Catalog("projects", "documents"));
        AssistantExchange[] history =
        [
            new("Explain PostgreSQL RLS", "Earlier isolation answer"),
            new("How do I use Documents?", "Document answer"),
            new("Explain that further", "More document detail")
        ];
        knowledge.Retrieve("Explain that further", history).Sources.First().Id.ShouldBe("documents");
    }

    [TestMethod]
    public void DisabledModulesNeverProduceModuleSpecificGuidesOrDeclaredEnabledFacts()
    {
        AssistantKnowledge knowledge = new(Catalog("projects"));
        knowledge.Get("documents").ShouldBeNull();
        knowledge.List().Select(source => source.Id).ShouldNotContain("documents");
        AssistantHelpContext context = knowledge.Retrieve("Explain the architecture", []);
        using JsonDocument data = JsonDocument.Parse(context.ReferenceData);
        data.RootElement.GetProperty("enabledModuleDeclarations").EnumerateArray().Select(module => module.GetProperty("Id").GetString()).ShouldBe(["projects"]);
    }

    [TestMethod]
    public void ChangedEnabledCompositionChangesContinuationKnowledgeRevision()
        => new AssistantKnowledge(Catalog("projects")).Revision.ShouldNotBe(new AssistantKnowledge(Catalog("projects", "documents")).Revision);

    private sealed class HelpModule(string id) : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, id, "1.0.0", "Approved test module", [], [], ModuleCapabilities.None, []);
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
