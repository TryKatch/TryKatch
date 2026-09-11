using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Identity;
using Trykatch.Application.Organizations;
using Trykatch.Domain.Organizations;
using Trykatch.Identity;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;
using Trykatch.Modules.Documents.Infrastructure;
using Trykatch.Modules.Projects.Infrastructure;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AccessManagementBoundaryTests
{
    private static readonly string[] OrdinaryOrganizationPermissions = ["projects.read"];
    private static readonly string[] LimitedOrganizationManagementPermissions = ["members.manage", "members.read", "roles.read", "roles.manage"];
    private static readonly string[] ElevatedOrganizationPermissions = ["organizations.manage"];
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DelegatedPlatformManagerCannotIssuePendingAdministratorCredentials(bool equivalentPermissions)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement role = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Delegated access manager",
            description = "Manage ordinary platform access",
            permissions = equivalentPermissions ? PlatformPermissions.All.ToArray() : new[] { PlatformPermissions.UsersRead, PlatformPermissions.UsersManage, PlatformPermissions.DashboardRead }
        });
        using HttpClient manager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", role.GetProperty("key").GetString()!);
        JsonElement pending = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email = "pending-administrator@trykatch.test", displayName = "Pending administrator", roleKey = PlatformRoles.Administrator
        });
        Guid target = pending.GetProperty("user").GetProperty("id").GetGuid();

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager, HttpMethod.Post, $"/api/v1/platform-users/{target}/activation-token", new { });

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await denied.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("title").GetString().ShouldBe("grant_boundary");
        problem.TryGetProperty("token", out _).ShouldBeFalse();
        JsonElement unchanged = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform-users/{target}");
        unchanged.GetProperty("roleKey").GetString().ShouldBe(PlatformRoles.Administrator);
        unchanged.GetProperty("isPendingActivation").GetBoolean().ShouldBeTrue();
    }

    [TestMethod]
    [DataRow("role", false)]
    [DataRow("suspend", false)]
    [DataRow("reactivate", false)]
    [DataRow("revoke", false)]
    [DataRow("role", true)]
    [DataRow("suspend", true)]
    [DataRow("reactivate", true)]
    [DataRow("revoke", true)]
    public async Task EquivalentPermissionManagerCannotManageAdministrator(string operation, bool pending)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement role = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "All permissions without Administrator", description = "Delegated manager", permissions = PlatformPermissions.All
        });
        string managerRole = role.GetProperty("key").GetString()!;
        using HttpClient manager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", managerRole);
        if (pending)
            await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
            {
                email = "target@trykatch.test", displayName = "Pending target", roleKey = PlatformRoles.Administrator
            });
        else
        {
            using HttpClient activated = await host.GrantAndSignInAsync(administrator, "target@trykatch.test", PlatformRoles.Administrator);
        }
        Guid targetId = (await AccessHost.PlatformUserAsync(administrator, "target@trykatch.test")).GetProperty("id").GetGuid();
        if (operation == "reactivate")
            await AccessHost.SuccessAsync(administrator, HttpMethod.Post, $"/api/v1/platform-users/{targetId}/suspend", new { });

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager,
            operation == "role" ? HttpMethod.Put : operation == "revoke" ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/platform-users/{targetId}" + (operation == "revoke" ? "" : $"/{operation}"), new { roleKey = managerRole });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "grant_boundary");
        JsonElement unchanged = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform-users/{targetId}");
        unchanged.GetProperty("roleKey").GetString().ShouldBe(PlatformRoles.Administrator);
        unchanged.GetProperty("isActive").GetBoolean().ShouldBe(operation != "reactivate");
        unchanged.GetProperty("isPendingActivation").GetBoolean().ShouldBe(pending);
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("revoke")]
    public async Task PendingAdministratorDoesNotAllowRemovingFinalActiveAdministrator(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email = "pending@trykatch.test", displayName = "Pending administrator", roleKey = PlatformRoles.Administrator
        });
        Guid actorId = (await AccessHost.PlatformUserAsync(administrator, AccessHost.AdministratorEmail)).GetProperty("id").GetGuid();

        using HttpResponseMessage denied = await AccessHost.SendAsync(administrator,
            operation == "role" ? HttpMethod.Put : operation == "revoke" ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/platform-users/{actorId}" + (operation == "revoke" ? "" : $"/{operation}"), new { roleKey = PlatformRoles.Auditor });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Conflict, "last_administrator");
        JsonElement unchanged = await administrator.GetFromJsonAsync<JsonElement>($"/api/v1/platform-users/{actorId}");
        unchanged.GetProperty("roleKey").GetString().ShouldBe(PlatformRoles.Administrator);
        unchanged.GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("reactivate")]
    [DataRow("archive")]
    [DataRow("restore")]
    [DataRow("delete")]
    public async Task EquivalentPermissionOrganizationManagerCannotManageOwner(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement ownerRole = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Owner");
        Guid ownerRoleId = ownerRole.GetProperty("id").GetGuid();
        Guid targetId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("membershipId").GetGuid();
        using HttpClient safetyOwner = await host.InviteAndActivateAsync(owner, "safety-owner@trykatch.test");
        Guid safetyOwnerId = (await AccessHost.OrganizationMemberAsync(owner, "safety-owner@trykatch.test")).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{safetyOwnerId}", new { membershipId = safetyOwnerId, roleIds = new[] { ownerRoleId }, isActive = true });
        JsonElement customRole = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Every permission without ownership", description = "Delegated manager", permissions = ownerRole.GetProperty("permissions")
        });
        Guid managerRoleId = customRole.GetProperty("id").GetGuid();
        using HttpClient manager = await host.InviteAndActivateAsync(owner, "organization-manager@trykatch.test");
        Guid managerId = (await AccessHost.OrganizationMemberAsync(owner, "organization-manager@trykatch.test")).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{managerId}", new { membershipId = managerId, roleIds = new[] { managerRoleId }, isActive = true });
        if (operation is "restore" or "delete")
            await AccessHost.SuccessAsync(safetyOwner, HttpMethod.Post, $"/api/v1/members/{targetId}/archive", new { });
        if (operation == "reactivate")
            await AccessHost.SuccessAsync(safetyOwner, HttpMethod.Put, $"/api/v1/members/{targetId}", new { membershipId = targetId, roleIds = new[] { ownerRoleId }, isActive = false });
        bool update = operation is "role" or "suspend" or "reactivate";

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager,
            update ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/members/{targetId}" + (update || operation == "delete" ? "" : $"/{operation}"),
            new { membershipId = targetId, roleIds = new[] { operation == "role" ? managerRoleId : ownerRoleId }, isActive = operation != "suspend", reason = "Requested lifecycle cleanup" });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        JsonElement unchanged = await safetyOwner.GetFromJsonAsync<JsonElement>($"/api/v1/members/{targetId}");
        unchanged.GetProperty("roles").EnumerateArray().ShouldContain(role => role.GetProperty("id").GetGuid() == ownerRoleId);
        unchanged.GetProperty("status").GetString().ShouldBe(operation == "reactivate" ? "Suspended" : "Active");
        unchanged.GetProperty("lifecycle").GetProperty("status").GetString().ShouldBe(operation is "restore" or "delete" ? "Archived" : "Active");
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("archive")]
    [DataRow("delete")]
    public async Task FinalOwnerCannotBeRemoved(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        Guid targetId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("membershipId").GetGuid();
        JsonElement[] roles = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray().ToArray();
        Guid nextRole = roles.Single(role => role.GetProperty("name").GetString() == (operation == "role" ? "Member" : "Owner")).GetProperty("id").GetGuid();

        using HttpResponseMessage denied = await AccessHost.SendAsync(owner,
            operation is "role" or "suspend" ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post,
            $"/api/v1/members/{targetId}" + (operation == "archive" ? "/archive" : ""),
            new { membershipId = targetId, roleIds = new[] { nextRole }, isActive = operation != "suspend", reason = "Requested lifecycle cleanup" });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Conflict, "last_owner");
        JsonElement unchanged = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/members/{targetId}");
        unchanged.GetProperty("roles").EnumerateArray().ShouldContain(role => role.GetProperty("name").GetString() == "Owner");
        unchanged.GetProperty("status").GetString().ShouldBe("Active");
    }

    [TestMethod]
    public async Task RevokedPlatformManagerCannotUseStaleApplicationAuthority()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement role = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Delegated manager", description = "All ordinary management", permissions = PlatformPermissions.All
        });
        string roleKey = role.GetProperty("key").GetString()!;
        using HttpClient manager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", roleKey);
        Guid managerId = (await AccessHost.PlatformUserAsync(administrator, "manager@trykatch.test")).GetProperty("id").GetGuid();
        JsonElement pending = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email = "ordinary-target@trykatch.test", displayName = "Ordinary target", roleKey = PlatformRoles.Auditor
        });
        Guid targetId = pending.GetProperty("user").GetProperty("id").GetGuid();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        IPlatformAccessDirectory directory = scope.ServiceProvider.GetRequiredService<IPlatformAccessDirectory>();
        (await directory.ResolveEffectiveAccessAsync(managerId)).HasPermission(PlatformPermissions.UsersManage).ShouldBeTrue();
        await AccessHost.SuccessAsync(administrator, HttpMethod.Delete, $"/api/v1/platform-users/{managerId}", new { });

        (await directory.CreateRoleAsync(managerId, new("Untrusted role", "Denied", []))).ErrorCode.ShouldBe("grant_boundary");
        (await directory.UpdateRoleAsync(managerId, roleKey, new("Changed role", "Denied", []))).ErrorCode.ShouldBe("grant_boundary");
        (await directory.DeleteRoleAsync(managerId, roleKey)).ErrorCode.ShouldBe("grant_boundary");
        (await directory.GrantAsync(managerId, new("denied@trykatch.test", "Denied user", PlatformRoles.Auditor))).ErrorCode.ShouldBe("grant_boundary");
        (await directory.ChangeRoleAsync(managerId, targetId, roleKey)).ErrorCode.ShouldBe("grant_boundary");
        (await directory.SetStatusAsync(managerId, targetId, false)).ErrorCode.ShouldBe("grant_boundary");
        (await directory.CreateActivationTokenAsync(managerId, targetId)).ErrorCode.ShouldBe("grant_boundary");
        (await directory.RevokeAsync(managerId, targetId)).ErrorCode.ShouldBe("grant_boundary");
        (await AccessHost.PlatformUserAsync(administrator, "ordinary-target@trykatch.test")).GetProperty("roleKey").GetString().ShouldBe(PlatformRoles.Auditor);
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("revoke")]
    public async Task ConcurrentAdministratorRemovalRereadsAuthorityAfterLock(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient first = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient second = await host.GrantAndSignInAsync(first, "second-administrator@trykatch.test", PlatformRoles.Administrator);
        Guid firstId = (await AccessHost.PlatformUserAsync(first, AccessHost.AdministratorEmail)).GetProperty("id").GetGuid();
        Guid secondId = (await AccessHost.PlatformUserAsync(first, "second-administrator@trykatch.test")).GetProperty("id").GetGuid();
        await using ManagementBarrier barrier = await host.BlockManagementAsync();
        HttpMethod method = operation == "role" ? HttpMethod.Put : operation == "revoke" ? HttpMethod.Delete : HttpMethod.Post;
        string suffix = operation == "revoke" ? "" : $"/{operation}";
        Task<HttpResponseMessage> firstChange = AccessHost.SendAsync(first, method, $"/api/v1/platform-users/{secondId}{suffix}", new { roleKey = PlatformRoles.Auditor });
        Task<HttpResponseMessage> secondChange = AccessHost.SendAsync(second, method, $"/api/v1/platform-users/{firstId}{suffix}", new { roleKey = PlatformRoles.Auditor });
        await barrier.WaitForBlockedRequestsAsync(2);
        await barrier.ReleaseAsync();
        using HttpResponseMessage firstResult = await firstChange;
        using HttpResponseMessage secondResult = await secondChange;

        new[] { firstResult.IsSuccessStatusCode, secondResult.IsSuccessStatusCode }.Count(success => success).ShouldBe(1);
        await AccessHost.AssertProblemAsync(firstResult.IsSuccessStatusCode ? secondResult : firstResult, HttpStatusCode.Forbidden, "grant_boundary");
        HttpClient survivor = firstResult.IsSuccessStatusCode ? first : second;
        JsonElement page = await survivor.GetFromJsonAsync<JsonElement>("/api/v1/platform-users");
        page.GetProperty("items").EnumerateArray().Count(user => user.GetProperty("roleKey").GetString() == PlatformRoles.Administrator
            && user.GetProperty("isActive").GetBoolean() && !user.GetProperty("isPendingActivation").GetBoolean()).ShouldBe(1);
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("archive")]
    public async Task ConcurrentOwnerRemovalRereadsAuthorityAfterLock(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient first = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement access = await first.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid organizationId = access.GetProperty("organizationId").GetGuid();
        Guid firstId = access.GetProperty("membershipId").GetGuid();
        JsonElement[] roles = (await first.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray().ToArray();
        Guid ownerRoleId = roles.Single(role => role.GetProperty("name").GetString() == "Owner").GetProperty("id").GetGuid();
        Guid memberRoleId = roles.Single(role => role.GetProperty("name").GetString() == "Member").GetProperty("id").GetGuid();
        using HttpClient second = await host.InviteAndActivateAsync(first, "second-owner@trykatch.test");
        Guid secondId = (await AccessHost.OrganizationMemberAsync(first, "second-owner@trykatch.test")).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(first, HttpMethod.Put, $"/api/v1/members/{secondId}", new { membershipId = secondId, roleIds = new[] { ownerRoleId }, isActive = true });
        await using ManagementBarrier barrier = await host.BlockManagementAsync(organizationId);
        string suffix = operation == "archive" ? "/archive" : "";
        HttpMethod method = operation == "archive" ? HttpMethod.Post : HttpMethod.Put;
        Guid nextRole = operation == "role" ? memberRoleId : ownerRoleId;
        Task<HttpResponseMessage> firstChange = AccessHost.SendAsync(first, method, $"/api/v1/members/{secondId}{suffix}", new { membershipId = secondId, roleIds = new[] { nextRole }, isActive = operation != "suspend" });
        Task<HttpResponseMessage> secondChange = AccessHost.SendAsync(second, method, $"/api/v1/members/{firstId}{suffix}", new { membershipId = firstId, roleIds = new[] { nextRole }, isActive = operation != "suspend" });
        await barrier.WaitForBlockedRequestsAsync(2);
        await barrier.ReleaseAsync();
        using HttpResponseMessage firstResult = await firstChange;
        using HttpResponseMessage secondResult = await secondChange;

        new[] { firstResult.IsSuccessStatusCode, secondResult.IsSuccessStatusCode }.Count(success => success).ShouldBe(1);
        await AccessHost.AssertProblemAsync(firstResult.IsSuccessStatusCode ? secondResult : firstResult, HttpStatusCode.Forbidden, "forbidden");
        JsonElement members = await (firstResult.IsSuccessStatusCode ? first : second).GetFromJsonAsync<JsonElement>("/api/v1/members");
        members.EnumerateArray().Count(member => member.GetProperty("status").GetString() == "Active"
            && member.GetProperty("roles").EnumerateArray().Any(role => role.GetProperty("id").GetGuid() == ownerRoleId)).ShouldBe(1);
    }

    [TestMethod]
    [DataRow("update")]
    [DataRow("revoke")]
    [DataRow("restore")]
    [DataRow("delete")]
    public async Task OrganizationManagerCannotManagePendingOwnerInvitations(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient manager = await host.DelegateOrganizationManagementAsync(owner);
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid ownerRoleId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Owner").GetProperty("id").GetGuid();
        Guid invitationId = await host.SeedOwnerInvitationAsync(organizationId, ownerRoleId);
        if (operation == "restore")
            await AccessHost.SuccessAsync(owner, HttpMethod.Delete, $"/api/v1/invitations/{invitationId}", new { reason = "Owner authorized cleanup" });
        HttpMethod method = operation == "update" ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post;
        string path = $"/api/v1/invitations/{invitationId}" + (operation is "revoke" or "restore" ? $"/{operation}" : "");

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager, method, path, new { expiresInDays = 2, reason = "Requested lifecycle cleanup" });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        JsonElement unchanged = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/invitations/{invitationId}");
        unchanged.GetProperty("status").GetString().ShouldBe("Pending");
        unchanged.GetProperty("lifecycle").GetProperty("status").GetString().ShouldBe(operation == "restore" ? "Deleted" : "Active");
        await AccessHost.SuccessAsync(owner, method, path, new { expiresInDays = 2, reason = "Owner authorized cleanup" });
    }

    [TestMethod]
    public async Task DelegatedManagersRetainOrdinaryPlatformWorkflows()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement managerRole = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Delegated manager", description = "All ordinary management", permissions = PlatformPermissions.All
        });
        using HttpClient manager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", managerRole.GetProperty("key").GetString()!);
        using HttpResponseMessage forbiddenGrant = await AccessHost.SendAsync(manager, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email = "forbidden-admin@trykatch.test", displayName = "Forbidden administrator", roleKey = PlatformRoles.Administrator
        });
        await AccessHost.AssertProblemAsync(forbiddenGrant, HttpStatusCode.Forbidden, "grant_boundary");
        JsonElement role = await AccessHost.SuccessAsync(manager, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Ordinary role", description = "A delegated role", permissions = new[] { PlatformPermissions.DashboardRead }
        });
        string roleKey = role.GetProperty("key").GetString()!;
        await AccessHost.SuccessAsync(manager, HttpMethod.Put, $"/api/v1/platform-users/roles/{roleKey}", new
        {
            name = "Updated ordinary role", description = "A delegated role", permissions = new[] { PlatformPermissions.DashboardRead, PlatformPermissions.UsersRead }
        });
        JsonElement granted = await AccessHost.SuccessAsync(manager, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email = "ordinary@trykatch.test", displayName = "Ordinary user", roleKey
        });
        Guid userId = granted.GetProperty("user").GetProperty("id").GetGuid();
        (await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/platform-users/{userId}/activation-token", new { }))
            .GetProperty("token").GetString().ShouldNotBeNullOrWhiteSpace();
        await AccessHost.SuccessAsync(manager, HttpMethod.Put, $"/api/v1/platform-users/{userId}/role", new { roleKey = PlatformRoles.Auditor });
        await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/platform-users/{userId}/suspend", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/platform-users/{userId}/reactivate", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Delete, $"/api/v1/platform-users/{userId}", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Delete, $"/api/v1/platform-users/roles/{roleKey}", new { });
    }

    [TestMethod]
    public async Task DelegatedManagersRetainOrdinaryOrganizationWorkflows()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient manager = await host.DelegateOrganizationManagementAsync(owner);
        using HttpClient member = await host.InviteAndActivateAsync(manager, "ordinary@trykatch.test");
        Guid memberId = (await AccessHost.OrganizationMemberAsync(manager, "ordinary@trykatch.test")).GetProperty("id").GetGuid();
        Guid ownerRoleId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Owner").GetProperty("id").GetGuid();
        using HttpResponseMessage deniedOwnership = await AccessHost.SendAsync(manager, HttpMethod.Put, $"/api/v1/members/{memberId}",
            new { membershipId = memberId, roleIds = new[] { ownerRoleId }, isActive = true });
        await AccessHost.AssertProblemAsync(deniedOwnership, HttpStatusCode.Forbidden, "forbidden");
        JsonElement role = await AccessHost.SuccessAsync(manager, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Ordinary role", description = "Delegated role", permissions = Array.Empty<string>()
        });
        Guid roleId = role.GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(manager, HttpMethod.Put, $"/api/v1/roles/{roleId}", new
        {
            id = roleId, name = "Updated ordinary role", description = "Delegated role", permissions = OrdinaryOrganizationPermissions
        });
        foreach (bool active in new[] { false, true })
            await AccessHost.SuccessAsync(manager, HttpMethod.Put, $"/api/v1/members/{memberId}", new { membershipId = memberId, roleIds = new[] { roleId }, isActive = active });
        foreach (string action in new[] { "archive", "restore", "archive" })
            await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/members/{memberId}/{action}", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Delete, $"/api/v1/members/{memberId}", new { reason = "Delegated member cleanup" });
        foreach (string action in new[] { "archive", "restore", "archive" })
            await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/roles/{roleId}/{action}", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Delete, $"/api/v1/roles/{roleId}", new { reason = "Delegated role cleanup" });
        JsonElement invitation = await AccessHost.SuccessAsync(manager, HttpMethod.Post, "/api/v1/invitations", new { email = "pending@trykatch.test", expiresInDays = 7 });
        Guid invitationId = invitation.GetProperty("invitation").GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(manager, HttpMethod.Put, $"/api/v1/invitations/{invitationId}", new { expiresInDays = 2 });
        await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/invitations/{invitationId}/revoke", new { });
        await AccessHost.SuccessAsync(manager, HttpMethod.Delete, $"/api/v1/invitations/{invitationId}", new { reason = "Delegated invitation cleanup" });
        await AccessHost.SuccessAsync(manager, HttpMethod.Post, $"/api/v1/invitations/{invitationId}/restore", new { });
    }

    [TestMethod]
    public async Task OrganizationManagementDoesNotTrustEarlierResolvedPermissions()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient manager = await host.DelegateOrganizationManagementAsync(owner);
        JsonElement managerRecord = await AccessHost.OrganizationMemberAsync(owner, "organization-manager@trykatch.test");
        Guid managerId = managerRecord.GetProperty("id").GetGuid();
        Guid actorId = managerRecord.GetProperty("userId").GetGuid();
        Guid organizationId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/access")).GetProperty("organizationId").GetGuid();
        Guid memberRoleId = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Member").GetProperty("id").GetGuid();
        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        await using IOrganizationDataScope transaction = await scope.ServiceProvider.GetRequiredService<IOrganizationDataScopeFactory>()
            .BeginAsync(organizationId, actorId, CancellationToken.None);
        OrganizationAccess? access = await scope.ServiceProvider.GetRequiredService<IOrganizationAccessResolver>().ResolveAsync(actorId, organizationId);
        access.ShouldNotBeNull();
        access.Permissions.ShouldContain("members.manage");
        scope.ServiceProvider.GetRequiredService<IOrganizationContextInitializer>().Initialize(access);
        await scope.ServiceProvider.GetRequiredService<IOrganizationAdministrationStore>().FindMembershipAsync(organizationId, managerId, CancellationToken.None);
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{managerId}", new { membershipId = managerId, roleIds = new[] { memberRoleId }, isActive = true });

        var result = await scope.ServiceProvider.GetRequiredService<OrganizationAdministration>()
            .CreateInvitationAsync(new("denied@trykatch.test"), CancellationToken.None);

        result.ErrorCode.ShouldBe("forbidden");
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/invitations")).EnumerateArray()
            .ShouldNotContain(invitation => invitation.GetProperty("email").GetString() == "denied@trykatch.test");
    }

    [TestMethod]
    public async Task DelegatedPlatformManagerCannotDowngradeMorePrivilegedCustomRole()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement limited = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Limited manager", description = "Delegated management", permissions = new[] { PlatformPermissions.UsersManage, PlatformPermissions.UsersRead, PlatformPermissions.DashboardRead }
        });
        JsonElement elevated = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Higher custom authority", description = "Not the Administrator role", permissions = PlatformPermissions.All
        });
        string limitedRole = limited.GetProperty("key").GetString()!;
        string elevatedRole = elevated.GetProperty("key").GetString()!;
        using HttpClient manager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", limitedRole);
        using HttpClient target = await host.GrantAndSignInAsync(administrator, "target@trykatch.test", elevatedRole);
        Guid targetId = (await AccessHost.PlatformUserAsync(administrator, "target@trykatch.test")).GetProperty("id").GetGuid();

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager, HttpMethod.Put, $"/api/v1/platform-users/{targetId}/role", new { roleKey = limitedRole });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "grant_boundary");
        (await AccessHost.PlatformUserAsync(administrator, "target@trykatch.test")).GetProperty("roleKey").GetString().ShouldBe(elevatedRole);
    }

    [TestMethod]
    public async Task DelegatedOrganizationManagerCannotDowngradeMorePrivilegedCustomRole()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient target = await host.DelegateOrganizationManagementAsync(owner);
        JsonElement targetRecord = await AccessHost.OrganizationMemberAsync(owner, "organization-manager@trykatch.test");
        Guid targetId = targetRecord.GetProperty("id").GetGuid();
        Guid elevatedRoleId = targetRecord.GetProperty("roles").EnumerateArray().Single().GetProperty("id").GetGuid();
        JsonElement limited = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Limited manager", description = "Delegated management",
            permissions = LimitedOrganizationManagementPermissions
        });
        Guid limitedRoleId = limited.GetProperty("id").GetGuid();
        using HttpClient manager = await host.InviteAndActivateAsync(owner, "limited-manager@trykatch.test");
        Guid managerId = (await AccessHost.OrganizationMemberAsync(owner, "limited-manager@trykatch.test")).GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{managerId}", new { membershipId = managerId, roleIds = new[] { limitedRoleId }, isActive = true });

        using HttpResponseMessage denied = await AccessHost.SendAsync(manager, HttpMethod.Put, $"/api/v1/members/{targetId}",
            new { membershipId = targetId, roleIds = new[] { limitedRoleId }, isActive = true });

        await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        (await owner.GetFromJsonAsync<JsonElement>($"/api/v1/members/{targetId}")).GetProperty("roles").EnumerateArray()
            .Single().GetProperty("id").GetGuid().ShouldBe(elevatedRoleId);
        // This replaces the old unit test that trusted a fake context and permission
        // authorizer, exercising the delegation boundary against persisted authority.
        using HttpResponseMessage deniedRoleGrant = await AccessHost.SendAsync(manager, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Escalated role", description = "Cannot grant authority not held", permissions = ElevatedOrganizationPermissions
        });
        await AccessHost.AssertProblemAsync(deniedRoleGrant, HttpStatusCode.Forbidden, "forbidden");
    }

    [TestMethod]
    public async Task ProtectedRoleDefinitionsRequireProtectedManagementAuthority()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement managerRole = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Delegated manager", description = "All ordinary management", permissions = PlatformPermissions.All
        });
        using HttpClient platformManager = await host.GrantAndSignInAsync(administrator, "manager@trykatch.test", managerRole.GetProperty("key").GetString()!);
        foreach (HttpMethod method in new[] { HttpMethod.Put, HttpMethod.Delete })
        {
            using HttpResponseMessage denied = await AccessHost.SendAsync(platformManager, method, $"/api/v1/platform-users/roles/{PlatformRoles.Administrator}", new
            {
                name = "Administrator", description = "Immutable", permissions = PlatformPermissions.All
            });
            await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "grant_boundary");
        }
        using HttpResponseMessage immutableAdministrator = await AccessHost.SendAsync(administrator, HttpMethod.Delete, $"/api/v1/platform-users/roles/{PlatformRoles.Administrator}", new { });
        await AccessHost.AssertProblemAsync(immutableAdministrator, HttpStatusCode.Conflict, "system_role");

        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        using HttpClient organizationManager = await host.DelegateOrganizationManagementAsync(owner);
        JsonElement ownerRole = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
            .Single(role => role.GetProperty("name").GetString() == "Owner");
        Guid ownerRoleId = ownerRole.GetProperty("id").GetGuid();
        foreach (string operation in new[] { "update", "archive", "restore", "delete" })
        {
            using HttpResponseMessage denied = await AccessHost.SendAsync(organizationManager,
                operation == "update" ? HttpMethod.Put : operation == "delete" ? HttpMethod.Delete : HttpMethod.Post,
                $"/api/v1/roles/{ownerRoleId}" + (operation is "archive" or "restore" ? $"/{operation}" : ""),
                new { id = ownerRoleId, name = "Owner", description = "Immutable", permissions = ownerRole.GetProperty("permissions"), reason = "Requested lifecycle cleanup" });
            await AccessHost.AssertProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        }
        using HttpResponseMessage immutableOwner = await AccessHost.SendAsync(owner, HttpMethod.Post, $"/api/v1/roles/{ownerRoleId}/archive", new { });
        await AccessHost.AssertProblemAsync(immutableOwner, HttpStatusCode.Conflict, "conflict");
    }

    [TestMethod]
    public async Task RolePermissionChangeRollsBackWhenAnyMemberCredentialInvalidationFails()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement originalRole = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Pending member role", description = "Original authority", permissions = new[] { PlatformPermissions.DashboardRead }
        });
        string roleKey = originalRole.GetProperty("key").GetString()!;
        List<JsonElement> grants = [];
        foreach (string email in new[] { "role-member-first@trykatch.test", "role-member-second@trykatch.test" })
            grants.Add(await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
            {
                email, displayName = "Pending member", roleKey
            }));
        await host.FailMemberStampRotationAfterAsync(1);

        using HttpResponseMessage rejected = await AccessHost.SendAsync(administrator, HttpMethod.Put, $"/api/v1/platform-users/roles/{roleKey}", new
        {
            name = "Elevated member role", description = "Must not commit", permissions = PlatformPermissions.All
        });

        await AccessHost.AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "identity_validation");
        JsonElement unchanged = (await administrator.GetFromJsonAsync<JsonElement>("/api/v1/platform-users/roles")).EnumerateArray()
            .Single(role => role.GetProperty("key").GetString() == roleKey);
        unchanged.GetProperty("name").GetString().ShouldBe("Pending member role");
        unchanged.GetProperty("description").GetString().ShouldBe("Original authority");
        unchanged.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString())
            .ShouldBe([PlatformPermissions.DashboardRead]);

        // Both original credentials remain valid for the unchanged, lower authority:
        // the first successful stamp rotation must roll back with the second failure.
        await host.RemoveStampRotationFailureAsync();
        foreach (JsonElement grant in grants)
        {
            using HttpClient activated = await host.ActivatePlatformGrantAsync(grant);
            (await activated.GetAsync("/api/v1/platform-users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }
    }

    [TestMethod]
    [DataRow("grant")]
    [DataRow("role")]
    [DataRow("suspend")]
    [DataRow("reactivate")]
    [DataRow("revoke")]
    public async Task PlatformAccessChangeRollsBackWhenCredentialInvalidationFails(string operation)
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        JsonElement role = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users/roles", new
        {
            name = "Pending member role", description = "Original authority", permissions = new[] { PlatformPermissions.DashboardRead }
        });
        string roleKey = role.GetProperty("key").GetString()!;
        const string email = "role-member-first@trykatch.test";
        JsonElement grant = await AccessHost.SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
        {
            email, displayName = "Pending member", roleKey
        });
        Guid userId = grant.GetProperty("user").GetProperty("id").GetGuid();
        if (operation == "grant")
            await AccessHost.SuccessAsync(administrator, HttpMethod.Delete, $"/api/v1/platform-users/{userId}", new { });
        if (operation == "reactivate")
            await AccessHost.SuccessAsync(administrator, HttpMethod.Post, $"/api/v1/platform-users/{userId}/suspend", new { });
        await host.FailMemberStampRotationAfterAsync(0);
        string path = operation == "grant" ? "/api/v1/platform-users" : $"/api/v1/platform-users/{userId}" + (operation == "revoke" ? "" : $"/{operation}");

        using HttpResponseMessage rejected = await AccessHost.SendAsync(administrator,
            operation == "role" ? HttpMethod.Put : operation == "revoke" ? HttpMethod.Delete : HttpMethod.Post,
            path, new { email, displayName = "Pending member", roleKey = PlatformRoles.Auditor });

        JsonElement problem = await AccessHost.AssertProblemAsync(rejected, HttpStatusCode.BadRequest, "identity_validation");
        problem.TryGetProperty("activationToken", out _).ShouldBeFalse();
        using HttpResponseMessage lookup = await administrator.GetAsync($"/api/v1/platform-users/{userId}");
        if (operation == "grant") lookup.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        else
        {
            lookup.StatusCode.ShouldBe(HttpStatusCode.OK);
            JsonElement unchanged = await lookup.Content.ReadFromJsonAsync<JsonElement>();
            unchanged.GetProperty("roleKey").GetString().ShouldBe(roleKey);
            unchanged.GetProperty("isActive").GetBoolean().ShouldBe(operation != "reactivate");
            unchanged.GetProperty("isPendingActivation").GetBoolean().ShouldBeTrue();
        }
    }

    private sealed class AccessHost(PostgreSqlContainer? postgres, WebApplicationFactory<Program> factory, string ownerConnection, string? maintenanceConnection, string? databaseName) : IAsyncDisposable
    {
        public const string AdministratorEmail = "authority-admin@trykatch.test";
        private const string Password = "Local-only!Authority-Password-42";
        public IServiceProvider Services => factory.Services;

        public static async Task<AccessHost> StartAsync()
        {
            string? configuredPostgres = Environment.GetEnvironmentVariable("TRYKATCH_TEST_POSTGRES");
            PostgreSqlContainer? postgres = string.IsNullOrWhiteSpace(configuredPostgres)
                ? new PostgreSqlBuilder("postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f").Build()
                : null;
            if (postgres is not null) await postgres.StartAsync();
            string ownerConnection = postgres?.GetConnectionString() ?? configuredPostgres!;
            string? databaseName = null;
            if (postgres is null)
            {
                databaseName = $"trykatch_authority_{Guid.NewGuid():N}";
                await using NpgsqlConnection maintenance = new(configuredPostgres);
                await maintenance.OpenAsync();
                await using NpgsqlCommand create = new($"CREATE DATABASE \"{databaseName}\"", maintenance);
                await create.ExecuteNonQueryAsync();
                ownerConnection = new NpgsqlConnectionStringBuilder(configuredPostgres) { Database = databaseName }.ConnectionString;
            }
            try
            {
                return new(postgres, await CreateFactoryAsync(ownerConnection), ownerConnection, configuredPostgres, databaseName);
            }
            catch
            {
                if (postgres is not null) await postgres.DisposeAsync();
                else await DropDatabaseAsync(configuredPostgres!, databaseName!);
                throw;
            }
        }

        private static async Task<WebApplicationFactory<Program>> CreateFactoryAsync(string ownerConnection)
        {
            var connections = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(ownerConnection);
            await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ownerConnection).Options);
            await identity.Database.MigrateAsync();
            await using PlatformDbContext platform = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ownerConnection).Options);
            await platform.Database.MigrateAsync();
            ProjectsModule projects = new();
            DocumentsModule documents = new();
            ModuleCatalog catalog = new([projects, documents]);
            await using ApplicationDbContext application = new(
                new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(ownerConnection).Options,
                [new ProjectsModelContributor(), new DocumentsModelContributor()], moduleCatalog: catalog);
            await application.Database.MigrateAsync();
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            foreach (ModuleMigration migration in documents.Migrations)
            {
                await using NpgsqlCommand command = new(migration.Sql, connection);
                await command.ExecuteNonQueryAsync();
            }
            await InstalledSchemaCatalog.SynchronizeAsync(ownerConnection, catalog.Descriptors.SelectMany(module =>
                module.DataResources.Select(resource => new InstalledDataResource(module.Id, resource))));
            await PostgresRuntimeRoleFixture.GrantApplicationPrivilegesAsync(ownerConnection);

            Dictionary<string, string?> settings = new()
            {
                ["ConnectionStrings:trykatchdb"] = connections.Organization,
                ["ConnectionStrings:trykatch-organization"] = connections.Organization,
                ["ConnectionStrings:trykatch-platform"] = connections.Platform,
                ["ConnectionStrings:trykatch-identity"] = connections.Identity,
                ["ConnectionStrings:trykatch-outbox"] = connections.Outbox,
                ["Bootstrap:PlatformAdminEmail"] = AdministratorEmail,
                ["Bootstrap:PlatformAdminPassword"] = Password,
                ["DevelopmentDemo:Enabled"] = "false"
            };
            WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(webHost =>
            {
                webHost.UseEnvironment("Development");
                foreach ((string key, string? value) in settings) webHost.UseSetting(key, value);
                webHost.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            });
            return factory;
        }

        public HttpClient CreateClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true
        });

        public async Task<ManagementBarrier> BlockManagementAsync(Guid? organizationId = null)
        {
            NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
            await using NpgsqlCommand command = new(organizationId is null
                ? "SELECT pg_advisory_xact_lock(734001)"
                : "SELECT pg_advisory_xact_lock(hashtextextended(@organization, 734002))", connection, transaction);
            if (organizationId is Guid id) command.Parameters.AddWithValue("organization", id.ToString());
            await command.ExecuteNonQueryAsync();
            return new(connection, transaction);
        }

        public async Task<HttpClient> SignInAsync(string email)
        {
            HttpClient client = CreateClient();
            await SuccessAsync(client, HttpMethod.Post, "/api/v1/auth/login", new { email, password = Password, rememberMe = false });
            return client;
        }

        public async Task<HttpClient> GrantAndSignInAsync(HttpClient administrator, string email, string roleKey)
        {
            JsonElement granted = await SuccessAsync(administrator, HttpMethod.Post, "/api/v1/platform-users", new
            {
                email, displayName = email, roleKey
            });
            return await ActivatePlatformGrantAsync(granted);
        }

        public async Task<HttpClient> ActivatePlatformGrantAsync(JsonElement granted)
        {
            using HttpClient anonymous = CreateClient();
            await SuccessAsync(anonymous, HttpMethod.Post, "/api/v1/access-activation", new
            {
                userId = granted.GetProperty("user").GetProperty("id").GetGuid(),
                token = granted.GetProperty("activationToken").GetString(), password = Password
            });
            return await SignInAsync(granted.GetProperty("user").GetProperty("email").GetString()!);
        }

        public async Task FailMemberStampRotationAfterAsync(int successfulRotations)
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new($$"""
                CREATE FUNCTION identity.test_fail_stamp_rotation() RETURNS trigger
                LANGUAGE plpgsql AS $failure$
                DECLARE rotations integer;
                BEGIN
                    IF NEW."Email" IN ('role-member-first@trykatch.test', 'role-member-second@trykatch.test')
                       AND OLD."SecurityStamp" IS DISTINCT FROM NEW."SecurityStamp" THEN
                        rotations := COALESCE(NULLIF(current_setting('test.stamp_rotations', true), ''), '0')::integer;
                        PERFORM set_config('test.stamp_rotations', (rotations + 1)::text, true);
                        IF rotations >= {{successfulRotations}} THEN
                            -- Zero updated rows makes the real EF Identity store return
                            -- IdentityResult.ConcurrencyFailure instead of throwing SQL errors.
                            RETURN NULL;
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $failure$;
                CREATE TRIGGER test_fail_stamp_rotation
                    BEFORE UPDATE ON identity."AspNetUsers"
                    FOR EACH ROW EXECUTE FUNCTION identity.test_fail_stamp_rotation();
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task RemoveStampRotationFailureAsync()
        {
            await using NpgsqlConnection connection = new(ownerConnection);
            await connection.OpenAsync();
            await using NpgsqlCommand command = new("""
                DROP TRIGGER test_fail_stamp_rotation ON identity."AspNetUsers";
                DROP FUNCTION identity.test_fail_stamp_rotation();
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<HttpClient> CreateOrganizationOwnerAsync(HttpClient administrator)
        {
            JsonElement created = await SuccessAsync(administrator, HttpMethod.Post, "/api/v1/tenants", new
            {
                name = "Authority workspace", slug = "authority-workspace", administratorEmail = "owner@trykatch.test"
            });
            return await ActivateOrganizationInvitationAsync(created.GetProperty("invitationToken").GetString()!);
        }

        public async Task<HttpClient> InviteAndActivateAsync(HttpClient inviter, string email)
        {
            JsonElement created = await SuccessAsync(inviter, HttpMethod.Post, "/api/v1/invitations", new { email, expiresInDays = 7 });
            string token = new Uri(created.GetProperty("invitationUrl").GetString()!).Segments.Last();
            return await ActivateOrganizationInvitationAsync(token);
        }

        public async Task<HttpClient> DelegateOrganizationManagementAsync(HttpClient owner)
        {
            JsonElement ownerRole = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/roles")).EnumerateArray()
                .Single(role => role.GetProperty("name").GetString() == "Owner");
            JsonElement role = await SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
            {
                id = (Guid?)null, name = "Delegated manager", description = "All ordinary management", permissions = ownerRole.GetProperty("permissions")
            });
            HttpClient manager = await InviteAndActivateAsync(owner, "organization-manager@trykatch.test");
            Guid managerId = (await OrganizationMemberAsync(owner, "organization-manager@trykatch.test")).GetProperty("id").GetGuid();
            await SuccessAsync(owner, HttpMethod.Put, $"/api/v1/members/{managerId}", new { membershipId = managerId, roleIds = new[] { role.GetProperty("id").GetGuid() }, isActive = true });
            return manager;
        }

        public async Task<Guid> SeedOwnerInvitationAsync(Guid organizationId, Guid roleId)
        {
            // Generic invitations intentionally grant Member. Seed the protected invitation
            // state using the same aggregate as organization provisioning, without a new API.
            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Invitation invitation = Invitation.Create(organizationId, roleId, "pending-owner@trykatch.test",
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))), DateTimeOffset.UtcNow.AddDays(7));
            await using PlatformDbContext database = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(ownerConnection).Options);
            database.Invitations.Add(invitation);
            await database.SaveChangesAsync();
            return invitation.Id;
        }

        private async Task<HttpClient> ActivateOrganizationInvitationAsync(string token)
        {
            HttpClient client = CreateClient();
            await SuccessAsync(client, HttpMethod.Post, "/api/v1/invitations/activate", new
            {
                token, firstName = "Authority", lastName = "Member", password = Password
            });
            return client;
        }

        public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object body)
        {
            JsonElement csrf = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/antiforgery");
            using HttpRequestMessage request = new(method, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("token").GetString());
            return await client.SendAsync(request);
        }

        public static async Task<JsonElement> SuccessAsync(HttpClient client, HttpMethod method, string url, object body)
        {
            using HttpResponseMessage response = await SendAsync(client, method, url, body);
            response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
            return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        public static async Task<JsonElement> PlatformUserAsync(HttpClient client, string email)
        {
            JsonElement page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/platform-users?search={Uri.EscapeDataString(email)}");
            return page.GetProperty("items").EnumerateArray().Single(user => user.GetProperty("email").GetString() == email);
        }

        public static async Task<JsonElement> OrganizationMemberAsync(HttpClient client, string email) =>
            (await client.GetFromJsonAsync<JsonElement>("/api/v1/members")).EnumerateArray()
                .Single(member => member.GetProperty("email").GetString() == email);

        public static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
        {
            response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync());
            JsonElement problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            problem.GetProperty("title").GetString().ShouldBe(code);
            return problem;
        }

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            if (postgres is not null) await postgres.DisposeAsync();
            else await DropDatabaseAsync(maintenanceConnection!, databaseName!);
        }

        private static async Task DropDatabaseAsync(string maintenanceConnection, string databaseName)
        {
            await using NpgsqlConnection maintenance = new(maintenanceConnection);
            await maintenance.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", maintenance);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ManagementBarrier(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async Task WaitForBlockedRequestsAsync(int expected)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(10))
            {
                await using NpgsqlCommand command = new("""
                    SELECT count(*) FROM pg_locks
                    WHERE locktype = 'advisory' AND NOT granted
                      AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                    """, connection, transaction);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) == expected) return;
                await Task.Delay(20);
            }
            Assert.Fail("Concurrent management requests did not both reach the transaction-scoped authority lock.");
        }

        public Task ReleaseAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
