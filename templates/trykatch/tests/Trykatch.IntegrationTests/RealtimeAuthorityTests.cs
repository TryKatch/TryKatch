using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Api.Realtime;
using Trykatch.Application.Authorization;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using static Trykatch.IntegrationTests.AccountSecurityStepUpTests;
using static Trykatch.IntegrationTests.WorkspaceSessionLifecycleTests;

namespace Trykatch.IntegrationTests;

[TestClass, TestCategory("Integration")]
public sealed class RealtimeAuthorityTests
{
    [TestMethod]
    public async Task OpenEstablishesFreshMembershipAndSharedReadOnlyOrganizationScope()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(browser, "realtime-authority-valid");
        RealtimeConnectionBinding binding = await BindingAsync(browser, session, organizationId);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        (await authority.OpenAsync(binding, CancellationToken.None)).ShouldBeTrue();

        IOrganizationContext organization = scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
        organization.IsResolved.ShouldBeTrue();
        organization.OrganizationId.ShouldBe(organizationId);
        organization.ActorId.ShouldBe(binding.Proof.ActorId);
        organization.MembershipId.ShouldNotBe(Guid.Empty);
        organization.Permissions.ShouldContain(Permissions.OrganizationsRead);

        OrganizationControlPlaneDbContext control = scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>();
        ApplicationDbContext application = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IDbContextTransaction transaction = control.Database.CurrentTransaction!;
        transaction.ShouldNotBeNull();
        application.Database.CurrentTransaction.ShouldNotBeNull();
        transaction.GetDbTransaction().ShouldBeSameAs(application.Database.CurrentTransaction!.GetDbTransaction());
        control.Database.GetDbConnection().ShouldBeSameAs(application.Database.GetDbConnection());

        await using NpgsqlCommand command = new("""
            SELECT current_setting('transaction_read_only'), current_setting('transaction_isolation'), current_user,
                   current_setting('app.organization_id'), current_setting('app.actor_id')
            """, (NpgsqlConnection)control.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetString(0).ShouldBe("on");
        reader.GetString(1).ShouldBe("read committed");
        reader.GetString(2).ShouldBe(PostgresRuntimeRoleFixture.OrganizationRole);
        reader.GetString(3).ShouldBe(organizationId.ToString());
        reader.GetString(4).ShouldBe(binding.Proof.ActorId.ToString());
    }

    [TestMethod]
    [DataRow("missing-session")]
    [DataRow("revoked-session")]
    [DataRow("expired-session")]
    [DataRow("expired-ticket")]
    [DataRow("changed-security-stamp")]
    [DataRow("suspended-membership")]
    [DataRow("deleted-membership")]
    [DataRow("inactive-organization")]
    public async Task OpenRejectsStaleSessionOrOrganizationAuthority(string invalidation)
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(browser, "realtime-authority-stale");
        RealtimeConnectionBinding binding = await BindingAsync(browser, session, organizationId);
        await InvalidateAsync(host, binding, invalidation);

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        (await authority.OpenAsync(binding, CancellationToken.None)).ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<IOrganizationContext>().IsResolved.ShouldBeFalse();
        scope.ServiceProvider.GetRequiredService<OrganizationControlPlaneDbContext>().Database.CurrentTransaction.ShouldBeNull();
    }

    [TestMethod]
    public async Task CurrentCheckAndFreshOpenRejectAnAbaWorkspaceGeneration()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationA = await CreateOrganizationForCurrentUserAsync(browser, "realtime-authority-aba-a");
        RealtimeConnectionBinding original = await BindingAsync(browser, session, organizationA);

        await using AsyncServiceScope liveScope = host.Services.CreateAsyncScope();
        RealtimeAuthority liveAuthority = liveScope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        (await liveAuthority.OpenAsync(original, CancellationToken.None)).ShouldBeTrue();

        Guid organizationB = await CreateOrganizationForCurrentUserAsync(browser, "realtime-authority-aba-b");
        using (HttpResponseMessage select = await PostAsync(browser, "/api/v1/workspace/select", new { organizationId = organizationA }))
            select.StatusCode.ShouldBe(HttpStatusCode.NoContent, await select.Content.ReadAsStringAsync());
        JsonElement returnedToA = await ReadRealtimeStateAsync(browser);
        returnedToA.GetProperty("generation").GetGuid().ShouldNotBe(original.Generation);
        returnedToA.GetProperty("generation").GetGuid().ShouldNotBe(Guid.Empty);
        organizationB.ShouldNotBe(organizationA);

        (await liveAuthority.IsSessionCurrentAsync(original, CancellationToken.None)).ShouldBeFalse();

        await using AsyncServiceScope freshScope = host.Services.CreateAsyncScope();
        RealtimeAuthority freshAuthority = freshScope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        (await freshAuthority.OpenAsync(original, CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task FreshOpenReadsCurrentRolePermissionsForTheActiveMembership()
    {
        await using SecurityHost host = await SecurityHost.StartAsync();
        using HttpClient browser = host.CreateClient();
        var session = await AccountSessionLifecycleTests.SignInAsync(host, browser);
        Guid organizationId = await CreateOrganizationForCurrentUserAsync(browser, "realtime-authority-permissions");
        RealtimeConnectionBinding binding = await BindingAsync(browser, session, organizationId);
        await using (NpgsqlConnection connection = new(host.OwnerConnection))
        {
            await connection.OpenAsync();
            await using NpgsqlCommand removeGrant = new("""
                DELETE FROM platform.role_permissions AS permission
                USING platform.membership_roles AS assignment
                WHERE permission."RoleId" = assignment."RoleId"
                  AND assignment."MembershipId" = (
                      SELECT membership."Id" FROM platform.memberships AS membership
                      WHERE membership."OrganizationId" = @organization AND membership."UserId" = @actor)
                  AND permission."Permission" = @permission
                """, connection);
            removeGrant.Parameters.AddWithValue("organization", organizationId);
            removeGrant.Parameters.AddWithValue("actor", binding.Proof.ActorId);
            removeGrant.Parameters.AddWithValue("permission", Permissions.OrganizationsRead);
            (await removeGrant.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        RealtimeAuthority authority = scope.ServiceProvider.GetRequiredService<RealtimeAuthority>();
        (await authority.OpenAsync(binding, CancellationToken.None)).ShouldBeTrue();
        IOrganizationContext organization = scope.ServiceProvider.GetRequiredService<IOrganizationContext>();
        organization.IsResolved.ShouldBeTrue();
        organization.OrganizationId.ShouldBe(organizationId);
        organization.ActorId.ShouldBe(binding.Proof.ActorId);
        organization.MembershipId.ShouldNotBe(Guid.Empty);
        organization.Permissions.ShouldNotContain(Permissions.OrganizationsRead);
    }

    private static async Task<RealtimeConnectionBinding> BindingAsync(
        HttpClient browser,
        (string Cookie, string Hash, Microsoft.AspNetCore.Authentication.AuthenticationTicket Ticket) session,
        Guid organizationId)
    {
        JsonElement state = await ReadRealtimeStateAsync(browser);
        Guid currentOrganization = (await browser.GetFromJsonAsync<JsonElement>("/api/v1/workspace/current"))
            .GetProperty("organizationId").GetGuid();
        currentOrganization.ShouldBe(organizationId);
        ClaimsPrincipal principal = session.Ticket.Principal;
        Guid actorId = Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
        string stamp = principal.FindFirstValue("AspNet.Identity.SecurityStamp")!;
        DateTimeOffset expiresAt = session.Ticket.Properties.ExpiresUtc!.Value;
        return new(new AccountSessionProof(actorId, session.Hash, stamp, expiresAt), organizationId,
            state.GetProperty("generation").GetGuid());
    }

    private static async Task<JsonElement> ReadRealtimeStateAsync(HttpClient browser)
    {
        using HttpResponseMessage response = await browser.GetAsync("/api/v1/realtime/state");
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task InvalidateAsync(SecurityHost host, RealtimeConnectionBinding binding, string invalidation)
    {
        string sql = invalidation switch
        {
            "missing-session" => "DELETE FROM identity.account_sessions WHERE \"SessionHash\" = @hash",
            "revoked-session" => "UPDATE identity.account_sessions SET \"RevokedAt\" = CURRENT_TIMESTAMP WHERE \"SessionHash\" = @hash",
            "expired-session" => "UPDATE identity.account_sessions SET \"ExpiresAt\" = @expired WHERE \"SessionHash\" = @hash",
            "expired-ticket" => "UPDATE identity.account_sessions SET \"ExpiresAt\" = @future WHERE \"SessionHash\" = @hash",
            "changed-security-stamp" => "UPDATE identity.\"AspNetUsers\" SET \"SecurityStamp\" = @stamp WHERE \"Id\" = @actor",
            "suspended-membership" => "UPDATE platform.memberships SET \"Status\" = 2 WHERE \"OrganizationId\" = @organization AND \"UserId\" = @actor",
            "deleted-membership" => "DELETE FROM platform.memberships WHERE \"OrganizationId\" = @organization AND \"UserId\" = @actor",
            "inactive-organization" => "UPDATE platform.organizations SET \"IsActive\" = false WHERE \"Id\" = @organization",
            _ => throw new ArgumentOutOfRangeException(nameof(invalidation))
        };

        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        if (invalidation is "missing-session" or "revoked-session" or "expired-session" or "expired-ticket")
            command.Parameters.AddWithValue("hash", binding.Proof.SessionHash);
        if (invalidation == "changed-security-stamp")
            command.Parameters.AddWithValue("actor", binding.Proof.ActorId);
        if (invalidation is "suspended-membership" or "deleted-membership" or "inactive-organization")
        {
            command.Parameters.AddWithValue("actor", binding.Proof.ActorId);
            command.Parameters.AddWithValue("organization", binding.OrganizationId);
        }
        if (invalidation == "expired-session")
            command.Parameters.AddWithValue("expired", binding.Proof.TicketExpiresAt.AddMinutes(-1));
        if (invalidation == "expired-ticket")
            command.Parameters.AddWithValue("future", binding.Proof.TicketExpiresAt.AddHours(1));
        if (invalidation == "changed-security-stamp")
            command.Parameters.AddWithValue("stamp", Guid.NewGuid().ToString("N"));
        (await command.ExecuteNonQueryAsync()).ShouldBe(1, $"Expected one row to be changed for {invalidation}.");
        if (invalidation == "expired-ticket")
            host.Clock.Advance(binding.Proof.TicketExpiresAt - host.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        if (invalidation == "expired-session")
            host.Clock.Advance(binding.Proof.TicketExpiresAt.AddMinutes(-1) - host.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
    }
}
