using TrykatchApp.Application.Authorization;
using TrykatchApp.Domain.Common;
using TrykatchApp.Domain.Organizations;
using TrykatchApp.Domain.Projects;
using Shouldly;

namespace TrykatchApp.UnitTests;

[TestClass]
public sealed class DomainTests
{
    [TestMethod]
    public void OrganizationRejectsAnUnsafeSlug() =>
        Should.Throw<DomainException>(() => Organization.Create("Acme", "Acme Corp"));

    [TestMethod]
    public void OrganizationAccessCanBeSuspendedAndRestored()
    {
        Organization organization = Organization.Create("Acme", "acme");

        organization.Deactivate();
        organization.IsActive.ShouldBeFalse();

        organization.Reactivate();
        organization.IsActive.ShouldBeTrue();
    }

    [TestMethod]
    public void InvitationCanOnlyBeAcceptedOnce()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid roleId = Guid.CreateVersion7();
        Invitation invitation = Invitation.Create(Guid.CreateVersion7(), roleId, "USER@example.com", "hash", now.AddDays(1));

        invitation.Email.ShouldBe("user@example.com");
        invitation.RoleId.ShouldBe(roleId);
        invitation.Accept(now);
        Should.Throw<DomainException>(() => invitation.Accept(now));
        Should.Throw<DomainException>(() => invitation.Revoke(now));
    }

    [TestMethod]
    public void ProjectArchiveIsIdempotent()
    {
        Project project = Project.Create(Guid.CreateVersion7(), "Atlas", null, Guid.CreateVersion7());
        Guid actorId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        project.Archive(actorId, now);
        DateTimeOffset? firstArchive = project.ArchivedAt;
        project.Archive(actorId, now.AddMinutes(1));
        project.ArchivedAt.ShouldBe(firstArchive);
    }

    [TestMethod]
    public void DeletedProjectCanBeRestoredToActive()
    {
        Project project = Project.Create(Guid.CreateVersion7(), "Atlas", null, Guid.CreateVersion7());
        Guid actorId = Guid.CreateVersion7();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        project.Archive(actorId, now);
        project.Delete(actorId, "Created in the wrong organization", now.AddMinutes(1));

        project.LifecycleState.ShouldBe(RecordLifecycleState.Deleted);
        project.DeletionReason.ShouldBe("Created in the wrong organization");
        project.Restore().ShouldBeTrue();
        project.LifecycleState.ShouldBe(RecordLifecycleState.Active);
        project.DeletionReason.ShouldBeNull();
    }

    [TestMethod]
    public void DeleteRequiresAnAccountableReason()
    {
        Project project = Project.Create(Guid.CreateVersion7(), "Atlas", null, Guid.CreateVersion7());
        project.Archive(Guid.CreateVersion7(), DateTimeOffset.UtcNow);
        Should.Throw<DomainException>(() => project.Delete(Guid.CreateVersion7(), "mistake", DateTimeOffset.UtcNow));
        project.LifecycleState.ShouldBe(RecordLifecycleState.Archived);
    }

    [TestMethod]
    public void ActiveProjectCannotEnterPendingDeletion()
    {
        Project project = Project.Create(Guid.CreateVersion7(), "Atlas", null, Guid.CreateVersion7());

        DomainException exception = Should.Throw<DomainException>(() =>
            project.Delete(Guid.CreateVersion7(), "Created in the wrong organization", DateTimeOffset.UtcNow));

        exception.Message.ShouldContain("archived");
        project.LifecycleState.ShouldBe(RecordLifecycleState.Active);
    }

    [TestMethod]
    public void InvitationCanEnterPendingDeletionWithoutArchiving()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Invitation invitation = Invitation.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "user@example.com", "hash", now.AddDays(1));

        invitation.Delete(Guid.CreateVersion7(), "Invitation was sent to the wrong address", now).ShouldBeTrue();
        invitation.LifecycleState.ShouldBe(RecordLifecycleState.Deleted);
        invitation.Restore().ShouldBeTrue();
        invitation.LifecycleState.ShouldBe(RecordLifecycleState.Active);
    }

    [TestMethod]
    public void SystemRoleCannotEnterADeletedLifecycle()
    {
        Role role = Role.Create(Guid.CreateVersion7(), "Owner", isSystem: true);
        Should.Throw<DomainException>(() => role.Delete(Guid.CreateVersion7(), "No longer required by this organization", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void PermissionCatalogHasUniqueStableValues() =>
        Permissions.All.Distinct(StringComparer.Ordinal).Count().ShouldBe(Permissions.All.Count);

    [TestMethod]
    public void SystemRoleCannotBeRenamed()
    {
        Role role = Role.Create(Guid.CreateVersion7(), "Owner", isSystem: true);
        Should.Throw<DomainException>(() => role.Rename("Something else"));
    }

    [TestMethod]
    public void ReplacingMembershipRolesRemovesDuplicates()
    {
        Membership membership = Membership.Create(Guid.CreateVersion7(), Guid.CreateVersion7());
        Guid roleId = Guid.CreateVersion7();
        membership.SetRoles([roleId, roleId]);
        membership.Roles.Single().RoleId.ShouldBe(roleId);
    }
}
