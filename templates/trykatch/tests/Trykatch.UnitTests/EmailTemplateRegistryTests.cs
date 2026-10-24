#if TRYKATCH_EMAIL
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Infrastructure.Modules.Email;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class EmailTemplateRegistryTests
{
    [TestMethod]
    public void RegisteredRenderingUsesCanonicalDataAndSyntheticPreviewData()
    {
        FixtureRenderer renderer = new();
        EmailTemplateRegistry registry = new(Catalog(), [new(Template(), renderer)], [new(Policy(), new FixturePolicy())]);
        registry.TryGetPolicy(Policy(), out var policy).ShouldBeTrue();
        policy.ShouldNotBeNull();
        registry.TryRender(Template(), JsonSerializer.SerializeToElement(new { title = "Current source" }), out ModuleEmailContent? rendered)
            .ShouldBeTrue();
        rendered.ShouldNotBeNull().TextBody.ShouldBe("Current source");
        registry.TryPreview(Template(), out ModuleEmailContent? preview).ShouldBeTrue();
        preview.ShouldNotBeNull().TextBody.ShouldBe("Synthetic preview");
    }

    [TestMethod]
    public void MissingDuplicateAndUndeclaredAdaptersFailClosed()
    {
        ModuleEmailTemplateBinding template = new(Template(), new FixtureRenderer());
        ModuleEmailDisclosurePolicyBinding policy = new(Policy(), new FixturePolicy());
        Should.Throw<InvalidOperationException>(() => new EmailTemplateRegistry(Catalog(), [], [policy]));
        Should.Throw<InvalidOperationException>(() => new EmailTemplateRegistry(Catalog(), [template], []));
        Should.Throw<InvalidOperationException>(() => new EmailTemplateRegistry(Catalog(), [template, template], [policy]));
        Should.Throw<InvalidOperationException>(() => new EmailTemplateRegistry(Catalog(), [template], [policy, policy]));
        Should.Throw<InvalidOperationException>(() => new EmailTemplateRegistry(Catalog(),
            [template with { Template = new("messages", "missing", 1) }], [policy]));
    }

    [TestMethod]
    public void UnknownVersionsOversizedDataAndInvalidContentCannotRender()
    {
        FixtureRenderer renderer = new();
        EmailTemplateRegistry registry = new(Catalog(), [new(Template(), renderer)], [new(Policy(), new FixturePolicy())]);
        registry.TryRender(Template() with { TemplateVersion = 2 },
            JsonSerializer.SerializeToElement(new { title = "Title" }), out _).ShouldBeFalse();
        registry.TryRender(Template(), JsonSerializer.SerializeToElement(new { title = new string('x', 1025) }), out _).ShouldBeFalse();
        registry.TryRender(Template(), JsonSerializer.SerializeToElement("not an object"), out _).ShouldBeFalse();
        renderer.InvalidSubject = true;
        registry.TryPreview(Template(), out _).ShouldBeFalse();
    }

    private static ModuleEmailTemplateRef Template() => new("messages", "message.notice", 1);
    private static ModuleEmailDisclosurePolicyRef Policy() => new("messages", "message.disclose", 1);
    private static ModuleCatalog Catalog() => new([new FixtureModule()]);

    private sealed class FixtureModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("messages", "Messages", "1.0.0", "Fixture", [], [],
            ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
        {
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            DataResources = [new("messages", "app", "messages", ModuleDataOwnership.Organization, IsolationPolicy: "messages_isolation")],
            EmailTemplates = [new("messages", "message.notice", 1, 1024)],
            EmailDisclosurePolicies = [new("messages", "message.disclose", 1, ["messages"], [], [Template()], [])]
        };
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private sealed class FixtureRenderer : IModuleEmailTemplateRenderer
    {
        public bool InvalidSubject { get; set; }
        public JsonElement PreviewData => JsonSerializer.SerializeToElement(new { title = "Synthetic preview" });
        public ModuleEmailContent Render(JsonElement canonicalData) => new(
            InvalidSubject ? "Invalid\r\nsubject" : "Approved subject", canonicalData.GetProperty("title").GetString()!);
    }

    private sealed class FixturePolicy : IModuleEmailDisclosurePolicy
    {
        public Task<ModuleEmailDisclosureDecision> ResolveAsync(ModuleEmailDisclosureContext context,
            ModuleEmailMessage message, CancellationToken cancellationToken) => Task.FromResult(new ModuleEmailDisclosureDecision(ModuleEmailDisclosureState.Denied));
    }
}
#endif
