using System.Text.Json;
using Trykatch.Application.Auditing;
using Trykatch.Infrastructure.Organizations;
using Shouldly;

namespace Trykatch.UnitTests;

[TestClass]
public sealed class AuditIntentTests
{
    [TestMethod]
    public void ApprovedDetailsProduceAnImmutableStableIntent()
    {
        Guid eventId = Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58b4");
        Guid organizationId = Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58b5");
        Guid actorId = Guid.Parse("01992162-8d8d-7d8b-9efe-b50959dc58b6");
        DateTimeOffset occurredAt = new(2026, 9, 11, 10, 15, 0, TimeSpan.Zero);

        AuditIntent intent = AuditIntent.Create(
            eventId,
            organizationId,
            actorId,
            AuditActions.RoleUpdated,
            new AuditTarget("Role", "role-7", "Operations"),
            new Dictionary<string, string?> { ["permissionCount"] = "4" },
            occurredAt);

        intent.Id.ShouldBe(eventId);
        intent.OrganizationId.ShouldBe(organizationId);
        intent.ActorId.ShouldBe(actorId);
        intent.Operation.ShouldBe(AuditActions.RoleUpdated);
        intent.OccurredAt.ShouldBe(occurredAt);
        JsonDocument.Parse(intent.Details).RootElement.GetProperty("permissionCount").GetString().ShouldBe("4");
        typeof(AuditIntent).GetProperties().All(property => property.SetMethod is null || property.SetMethod.IsPrivate).ShouldBeTrue();
    }

    [TestMethod]
    public void UnapprovedOrSensitiveDetailsAreRejected()
    {
        foreach (string key in new[] { "password", "token", "authorization", "clientSecret", "arbitraryField" })
            Should.Throw<ArgumentException>(() => AuditIntent.Create(
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                Guid.CreateVersion7(),
                AuditActions.InvitationCreated,
                new AuditTarget("Invitation", "invite-7", "person@example.test"),
                new Dictionary<string, string?> { [key] = "sentinel-secret" },
                DateTimeOffset.UtcNow))
                .Message.ShouldContain("approved");
    }

    [TestMethod]
    public void UserSuppliedDeletionReasonIsNotCopiedIntoAuditDetails()
    {
        const string suppliedReason = "cleanup password=secret-sentinel token=secret-sentinel";

        AuditIntent intent = AuditIntent.Create(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            AuditActions.RoleDeleted,
            new AuditTarget("Role", "role-7", "Operations"),
            AuditDetails.ReasonProvided(suppliedReason),
            DateTimeOffset.UtcNow);

        intent.Details.ShouldContain("reasonProvided");
        intent.Details.ShouldNotContain("secret-sentinel");
        intent.Details.ShouldNotContain("password");
        intent.Details.ShouldNotContain("token");
    }

    [TestMethod]
    public void RuntimeProfilesKeepAuditIntentAndProjectionPrivilegesMinimal()
    {
        Dictionary<string, Trykatch.Modules.DataResourceDescriptor> resources = [];

        RuntimeDatabaseAccessProfiles.PermissionsFor(
            RuntimeDatabaseRoleKind.Organization, "platform.audit_intents", resources)
            .ShouldBe(new HashSet<string> { "INSERT" }, ignoreOrder: true);
        RuntimeDatabaseAccessProfiles.PermissionsFor(
            RuntimeDatabaseRoleKind.Outbox, "platform.audit_intents", resources)
            .ShouldBe(new HashSet<string> { "SELECT" }, ignoreOrder: true);
        RuntimeDatabaseAccessProfiles.PermissionsFor(
            RuntimeDatabaseRoleKind.Outbox, "platform.audit_entries", resources)
            .ShouldBe(new HashSet<string> { "SELECT", "INSERT" }, ignoreOrder: true);

        foreach (RuntimeDatabaseRoleKind role in Enum.GetValues<RuntimeDatabaseRoleKind>())
        {
            IReadOnlySet<string> intentPermissions = RuntimeDatabaseAccessProfiles.PermissionsFor(
                role, "platform.audit_intents", resources);
            intentPermissions.ShouldNotContain("UPDATE");
            intentPermissions.ShouldNotContain("DELETE");
        }
    }
}
