using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Modules;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed record OrganizationModuleEmailIntent(
    int Version, Guid DeliveryId, ModuleExecutionIdentity Identity, ModuleEmailProducerRef? Producer,
    ModuleEmailMessage Message, string RecipientAtAdmission)
{
    public override string ToString() => $"Organization email intent {DeliveryId:N}";
}

internal static class OrganizationModuleEmailIntentCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 32
    };

    public static string AdmissionKey(ModuleExecutionIdentity identity, ModuleEmailProducerRef? producer)
    {
        if (identity.ExecutionKind == ModuleExecutionKind.UserCommand)
            return $"user/{identity.EffectiveActorId:N}/{identity.MembershipId:N}";
        // Only non-secret authority identifiers are hashed. Private content and recipient are never indexed fingerprints.
        string authority = string.Join('\0', producer?.ModuleId, producer?.OperationKey,
            producer?.OperationVersion.ToString(CultureInfo.InvariantCulture), identity.GrantKey,
            identity.GrantVersion?.ToString(CultureInfo.InvariantCulture), identity.EffectiveActorId.ToString("N"));
        return "system/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
    }

    public static string StorageKey(ModuleEmailTemplateRef template) => $"{template.OwnerModuleId}/{template.TemplateKey}";

    public static EmailDeliveryProtectionContext Context(OrganizationEmailDeliveryRecord row) =>
        new(EmailDeliveryBoundary.Organization, row.OrganizationId, row.Id, row.TemplateKey, row.TemplateVersion);

    public static string Protect(OrganizationModuleEmailIntent intent, IEmailPayloadProtector protector) =>
        protector.Protect(new(EmailDeliveryBoundary.Organization, intent.Identity.OrganizationId, intent.DeliveryId,
            StorageKey(intent.Message.Template), intent.Message.Template.TemplateVersion), JsonSerializer.Serialize(intent, JsonOptions));

    public static bool TryRead(OrganizationEmailDeliveryRecord row, IEmailPayloadProtector protector,
        out OrganizationModuleEmailIntent? intent)
    {
        intent = null;
        try
        {
            string json = protector.Unprotect(Context(row), row.ProtectedIntent);
            if (Encoding.UTF8.GetByteCount(json) > 196_608) return false;
            OrganizationModuleEmailIntent? parsed = JsonSerializer.Deserialize<OrganizationModuleEmailIntent>(json, JsonOptions);
            if (parsed is null || parsed.Version != 1 || parsed.DeliveryId != row.Id
                || parsed.Identity.OrganizationId != row.OrganizationId || parsed.Identity.EffectiveActorId != row.ActorId
                || parsed.Message.Template is null || parsed.Message.LogicalDedupId != row.MessageId
                || StorageKey(parsed.Message.Template) != row.TemplateKey || parsed.Message.Template.TemplateVersion != row.TemplateVersion
                || AdmissionKey(parsed.Identity, parsed.Producer) != row.AdmissionIdentityKey
                || !IsRecipient(parsed.RecipientAtAdmission))
                return false;
            intent = parsed;
            return true;
        }
        catch (Exception failure) when (IsPayloadFailure(failure))
        {
            return false;
        }
    }

    public static bool SameIntent(OrganizationModuleEmailIntent existing, OrganizationModuleEmailIntent candidate) =>
        existing.Identity with { TraceCorrelation = null } == candidate.Identity with { TraceCorrelation = null }
        && existing.Producer == candidate.Producer
        && existing.Message with { TemplateData = default, Correlation = null }
            == candidate.Message with { TemplateData = default, Correlation = null }
        && JsonElement.DeepEquals(existing.Message.TemplateData, candidate.Message.TemplateData)
        && string.Equals(existing.RecipientAtAdmission, candidate.RecipientAtAdmission, StringComparison.OrdinalIgnoreCase);

    public static bool IsRecipient(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 254 || value.Any(char.IsControl)) return false;
        try { return string.Equals(new System.Net.Mail.MailAddress(value).Address, value, StringComparison.OrdinalIgnoreCase); }
        catch (FormatException) { return false; }
    }

    public static bool IsPayloadFailure(Exception failure) =>
        failure is CryptographicException or JsonException or ArgumentException or FormatException;
}
