using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Modules;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class ModuleEmailContractTests
{
    [TestMethod]
    public void CatalogAllowsSeparatePolicyAndTemplateOwnersAndDormantSystemBindings()
    {
        ModuleDescriptor source = SourceModule();
        ModuleEmailDisclosurePolicyDescriptor policy = source.EmailDisclosurePolicies.Single();
        source = source with
        {
            EmailDisclosurePolicies = [policy with
            {
                AllowedSystemProducers = [new(new("reports", "reports.notify", 1), TemplateReference())]
            }]
        };
        Should.NotThrow(() => new ModuleCatalog([new Stub(source), new Stub(TemplateModule())]));
        Should.NotThrow(() => new ModuleCatalog([new Stub(source)]));
    }

    [TestMethod]
    [DataRow("foreign-template-owner")]
    [DataRow("template-version")]
    [DataRow("template-size")]
    [DataRow("duplicate-template")]
    [DataRow("foreign-policy-owner")]
    [DataRow("policy-version")]
    [DataRow("undeclared-source")]
    [DataRow("unknown-permission")]
    [DataRow("unknown-installed-template")]
    [DataRow("missing-background-capability")]
    public void CatalogRejectsUnsafeEmailMetadata(string change)
    {
        ModuleDescriptor templateModule = TemplateModule();
        ModuleDescriptor source = SourceModule();
        ModuleEmailTemplateDescriptor template = templateModule.EmailTemplates.Single();
        ModuleEmailDisclosurePolicyDescriptor policy = source.EmailDisclosurePolicies.Single();
        switch (change)
        {
            case "foreign-template-owner": templateModule = templateModule with { EmailTemplates = [template with { OwnerModuleId = "chat" }] }; break;
            case "template-version": templateModule = templateModule with { EmailTemplates = [template with { TemplateVersion = 0 }] }; break;
            case "template-size": templateModule = templateModule with { EmailTemplates = [template with { MaximumTemplateDataBytes = 65_537 }] }; break;
            case "duplicate-template": templateModule = templateModule with { EmailTemplates = [template, template] }; break;
            case "foreign-policy-owner": source = source with { EmailDisclosurePolicies = [policy with { OwnerModuleId = "notifications" }] }; break;
            case "policy-version": source = source with { EmailDisclosurePolicies = [policy with { PolicyVersion = 0 }] }; break;
            case "undeclared-source": source = source with { EmailDisclosurePolicies = [policy with { AllowedSourceResourceKeys = ["foreign"] }] }; break;
            case "unknown-permission": source = source with { EmailDisclosurePolicies = [policy with { RequiredUserPermissionKeys = ["unknown.send"] }] }; break;
            case "unknown-installed-template": source = source with { EmailDisclosurePolicies = [policy with { AllowedTemplates = [new("notifications", "notice.missing", 1)] }] }; break;
            case "missing-background-capability": templateModule = templateModule with { Capabilities = ModuleCapabilities.None }; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new Stub(source), new Stub(templateModule)]));
    }

    [TestMethod]
    public void PublicQueueReturnsTheFrozenDurableReceiptAndPrivateDataIsRedacted()
    {
        typeof(IModuleEmailQueue).GetMethod(nameof(IModuleEmailQueue.EnqueueAsync))!.ReturnType
            .ShouldBe(typeof(Task<ModuleWorkReceipt>));
        const string secret = "private-recipient-token-body";
        JsonElement data = JsonSerializer.SerializeToElement(new { secret });
        ModuleEmailMessage message = new(Guid.NewGuid(), Guid.NewGuid(), TemplateReference(),
            new("chat", "chat.disclose", 1), new("messages", secret), new(secret), data, secret);
        message.ToString().ShouldNotContain(secret);
        new ModuleEmailContent(secret, secret, secret).ToString().ShouldNotContain(secret);
        new ModuleEmailDisclosureDecision(ModuleEmailDisclosureState.Allowed, secret, data).ToString()
            .ShouldNotContain(secret);
    }

    [TestMethod]
    public void QualifiedTemplateIdentityFitsTheProtectedStoragePurpose()
    {
        ModuleDescriptor module = TemplateModule();
        module = module with { EmailTemplates = [module.EmailTemplates.Single() with { TemplateKey = new string('x', 150) }] };
        Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new Stub(module)]));
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("unknown-policy")]
    [DataRow("missing-consent")]
    [DataRow("wrong-operation-version")]
    [DataRow("missing-source-dependency")]
    public void SystemEmailAuthorityRequiresReciprocalVersionedDeclarations(string change)
    {
        ModuleEmailDisclosurePolicyRef policyReference = new("chat", "chat.disclose", 1);
        ModuleEmailProducerRef producer = new("notifications", "notifications.emit", 1);
        ModuleDescriptor source = SourceModule();
        source = source with { EmailDisclosurePolicies = [source.EmailDisclosurePolicies.Single() with
        {
            AllowedSystemProducers = change == "missing-consent" ? []
                : [new(producer with { OperationVersion = change == "wrong-operation-version" ? 2 : 1 }, TemplateReference())]
        }] };
        ModuleDescriptor notifications = TemplateModule() with
        {
            Requires = change == "missing-source-dependency" ? [] : ["chat"],
            Capabilities = ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data,
            DefaultDataOwnership = ModuleDataOwnership.Organization,
            DataResources = [new("notifications", "app", "notifications", ModuleDataOwnership.Organization,
                IsolationPolicy: "notifications_isolation")],
            Operations = [new("notifications", "notifications.emit", 1, ModuleExecutionKind.ScheduledSystem,
                "notifications.payload", 1, [], "notifications", "notifications.mail", 1024)],
            SystemGrants = [new ModuleSystemGrantDescriptor("notifications.mail", 1, "notifications", Guid.NewGuid(),
                ["notifications.emit"], ["notifications"])
            {
                AllowedEmailDisclosurePolicies = [policyReference with { PolicyVersion = change == "unknown-policy" ? 2 : 1 }]
            }]
        };
        if (change == "valid")
        {
            Should.NotThrow(() => new ModuleCatalog([new Stub(source), new Stub(notifications)]));
            notifications.SystemGrants.Single().AllowedResourceKeys.ShouldBe(["notifications"]);
        }
        else
            Should.Throw<InvalidOperationException>(() => new ModuleCatalog([new Stub(source), new Stub(notifications)]));
    }

    private static ModuleEmailTemplateRef TemplateReference() => new("notifications", "notice.message", 1);

    private static ModuleDescriptor TemplateModule() => new("notifications", "Notifications", "1.0.0", "Fixture", [], [],
        ModuleCapabilities.BackgroundWork, [])
    {
        EmailTemplates = [new("notifications", "notice.message", 1, 4096)]
    };

    private static ModuleDescriptor SourceModule() => new("chat", "Chat", "1.0.0", "Fixture", [], [],
        ModuleCapabilities.BackgroundWork | ModuleCapabilities.Data, [])
    {
        DefaultDataOwnership = ModuleDataOwnership.Organization,
        DataResources = [new("messages", "app", "messages", ModuleDataOwnership.Organization,
            IsolationPolicy: "messages_isolation")],
        Permissions = [new("chat.email.send", "Send", "Fixture")],
        EmailDisclosurePolicies = [new("chat", "chat.disclose", 1, ["messages"], ["chat.email.send"],
            [TemplateReference()], [])]
    };

    private sealed class Stub(ModuleDescriptor descriptor) : IModule
    {
        public ModuleDescriptor Descriptor => descriptor;
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }
}
