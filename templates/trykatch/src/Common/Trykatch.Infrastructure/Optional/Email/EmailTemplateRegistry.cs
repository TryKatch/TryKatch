using System.Text;
using System.Text.Json;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed record EmailTemplateRegistration(ModuleEmailTemplateDescriptor Descriptor, IModuleEmailTemplateRenderer Renderer)
{
    public string StorageKey => $"{Descriptor.OwnerModuleId}/{Descriptor.TemplateKey}";
}

internal sealed record EmailDisclosurePolicyRegistration(ModuleEmailDisclosurePolicyDescriptor Descriptor, IModuleEmailDisclosurePolicy Adapter);

/// <summary>Only explicitly installed metadata and matching compiled adapters can render or resolve disclosure.</summary>
internal sealed class EmailTemplateRegistry
{
    private readonly Dictionary<ModuleEmailTemplateRef, EmailTemplateRegistration> templates = [];
    private readonly Dictionary<ModuleEmailDisclosurePolicyRef, EmailDisclosurePolicyRegistration> policies = [];

    public EmailTemplateRegistry(ModuleCatalog catalog, IEnumerable<ModuleEmailTemplateBinding> templateBindings,
        IEnumerable<ModuleEmailDisclosurePolicyBinding> policyBindings)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(templateBindings);
        ArgumentNullException.ThrowIfNull(policyBindings);
        Dictionary<ModuleEmailTemplateRef, ModuleEmailTemplateDescriptor> declaredTemplates = catalog.Descriptors
            .SelectMany(module => module.EmailTemplates).ToDictionary(
                value => new ModuleEmailTemplateRef(value.OwnerModuleId, value.TemplateKey, value.TemplateVersion));
        Dictionary<ModuleEmailDisclosurePolicyRef, ModuleEmailDisclosurePolicyDescriptor> declaredPolicies = catalog.Descriptors
            .SelectMany(module => module.EmailDisclosurePolicies).ToDictionary(
                value => new ModuleEmailDisclosurePolicyRef(value.OwnerModuleId, value.PolicyKey, value.PolicyVersion));

        foreach (ModuleEmailTemplateBinding binding in templateBindings)
        {
            if (binding is null || binding.Template is null || binding.Renderer is null
                || !declaredTemplates.TryGetValue(binding.Template, out ModuleEmailTemplateDescriptor? descriptor)
                || !templates.TryAdd(binding.Template, new(descriptor, binding.Renderer)))
                throw new InvalidOperationException("Email renderer binding is duplicate or undeclared.");
        }
        foreach (ModuleEmailDisclosurePolicyBinding binding in policyBindings)
        {
            if (binding is null || binding.Policy is null || binding.Adapter is null
                || !declaredPolicies.TryGetValue(binding.Policy, out ModuleEmailDisclosurePolicyDescriptor? descriptor)
                || !policies.TryAdd(binding.Policy, new(descriptor, binding.Adapter)))
                throw new InvalidOperationException("Email disclosure binding is duplicate or undeclared.");
        }
        if (templates.Count != declaredTemplates.Count || policies.Count != declaredPolicies.Count)
            throw new InvalidOperationException("Installed email declarations require their explicit renderer and disclosure adapters.");
    }

    public IReadOnlyList<ModuleEmailTemplateDescriptor> Templates => templates.Values
        .Select(value => value.Descriptor).OrderBy(value => value.OwnerModuleId, StringComparer.Ordinal)
        .ThenBy(value => value.TemplateKey, StringComparer.Ordinal).ThenBy(value => value.TemplateVersion).ToArray();

    public bool TryGetTemplate(ModuleEmailTemplateRef reference, out EmailTemplateRegistration? registration) =>
        templates.TryGetValue(reference, out registration);

    public bool TryGetPolicy(ModuleEmailDisclosurePolicyRef reference, out EmailDisclosurePolicyRegistration? registration) =>
        policies.TryGetValue(reference, out registration);

    public bool TryPreview(ModuleEmailTemplateRef reference, out ModuleEmailContent? content)
    {
        content = null;
        return templates.TryGetValue(reference, out EmailTemplateRegistration? registration)
            && TryRender(reference, registration.Renderer.PreviewData, out content);
    }

    public bool TryRender(ModuleEmailTemplateRef reference, JsonElement canonicalData, out ModuleEmailContent? content)
    {
        content = null;
        if (!templates.TryGetValue(reference, out EmailTemplateRegistration? registration)) return false;
        try
        {
            if (canonicalData.ValueKind != JsonValueKind.Object
                || Encoding.UTF8.GetByteCount(canonicalData.GetRawText()) > registration.Descriptor.MaximumTemplateDataBytes)
                return false;
            ModuleEmailContent rendered = registration.Renderer.Render(canonicalData);
            if (rendered is null || string.IsNullOrWhiteSpace(rendered.Subject) || rendered.Subject.Length > 240
                || rendered.Subject.Any(char.IsControl) || string.IsNullOrWhiteSpace(rendered.TextBody)
                || Encoding.UTF8.GetByteCount(rendered.TextBody) + Encoding.UTF8.GetByteCount(rendered.HtmlBody ?? "") > 262_144)
                return false;
            content = rendered;
            return true;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}
