#if TRYKATCH_EMAIL
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Trykatch.Application.Mailing;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Modules.Email;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class InvitationEmailPolicyTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly Guid OrganizationId = Guid.Parse("3325a7a1-2460-4587-81b2-7c37a1efcb14");
    private static readonly Guid ActorId = Guid.Parse("0980dfee-793a-4ed0-bcc0-9e232b83e1b6");
    private static readonly Guid DeliveryId = Guid.Parse("2e5314b9-72c7-46ae-b195-615c3ea83f42");
    private static readonly Guid InvitationId = Guid.Parse("5319721e-ae19-4c37-99c4-7af296ca52fb");
    private static readonly Guid RoleId = Guid.Parse("aaed5d62-1881-47e7-8cf1-769689a76d35");

    [TestMethod]
    public void ConfiguredHttpsOriginBuildsPathBaseInvitationLink()
    {
        InvitationEmailLinkPolicy links = Links("https://app.example.test/katch/", isDevelopment: false);

        links.IsAvailable.ShouldBeTrue();
        links.Build(Token).ShouldBe($"https://app.example.test/katch/invite/{Token}");
        links.IsValid($"https://app.example.test/katch/invite/{Token}", Token).ShouldBeTrue();
        links.IsValid($"https://attacker.example.test/katch/invite/{Token}", Token).ShouldBeFalse();
    }

    [TestMethod]
    public void DevelopmentAllowsConfiguredHttpOrigin()
    {
        InvitationEmailLinkPolicy links = Links("http://localhost:5080/app", isDevelopment: true);

        links.IsAvailable.ShouldBeTrue();
        links.Build(Token).ShouldBe($"http://localhost:5080/app/invite/{Token}");
    }

    [TestMethod]
    [DataRow("http://app.example.test", false)]
    [DataRow("ftp://app.example.test", true)]
    [DataRow("https://user:password@app.example.test", false)]
    [DataRow("https://app.example.test/path?next=home", false)]
    [DataRow("https://app.example.test/path#fragment", false)]
    [DataRow("file:///tmp/app", true)]
    public void InvalidOrUntrustedOriginsAreUnavailable(string value, bool isDevelopment)
    {
        InvitationEmailLinkPolicy links = Links(value, isDevelopment);

        links.IsAvailable.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => links.Build(Token));
    }

    [TestMethod]
    public void MissingOriginIsUnavailable()
    {
        InvitationEmailLinkPolicy links = Links(null, isDevelopment: true);

        links.IsAvailable.ShouldBeFalse();
    }

    [TestMethod]
    public void InvitationTokenMustBeExactly64LowercaseHexCharacters()
    {
        InvitationEmailIntentCodec.IsToken(Token).ShouldBeTrue();
        InvitationEmailIntentCodec.IsToken(new string('a', 63)).ShouldBeFalse();
        InvitationEmailIntentCodec.IsToken(new string('a', 65)).ShouldBeFalse();
        InvitationEmailIntentCodec.IsToken(new string('A', 64)).ShouldBeFalse();
        InvitationEmailIntentCodec.IsToken(new string('g', 64)).ShouldBeFalse();
        InvitationEmailIntentCodec.IsToken(null).ShouldBeFalse();
    }

    [TestMethod]
    public void ProtectedInvitationPacketRoundTripsAndRedactsRequestAndPacketStrings()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());
        InvitationEmailIntent intent = Intent();
        string protectedPacket = InvitationEmailIntentCodec.Protect(intent, protector);

        InvitationEmailIntentCodec.TryRead(Row(protectedPacket), protector, out InvitationEmailIntent? parsed).ShouldBeTrue();
        parsed.ShouldBe(intent);
        protectedPacket.ShouldNotContain(intent.Recipient);
        protectedPacket.ShouldNotContain(intent.Token);
        intent.ToString().ShouldNotContain(intent.Recipient);
        intent.ToString().ShouldNotContain(intent.Token);
        intent.ToString().ShouldNotContain(intent.InvitationUrl);
        OrganizationInvitationEmailRequest request = new(InvitationId, Token);
        request.ToString().ShouldNotContain(Token);
    }

    [TestMethod]
    public void RowPurposeTemplateAdmissionInvitationAndActorMismatchesAreRejected()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());
        InvitationEmailIntent intent = Intent();
        string protectedPacket = InvitationEmailIntentCodec.Protect(intent, protector);
        OrganizationEmailDeliveryRecord valid = Row(protectedPacket);

        InvitationEmailIntentCodec.TryRead(Copy(valid, id: Guid.NewGuid()), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, organizationId: Guid.NewGuid()), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, actorId: Guid.NewGuid()), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, messageId: Guid.NewGuid()), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, admission: "core.other-admission"), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, templateKey: "core.other-template"), protector, out _).ShouldBeFalse();
        InvitationEmailIntentCodec.TryRead(Copy(valid, templateVersion: InvitationEmailIntentCodec.TemplateVersion + 1), protector, out _).ShouldBeFalse();

        string wrongPurposePacket = protector.Protect(
            new(EmailDeliveryBoundary.Identity, OrganizationId, DeliveryId, InvitationEmailIntentCodec.TemplateKey,
                InvitationEmailIntentCodec.TemplateVersion), JsonSerializer.Serialize(intent));
        InvitationEmailIntentCodec.TryRead(Row(wrongPurposePacket), protector, out _).ShouldBeFalse();

        string wrongInvitationPacket = InvitationEmailIntentCodec.Protect(intent with { InvitationId = Guid.NewGuid() }, protector);
        InvitationEmailIntentCodec.TryRead(Row(wrongInvitationPacket), protector, out _).ShouldBeFalse();
        string wrongActorPacket = InvitationEmailIntentCodec.Protect(intent with { ActorId = Guid.NewGuid() }, protector);
        InvitationEmailIntentCodec.TryRead(Row(wrongActorPacket), protector, out _).ShouldBeFalse();
    }

    [TestMethod]
    public void ProtectedPacketRejectsInvalidTokenAndOversizedInvitationLink()
    {
        DataProtectionEmailPayloadProtector protector = new(new EphemeralDataProtectionProvider());
        string invalidTokenPacket = InvitationEmailIntentCodec.Protect(Intent() with { Token = new string('A', 64) }, protector);
        InvitationEmailIntentCodec.TryRead(Row(invalidTokenPacket), protector, out _).ShouldBeFalse();

        string oversizedLinkPacket = InvitationEmailIntentCodec.Protect(Intent() with
        {
            InvitationUrl = "https://app.example.test/" + new string('x', 8192)
        }, protector);
        InvitationEmailIntentCodec.TryRead(Row(oversizedLinkPacket), protector, out _).ShouldBeFalse();
    }

    private static InvitationEmailLinkPolicy Links(string? publicUrl, bool isDevelopment)
    {
        Dictionary<string, string?> values = [];
        if (publicUrl is not null) values["Application:PublicUrl"] = publicUrl;
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new InvitationEmailLinkPolicy(configuration, isDevelopment);
    }

    private static InvitationEmailIntent Intent() => new(1, DeliveryId, OrganizationId, ActorId, InvitationId, RoleId,
        "invitee@example.test", Token, $"https://app.example.test/invite/{Token}");

    private static OrganizationEmailDeliveryRecord Row(string protectedIntent) => new()
    {
        Id = DeliveryId,
        OrganizationId = OrganizationId,
        ActorId = ActorId,
        MessageId = InvitationId,
        AdmissionIdentityKey = InvitationEmailIntentCodec.AdmissionKey,
        TemplateKey = InvitationEmailIntentCodec.TemplateKey,
        TemplateVersion = InvitationEmailIntentCodec.TemplateVersion,
        ProtectedIntent = protectedIntent
    };

    private static OrganizationEmailDeliveryRecord Copy(OrganizationEmailDeliveryRecord source, Guid? id = null,
        Guid? organizationId = null, Guid? actorId = null, Guid? messageId = null, string? admission = null,
        string? templateKey = null, int? templateVersion = null) => new()
    {
        Id = id ?? source.Id,
        OrganizationId = organizationId ?? source.OrganizationId,
        ActorId = actorId ?? source.ActorId,
        MessageId = messageId ?? source.MessageId,
        AdmissionIdentityKey = admission ?? source.AdmissionIdentityKey,
        TemplateKey = templateKey ?? source.TemplateKey,
        TemplateVersion = templateVersion ?? source.TemplateVersion,
        ProtectedIntent = source.ProtectedIntent
    };
}
#endif
