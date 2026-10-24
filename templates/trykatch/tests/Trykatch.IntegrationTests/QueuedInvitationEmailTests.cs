#if TRYKATCH_EMAIL
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trykatch.Application.Auditing;
using Trykatch.Application.Mailing;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Infrastructure.Mailing;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Identity;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    [DataRow("invalid@")]
    [DataRow("Display name <person@example.test>")]
    public async Task InvitationRejectsInvalidMailboxBeforeAnyWrites(string email)
    {
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        using HttpResponseMessage response = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email, expiresInDays = 7 });
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await using PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options);
        string normalizedEmail = email.ToLowerInvariant();
        (await database.Invitations.CountAsync(candidate => candidate.OrganizationId == organizationId && candidate.Email == normalizedEmail)).ShouldBe(0);
        await using OrganizationEmailDeliveryDbContext queue = InvitationQueueDatabase(host);
        (await queue.EmailDeliveries.CountAsync(candidate => candidate.OrganizationId == organizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task InvitationAdmissionDeduplicatesAndRechecksManagementBeforeReturningReceipt()
    {
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid roleId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray().Single(role => role.GetProperty("name").GetString() == "Member").GetProperty("id").GetGuid();
        Guid actorId;
        using (IServiceScope scope = host.Services.CreateScope())
            actorId = (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync("owner@trykatch.test"))!.Id;
        string token = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Invitation invitation = Invitation.Create(organizationId, roleId, "dedup-invite@trykatch.test",
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow.AddDays(7));
        Guid foreignRoleId = await host.SeedForeignRoleAsync();
        string foreignToken = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        Guid foreignInvitationId;
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            Role foreignRole = await database.Roles.SingleAsync(role => role.Id == foreignRoleId);
            Invitation foreign = Invitation.Create(foreignRole.OrganizationId, foreignRoleId, "foreign-invite@trykatch.test",
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(foreignToken))), DateTimeOffset.UtcNow.AddDays(7));
            foreignInvitationId = foreign.Id;
            database.Invitations.AddRange(invitation, foreign);
            await database.SaveChangesAsync();
        }
        ModuleWorkReceipt first;
        OrganizationAccess access;
        await using (AsyncServiceScope scope = host.Services.CreateAsyncScope())
        {
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organizationId, actorId, CancellationToken.None);
            access = (await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>().ResolveAsync(actorId, organizationId))!;
            scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
            IOrganizationInvitationEmailSubmission queue = scope.ServiceProvider.GetRequiredService<IOrganizationInvitationEmailSubmission>();
            first = await queue.EnqueueAsync(new(invitation.Id, token), CancellationToken.None);
            first.State.ShouldBe(ModuleWorkState.Accepted);
            ModuleWorkReceipt pending = await queue.EnqueueAsync(new(invitation.Id, token), CancellationToken.None);
            pending.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
            pending.WorkId.ShouldBe(first.WorkId);
            ModuleWorkReceipt foreign = await queue.EnqueueAsync(new(foreignInvitationId, foreignToken), CancellationToken.None);
            foreign.State.ShouldBe(ModuleWorkState.Rejected);
            foreign.WorkId.ShouldBe(Guid.Empty);
            await transaction.CommitAsync(CancellationToken.None);
        }
        async Task<ModuleWorkReceipt> SubmitAsync()
        {
            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>().BeginAsync(organizationId, actorId, CancellationToken.None);
            scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
            ModuleWorkReceipt receipt = await scope.ServiceProvider.GetRequiredService<IOrganizationInvitationEmailSubmission>().EnqueueAsync(new(invitation.Id, token), CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        }
        foreach (ModuleWorkReceipt receipt in await Task.WhenAll(SubmitAsync(), SubmitAsync()))
        {
            receipt.State.ShouldBe(ModuleWorkState.AlreadyAccepted);
            receipt.WorkId.ShouldBe(first.WorkId);
        }
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            (await database.Memberships.SingleAsync(member => member.OrganizationId == organizationId && member.UserId == actorId)).Suspend();
            await database.SaveChangesAsync();
        }
        ModuleWorkReceipt denied = await SubmitAsync();
        denied.State.ShouldBe(ModuleWorkState.Rejected);
        denied.WorkId.ShouldBe(Guid.Empty);
        await using OrganizationEmailDeliveryDbContext retained = InvitationQueueDatabase(host);
        (await retained.EmailDeliveries.CountAsync(row => row.OrganizationId == organizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task InvitationEmailUsesCurrentNamesAndPendingGrantAfterInviterLeaves()
    {
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement role = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new { id = (Guid?)null, name = "Original analysts", description = "", permissions = Array.Empty<string>() });
        Guid roleId = role.GetProperty("id").GetGuid();
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email = "current-invite@trykatch.test", roleId, expiresInDays = 7 });
        using IServiceScope scope = host.Services.CreateScope();
        IEmailDeliverySource source = scope.ServiceProvider.GetServices<IEmailDeliverySource>().Single(candidate => candidate.Boundary == EmailDeliveryBoundary.Organization);
        EmailDeliveryLease lease = (await source.ClaimAsync(1, CancellationToken.None)).Single();
        await using OrganizationEmailDeliveryDbContext queue = InvitationQueueDatabase(host);
        Guid actorId = (await queue.EmailDeliveries.AsNoTracking().SingleAsync(row => row.Id == lease.Context.DeliveryId)).ActorId;
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            (await database.Organizations.SingleAsync(organization => organization.Id == lease.Context.BoundaryOwnerId)).Rename("Current workspace");
            (await database.Roles.SingleAsync(candidate => candidate.Id == roleId)).Rename("Current analysts");
            (await database.Memberships.SingleAsync(member => member.OrganizationId == lease.Context.BoundaryOwnerId && member.UserId == actorId)).Suspend();
            await database.SaveChangesAsync();
        }
        EmailDeliveryPreparation prepared = await source.PrepareAsync(lease, CancellationToken.None);
        prepared.Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        prepared.Message!.TextBody.ShouldContain("Current workspace");
        prepared.Message.TextBody.ShouldContain("Current analysts");
        prepared.Message.TextBody.ShouldNotContain("Original analysts");
        prepared.Message.TextBody.ShouldContain(created.GetProperty("invitationUrl").GetString()!);
        await source.CompleteAsync(lease, new(EmailAttemptDisposition.Suppressed, EmailFailureCode.SourceDenied), CancellationToken.None);
    }

    [TestMethod]
    public async Task InvitationQueuesProtectedIntentAndReturnsTruthfulDeliveryState()
    {
        RecordingInvitationNotifier legacy = new();
        await using AccessHost host = await AccessHost.StartAsync(legacy, queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement access = await owner.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid organizationId = access.GetProperty("organizationId").GetGuid();
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email = "queued-invite@trykatch.test", expiresInDays = 7 });

        created.GetProperty("emailQueued").GetBoolean().ShouldBeTrue();
        created.GetProperty("emailDelivered").GetBoolean().ShouldBeFalse();
        legacy.Recipient.ShouldBeNull();
        await using OrganizationEmailDeliveryDbContext database = InvitationQueueDatabase(host);
        OrganizationEmailDeliveryRecord row = await database.EmailDeliveries.AsNoTracking().SingleAsync(candidate => candidate.OrganizationId == organizationId);
        row.MessageId.ShouldBe(created.GetProperty("invitation").GetProperty("id").GetGuid());
        row.TemplateKey.ShouldBe("core.organization-invitation");
        row.State.ShouldBe("queued");
        row.Attempts.ShouldBe(0);
        row.ProtectedIntent.ShouldNotContain("queued-invite@trykatch.test");
        row.ProtectedIntent.ShouldNotContain(new Uri(created.GetProperty("invitationUrl").GetString()!).Segments.Last());

        using IServiceScope scope = host.Services.CreateScope();
        IEmailDeliverySource source = scope.ServiceProvider.GetServices<IEmailDeliverySource>().Single(candidate => candidate.Boundary == EmailDeliveryBoundary.Organization);
        EmailDeliveryLease lease = (await source.ClaimAsync(1, CancellationToken.None)).Single();
        EmailDeliveryPreparation prepared = await source.PrepareAsync(lease, CancellationToken.None);
        prepared.Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        prepared.Message!.Recipient.ShouldBe("queued-invite@trykatch.test");
        prepared.Message.TextBody.ShouldContain(created.GetProperty("invitationUrl").GetString()!);
        prepared.Message.TextBody.ShouldContain("Workspace role: Member");
        await source.CompleteAsync(lease, new(EmailAttemptDisposition.Acknowledged), CancellationToken.None);
    }

    [TestMethod]
    [DataRow("revoked")]
    [DataRow("accepted")]
    [DataRow("expired")]
    [DataRow("recipient")]
    [DataRow("token")]
    [DataRow("role")]
    [DataRow("organization")]
    public async Task InvitationEmailSuppressesChangedCurrentSource(string change)
    {
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email = "revocable-invite@trykatch.test", expiresInDays = 7 });
        Guid invitationId = created.GetProperty("invitation").GetProperty("id").GetGuid();
        using IServiceScope scope = host.Services.CreateScope();
        IEmailDeliverySource source = scope.ServiceProvider.GetServices<IEmailDeliverySource>().Single(candidate => candidate.Boundary == EmailDeliveryBoundary.Organization);
        EmailDeliveryLease lease = (await source.ClaimAsync(1, CancellationToken.None)).Single();
        (await source.PrepareAsync(lease, CancellationToken.None)).Disposition.ShouldBe(EmailPreparationDisposition.Ready);
        await using (PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options))
        {
            Invitation invitation = await database.Invitations.SingleAsync(candidate => candidate.Id == invitationId);
            switch (change)
            {
                case "revoked": invitation.Revoke(DateTimeOffset.UtcNow); break;
                case "accepted": invitation.Accept(DateTimeOffset.UtcNow); break;
                case "expired": database.Entry(invitation).Property(candidate => candidate.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "recipient": database.Entry(invitation).Property(candidate => candidate.Email).CurrentValue = "changed-recipient@trykatch.test"; break;
                case "token": database.Entry(invitation).Property(candidate => candidate.TokenHash).CurrentValue = new string('0', 64); break;
                case "role":
                    Role role = await database.Roles.SingleAsync(candidate => candidate.Id == invitation.RoleId);
                    database.Entry(role).Property(candidate => candidate.ArchivedAt).CurrentValue = DateTimeOffset.UtcNow;
                    database.Entry(role).Property(candidate => candidate.ArchivedBy).CurrentValue = lease.Context.BoundaryOwnerId;
                    break;
                case "organization": (await database.Organizations.SingleAsync(candidate => candidate.Id == invitation.OrganizationId)).Deactivate(); break;
                default: throw new InvalidOperationException("Unknown source fixture mutation.");
            }
            await database.SaveChangesAsync();
        }

        EmailDeliveryPreparation suppressed = await source.PrepareAsync(lease, CancellationToken.None);
        suppressed.Disposition.ShouldBe(EmailPreparationDisposition.Suppressed);
        suppressed.Message.ShouldBeNull();
        await source.CompleteAsync(lease, new(EmailAttemptDisposition.Suppressed, suppressed.FailureCode), CancellationToken.None);
    }

    [TestMethod]
    public async Task InvitationEmailInsertFailureRollsBackInvitationAndRequiredAudit()
    {
        await using AccessHost host = await AccessHost.StartAsync(queuedEmail: true);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        await using PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(host.OwnerConnection).Options);
        int before = await database.AuditIntents.CountAsync(candidate => candidate.OrganizationId == organizationId && candidate.Operation == AuditActions.InvitationCreated);
        await database.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION infrastructure.reject_invitation_email_fixture() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW."TemplateKey" = 'core.organization-invitation' THEN
                RAISE EXCEPTION 'Injected invitation queue persistence failure';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER reject_invitation_email_fixture BEFORE INSERT ON infrastructure.email_deliveries
              FOR EACH ROW EXECUTE FUNCTION infrastructure.reject_invitation_email_fixture();
            """);
        using HttpResponseMessage response = await AccessHost.SendAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email = "rollback-invite@trykatch.test", expiresInDays = 7 });
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await database.Invitations.CountAsync(candidate => candidate.OrganizationId == organizationId && candidate.Email == "rollback-invite@trykatch.test")).ShouldBe(0);
        (await database.AuditIntents.CountAsync(candidate => candidate.OrganizationId == organizationId && candidate.Operation == AuditActions.InvitationCreated)).ShouldBe(before);
        await using OrganizationEmailDeliveryDbContext queue = InvitationQueueDatabase(host);
        (await queue.EmailDeliveries.CountAsync(candidate => candidate.OrganizationId == organizationId)).ShouldBe(0);
    }

    [TestMethod]
    public async Task InvitationWithoutApprovedEmailOriginPreservesCopyLinkAndReportsNoDelivery()
    {
        RecordingInvitationNotifier legacy = new();
        await using AccessHost host = await AccessHost.StartAsync(legacy);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/invitations", new { email = "copy-invite@trykatch.test", expiresInDays = 7 });
        created.GetProperty("emailQueued").GetBoolean().ShouldBeFalse();
        created.GetProperty("emailDelivered").GetBoolean().ShouldBeFalse();
        created.GetProperty("invitationUrl").GetString().ShouldNotBeNullOrEmpty();
        legacy.Recipient.ShouldBeNull();
    }

    private static OrganizationEmailDeliveryDbContext InvitationQueueDatabase(AccessHost host) =>
        new(new DbContextOptionsBuilder<OrganizationEmailDeliveryDbContext>().UseNpgsql(host.OwnerConnection).Options);
}
#endif
