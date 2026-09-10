using System.Security.Cryptography;
using System.Text;
using Trykatch.Domain.Common;

namespace Trykatch.Domain.Organizations;

/// <summary>
/// Durable identity for one organization-creation workflow. A pre-existing
/// organization without this record is never eligible for provisioning recovery.
/// </summary>
public sealed class OrganizationCreationIntent : Entity
{
    private OrganizationCreationIntent() : base(Guid.Empty) { }

    private OrganizationCreationIntent(
        Guid id,
        Guid organizationId,
        string slug,
        Guid initiatingActorId,
        string administratorEmail,
        OrganizationDataPlacementKind placement,
        DateTimeOffset createdAt) : base(id)
    {
        OrganizationId = organizationId;
        Slug = slug;
        InitiatingActorId = initiatingActorId;
        AdministratorEmail = NormalizeEmail(administratorEmail);
        IdempotencyIdentityHash = CreateIdentityHash(slug, initiatingActorId, AdministratorEmail);
        Placement = placement;
        CreatedAt = createdAt;
    }

    public Guid OrganizationId { get; private init; }
    public string Slug { get; private init; } = string.Empty;
    public Guid InitiatingActorId { get; private init; }
    public string AdministratorEmail { get; private init; } = string.Empty;
    public string IdempotencyIdentityHash { get; private init; } = string.Empty;
    public OrganizationDataPlacementKind Placement { get; private init; }
    public Guid? InvitationId { get; private set; }
    public string? ProtectedInvitationToken { get; private set; }
    public DateTimeOffset CreatedAt { get; private init; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public bool IsCompleted => InvitationId is not null && ProtectedInvitationToken is not null;

    public static OrganizationCreationIntent Begin(
        Guid organizationId,
        string slug,
        Guid initiatingActorId,
        string administratorEmail,
        OrganizationDataPlacementKind placement,
        DateTimeOffset now)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization id is required.", nameof(organizationId));
        if (initiatingActorId == Guid.Empty) throw new ArgumentException("Initiating actor id is required.", nameof(initiatingActorId));
        if (string.IsNullOrWhiteSpace(slug)) throw new ArgumentException("Slug is required.", nameof(slug));
        if (string.IsNullOrWhiteSpace(administratorEmail)) throw new ArgumentException("Administrator email is required.", nameof(administratorEmail));
        return new(Guid.CreateVersion7(), organizationId, slug, initiatingActorId, administratorEmail, placement, now);
    }

    public bool Matches(
        string slug,
        Guid initiatingActorId,
        string administratorEmail,
        OrganizationDataPlacementKind placement) =>
        string.Equals(Slug, slug, StringComparison.Ordinal)
        && InitiatingActorId == initiatingActorId
        && string.Equals(AdministratorEmail, NormalizeEmail(administratorEmail), StringComparison.Ordinal)
        && Placement == placement
        && string.Equals(
            IdempotencyIdentityHash,
            CreateIdentityHash(slug, initiatingActorId, NormalizeEmail(administratorEmail)),
            StringComparison.Ordinal);

    public void MarkInvitationIssued(Guid invitationId, string protectedToken, DateTimeOffset now)
    {
        if (invitationId == Guid.Empty) throw new ArgumentException("Invitation id is required.", nameof(invitationId));
        if (string.IsNullOrWhiteSpace(protectedToken)) throw new ArgumentException("Protected invitation token is required.", nameof(protectedToken));
        if (IsCompleted)
        {
            if (InvitationId != invitationId || !string.Equals(ProtectedInvitationToken, protectedToken, StringComparison.Ordinal))
                throw new InvalidOperationException("Organization creation is already bound to another invitation.");
            return;
        }
        InvitationId = invitationId;
        ProtectedInvitationToken = protectedToken;
        CompletedAt = now;
    }

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    private static string CreateIdentityHash(string slug, Guid actorId, string normalizedEmail) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{slug}\n{actorId:D}\n{normalizedEmail}")));
}
