using System.Net;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using Shouldly;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private static readonly string[] RequestLimitReaderPermissions = ["projects.read"];
    [TestMethod]
    public async Task OrganizationApiAllowanceIsSharedAcrossProvidersAndIgnoresUntrustedOrganizationHeader()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();

        IReadOnlyDictionary<string, string?> settings = RequestLimitSettings(permitLimit: 1, windowSeconds: 60);
        await using AccessHost firstReplica = await setup.CreateReplicaAsync(settings);
        await using AccessHost secondReplica = await setup.CreateReplicaAsync(settings);
        using HttpClient firstOwner = await firstReplica.SignInAsync("owner@trykatch.test");
        using HttpClient secondOwner = await secondReplica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(firstOwner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });
        await AccessHost.SuccessAsync(secondOwner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        using HttpResponseMessage admitted = await firstOwner.GetAsync("/api/v1/access");
        using HttpRequestMessage forgedOrganizationRequest = new(HttpMethod.Get, "/api/v1/access");
        forgedOrganizationRequest.Headers.Add("X-Organization-Id", Guid.NewGuid().ToString());
        using HttpResponseMessage exhausted = await secondOwner.SendAsync(forgedOrganizationRequest);

        admitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        exhausted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        exhausted.Headers.RetryAfter.ShouldNotBeNull();
        exhausted.Headers.RetryAfter!.Delta.ShouldNotBeNull();
        exhausted.Headers.RetryAfter.Delta!.Value.TotalSeconds.ShouldBeInRange(1, 60);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task PermissionAndAntiforgeryRejectionsDoNotConsumeOrganizationAllowance()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        using HttpClient setupMember = await setup.InviteAndActivateAsync(setupOwner, "rate-limit-member@trykatch.test");
        JsonElement readOnlyRole = await AccessHost.SuccessAsync(setupOwner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Request limit reader", description = "Read without project management",
            permissions = RequestLimitReaderPermissions
        });
        Guid membershipId = (await AccessHost.OrganizationMemberAsync(setupOwner, "rate-limit-member@trykatch.test"))
            .GetProperty("id").GetGuid();
        await AccessHost.SuccessAsync(setupOwner, HttpMethod.Put, $"/api/v1/members/{membershipId}", new
        {
            membershipId, roleIds = new[] { readOnlyRole.GetProperty("id").GetGuid() }, isActive = true
        });
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();

        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(permitLimit: 1, windowSeconds: 60));
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        using HttpClient member = await replica.SignInAsync("rate-limit-member@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });
        await AccessHost.SuccessAsync(member, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        using HttpResponseMessage denied = await AccessHost.SendAsync(member, HttpMethod.Post, "/api/v1/projects",
            new { name = "Unauthorized project", description = "Must not be admitted." });
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpRequestMessage missingAntiforgery = new(HttpMethod.Post, "/api/v1/projects")
        {
            Content = JsonContent.Create(new { name = "Unverified project", description = "Must not be admitted." })
        };
        using HttpResponseMessage csrfRejected = await owner.SendAsync(missingAntiforgery);
        csrfRejected.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        using HttpResponseMessage admitted = await owner.GetAsync("/api/v1/access");
        admitted.StatusCode.ShouldBe(HttpStatusCode.OK, await admitted.Content.ReadAsStringAsync());
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
    }

    [TestMethod]
    public async Task FeatureRejectionStillConsumesAnAuthorizedOrganizationAttempt()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();
        await SaveProjectsEnvironmentAsync(administrator, "projects.writes", enabled: false);
        await using AccessHost replica = await setup.CreateReplicaAsync(RequestLimitSettings(1, 60));
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        using HttpResponseMessage disabled = await SendProjectsFeatureMutationAsync(owner, "feature-disabled-limit", "Unavailable project", "No business effect");
        await AccessHost.AssertProblemAsync(disabled, HttpStatusCode.Conflict, "feature_unavailable");
        using HttpResponseMessage exhausted = await owner.GetAsync("/api/v1/access");
        exhausted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
        (await ReadProjectEffectsAsync(setup.OwnerConnection, organizationId)).ShouldBe((0L, 0L, 0L));
    }

    [TestMethod]
    public async Task RedisOutageCannotResetOrReplaceThePostgresOrganizationAllowance()
    {
        await using AccessHost setup = await AccessHost.StartAsync();
        using HttpClient administrator = await setup.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient setupOwner = await setup.CreateOrganizationOwnerAsync(administrator);
        Guid organizationId = (await setupOwner.GetFromJsonAsync<JsonElement>("/api/v1/access"))
            .GetProperty("organizationId").GetGuid();
        Dictionary<string, string?> settings = RequestLimitSettings(1, 60);
        settings["Cache:Mode"] = "Distributed";
        settings["ConnectionStrings:cache"] = "127.0.0.1:1,password=request-limit-test,abortConnect=false";
        await using AccessHost replica = await setup.CreateReplicaAsync(settings);
        HealthReport cache = await replica.Services.GetRequiredService<HealthCheckService>()
            .CheckHealthAsync(registration => registration.Name == "organization-cache");
        cache.Status.ShouldBe(HealthStatus.Degraded);
        cache.Entries.Count.ShouldBe(1);
        using HttpClient owner = await replica.SignInAsync("owner@trykatch.test");
        await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/workspace/select", new { organizationId, remember = false });

        using HttpResponseMessage admitted = await owner.GetAsync("/api/v1/access");
        admitted.StatusCode.ShouldBe(HttpStatusCode.OK);
        using HttpResponseMessage exhausted = await owner.GetAsync("/api/v1/access");
        exhausted.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadAttemptsAsync(setup.OwnerConnection, organizationId)).ShouldBe(1);
    }

    private static Dictionary<string, string?> RequestLimitSettings(int permitLimit, int windowSeconds) =>
        new Dictionary<string, string?>
        {
            ["OrganizationRequestLimits:Enabled"] = "true",
            ["OrganizationRequestLimits:PermitLimit"] = permitLimit.ToString(CultureInfo.InvariantCulture),
            ["OrganizationRequestLimits:WindowSeconds"] = windowSeconds.ToString(CultureInfo.InvariantCulture)
        };

    private static async Task<int> ReadAttemptsAsync(string connectionString, Guid organizationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT "Attempts" FROM app.organization_request_limits
            WHERE "OrganizationId" = @organization AND "Policy" = 'organization-api'
            """, connection);
        command.Parameters.AddWithValue("organization", organizationId);
        object? value = await command.ExecuteScalarAsync();
        return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}
