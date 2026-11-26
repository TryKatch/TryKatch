using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Mailing;

namespace Trykatch.Infrastructure.Modules.Email;

internal sealed record InvitationEmailIntent(int Version, Guid DeliveryId, Guid OrganizationId, Guid ActorId,
    Guid InvitationId, Guid RoleId, string Recipient, string Token, string InvitationUrl)
{
    public override string ToString() => $"Invitation email delivery {DeliveryId:N}";
}

internal static class InvitationEmailIntentCodec
{
    public const string TemplateKey = "core.organization-invitation";
    public const int TemplateVersion = 1;
    public const string AdmissionKey = "core.organization-invitation";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public static bool IsToken(string? token) => token is { Length: 64 }
        && token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string Protect(InvitationEmailIntent intent, IEmailPayloadProtector protector)
    {
        string json = JsonSerializer.Serialize(intent, Options);
        if (Encoding.UTF8.GetByteCount(json) > 65_536)
            throw new ArgumentException("Invitation email intent exceeds the supported size.", nameof(intent));
        return protector.Protect(new(EmailDeliveryBoundary.Organization, intent.OrganizationId, intent.DeliveryId, TemplateKey, TemplateVersion), json);
    }

    public static bool TryRead(OrganizationEmailDeliveryRecord row, IEmailPayloadProtector protector, out InvitationEmailIntent? intent)
    {
        intent = null;
        if (row.TemplateKey != TemplateKey || row.TemplateVersion != TemplateVersion || row.AdmissionIdentityKey != AdmissionKey)
            return false;
        try
        {
            string json = protector.Unprotect(new(EmailDeliveryBoundary.Organization, row.OrganizationId, row.Id, TemplateKey, TemplateVersion), row.ProtectedIntent);
            if (Encoding.UTF8.GetByteCount(json) > 65_536) return false;
            InvitationEmailIntent? parsed = JsonSerializer.Deserialize<InvitationEmailIntent>(json, Options);
            if (parsed is null || parsed.Version != 1 || parsed.DeliveryId == Guid.Empty || parsed.DeliveryId != row.Id
                || parsed.OrganizationId == Guid.Empty || parsed.OrganizationId != row.OrganizationId
                || parsed.ActorId == Guid.Empty || parsed.ActorId != row.ActorId
                || parsed.InvitationId == Guid.Empty || parsed.InvitationId != row.MessageId || parsed.RoleId == Guid.Empty
                || !OrganizationModuleEmailIntentCodec.IsRecipient(parsed.Recipient) || !IsToken(parsed.Token)
                || string.IsNullOrEmpty(parsed.InvitationUrl) || parsed.InvitationUrl.Length > 8192)
                return false;
            intent = parsed;
            return true;
        }
        catch (Exception failure) when (OrganizationModuleEmailIntentCodec.IsPayloadFailure(failure)) { return false; }
    }

    public static bool SameIntent(InvitationEmailIntent accepted, InvitationEmailIntent candidate) =>
        accepted with { DeliveryId = candidate.DeliveryId, ActorId = candidate.ActorId } == candidate;
}
