using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Trykatch.Modules;

public sealed record ModuleEmailTemplateRef(string OwnerModuleId, string TemplateKey, int TemplateVersion);
public sealed record ModuleEmailDisclosurePolicyRef(string OwnerModuleId, string PolicyKey, int PolicyVersion);
public sealed record ModuleEmailProducerRef(string ModuleId, string OperationKey, int OperationVersion);

public sealed record ModuleEmailSourceRef(string ResourceKey, string ResourceId)
{
    public override string ToString() => $"Email source {ResourceKey}";
}

/// <summary>A bounded policy-local identifier resolved by the source owner, never a caller-selected SMTP address.</summary>
public sealed record ModuleEmailRecipientIntent(string RecipientKey)
{
    public override string ToString() => "Email recipient intent";
}

/// <summary>Untrusted intent selectors and data; the host supplies all execution authority separately.</summary>
public sealed record ModuleEmailMessage(
    Guid MessageId,
    Guid LogicalDedupId,
    ModuleEmailTemplateRef Template,
    ModuleEmailDisclosurePolicyRef DisclosurePolicy,
    ModuleEmailSourceRef Source,
    ModuleEmailRecipientIntent Recipient,
    JsonElement TemplateData,
    string? Correlation)
{
    public override string ToString() => $"Module email message {MessageId:N}";
}

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Preserves the approved public IModuleEmailQueue contract.")]
public interface IModuleEmailQueue
{
    Task<ModuleWorkReceipt> EnqueueAsync(ModuleEmailMessage message, CancellationToken cancellationToken = default);
}

public sealed record ModuleEmailTemplateDescriptor(
    string OwnerModuleId, string TemplateKey, int TemplateVersion, int MaximumTemplateDataBytes);

/// <summary>Exact source-owner consent for one system producer operation and template version pair.</summary>
public sealed record ModuleEmailSystemDisclosureBinding(ModuleEmailProducerRef Producer, ModuleEmailTemplateRef Template);

/// <summary>Empty user permissions makes this policy system-only; system producers require reciprocal finite grants.</summary>
public sealed record ModuleEmailDisclosurePolicyDescriptor(
    string OwnerModuleId,
    string PolicyKey,
    int PolicyVersion,
    IReadOnlyList<string> AllowedSourceResourceKeys,
    IReadOnlyList<string> RequiredUserPermissionKeys,
    IReadOnlyList<ModuleEmailTemplateRef> AllowedTemplates,
    IReadOnlyList<ModuleEmailSystemDisclosureBinding> AllowedSystemProducers);

/// <summary>Host-validated provenance for current disclosure checks. It is not supplied by an email caller.</summary>
public sealed record ModuleEmailDisclosureContext(
    Guid DeliveryId, ModuleExecutionIdentity Identity, ModuleEmailProducerRef? Producer);

public enum ModuleEmailDisclosureState { Allowed, Denied, Unavailable }

/// <summary>Allowed data is built by the source policy from current authorized state, never merged blindly from caller JSON.</summary>
public sealed record ModuleEmailDisclosureDecision(
    ModuleEmailDisclosureState State, string? Recipient = null, JsonElement CanonicalData = default)
{
    public override string ToString() => $"Email disclosure {State}";
}

public interface IModuleEmailDisclosurePolicy
{
    Task<ModuleEmailDisclosureDecision> ResolveAsync(
        ModuleEmailDisclosureContext context, ModuleEmailMessage message, CancellationToken cancellationToken);
}

public sealed record ModuleEmailContent(string Subject, string TextBody, string? HtmlBody = null)
{
    public override string ToString() => "Rendered email (private content)";
}

/// <summary>Compiled approved rendering; synthetic preview data must never load an actual queued message.</summary>
public interface IModuleEmailTemplateRenderer
{
    JsonElement PreviewData { get; }
    ModuleEmailContent Render(JsonElement canonicalData);
}

public sealed record ModuleEmailTemplateBinding(ModuleEmailTemplateRef Template, IModuleEmailTemplateRenderer Renderer);
public sealed record ModuleEmailDisclosurePolicyBinding(ModuleEmailDisclosurePolicyRef Policy, IModuleEmailDisclosurePolicy Adapter);
