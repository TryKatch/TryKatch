using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Caching.Hybrid;
using Npgsql;
using Shouldly;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    private static readonly string[] CacheProjectReaderPermissions = ["projects.read"];
    private static readonly string[] CacheRevokedReaderPermissions = ["members.read"];

    [TestMethod]
    public async Task ProjectsDetailUsesWarmCacheOnlyAfterCurrentHttpPermissionAndTracksCommittedGenerations()
    {
        ProjectReadRecorder reads = new();
        await using AccessHost host = await StartCacheHostAsync(reads);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement access = await owner.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid organizationId = access.GetProperty("organizationId").GetGuid();
        Guid actorId = await ReadActorIdAsync(host.OwnerConnection, access.GetProperty("membershipId").GetGuid());

        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Cache lifecycle", description = "Committed project detail"
        });
        Guid projectId = created.GetProperty("id").GetGuid();
        string logicalHash = DetailHash(host, organizationId, actorId, projectId);
        Guid generation = await ReadGenerationAsync(host.OwnerConnection, organizationId, logicalHash);
        generation.ShouldNotBe(Guid.Empty);
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Cache lifecycle", "Active");

        Guid next = await MutateAndAssertGenerationAsync(host, owner, organizationId, logicalHash, generation,
            HttpMethod.Put, $"/api/v1/projects/{projectId}", new { id = projectId, name = "Updated lifecycle", description = "Updated" });
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Updated lifecycle", "Active");

        generation = next;
        next = await MutateAndAssertGenerationAsync(host, owner, organizationId, logicalHash, generation,
            HttpMethod.Post, $"/api/v1/projects/{projectId}/archive", new { });
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Updated lifecycle", "Archived");

        generation = next;
        next = await MutateAndAssertGenerationAsync(host, owner, organizationId, logicalHash, generation,
            HttpMethod.Post, $"/api/v1/projects/{projectId}/restore", new { });
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Updated lifecycle", "Active");

        generation = next;
        next = await MutateAndAssertGenerationAsync(host, owner, organizationId, logicalHash, generation,
            HttpMethod.Post, $"/api/v1/projects/{projectId}/archive", new { });
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Updated lifecycle", "Archived");

        generation = next;
        _ = await MutateAndAssertGenerationAsync(host, owner, organizationId, logicalHash, generation,
            HttpMethod.Delete, $"/api/v1/projects/{projectId}", new { reason = "Requested by project owner" });
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Updated lifecycle", "Deleted");
    }

    [TestMethod]
    public async Task RevokedMembershipCannotReadPreviouslyWarmProjectDetail()
    {
        ProjectReadRecorder reads = new();
        await using AccessHost host = await StartCacheHostAsync(reads);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement readerRole = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/roles", new
        {
            id = (Guid?)null, name = "Temporary project reader", description = "Only has project detail access",
            permissions = CacheProjectReaderPermissions
        });
        Guid readerRoleId = readerRole.GetProperty("id").GetGuid();
        using HttpClient member = await host.InviteAndActivateAsync(owner, "warm-cache-member@trykatch.test", readerRoleId);
        JsonElement access = await member.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid membershipId = access.GetProperty("membershipId").GetGuid();

        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Permission protected", description = "Must stay behind current access"
        });
        Guid projectId = created.GetProperty("id").GetGuid();
        await AssertDetailCacheHitAsync(member, reads, projectId, "Permission protected", "Active");

        await AccessHost.SuccessAsync(owner, HttpMethod.Put, $"/api/v1/roles/{readerRoleId}", new
        {
            id = readerRoleId, name = "Temporary project reader", description = "Project read permission revoked",
            permissions = CacheRevokedReaderPermissions
        });
        reads.Reset();
        using HttpResponseMessage denied = await member.GetAsync($"/api/v1/projects/{projectId}");

        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        reads.ProjectReadCount.ShouldBe(0);
    }

    [TestMethod]
    public async Task FailedGenerationPersistenceRollsBackProjectUpdateAndRetainsWarmValue()
    {
        ProjectReadRecorder reads = new();
        await using AccessHost host = await StartCacheHostAsync(reads);
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        JsonElement access = await owner.GetFromJsonAsync<JsonElement>("/api/v1/access");
        Guid organizationId = access.GetProperty("organizationId").GetGuid();
        Guid actorId = await ReadActorIdAsync(host.OwnerConnection, access.GetProperty("membershipId").GetGuid());
        JsonElement created = await AccessHost.SuccessAsync(owner, HttpMethod.Post, "/api/v1/projects", new
        {
            name = "Rollback value", description = "Before failed persistence"
        });
        Guid projectId = created.GetProperty("id").GetGuid();
        string logicalHash = DetailHash(host, organizationId, actorId, projectId);
        Guid generation = await ReadGenerationAsync(host.OwnerConnection, organizationId, logicalHash);
        generation.ShouldNotBe(Guid.Empty);
        await AssertDetailCacheHitAsync(owner, reads, projectId, "Rollback value", "Active");

        await InstallCacheGenerationUpdateFailureAsync(host.OwnerConnection);
        try
        {
            using HttpResponseMessage failed = await AccessHost.SendAsync(owner, HttpMethod.Put, $"/api/v1/projects/{projectId}", new
            {
                id = projectId, name = "Must roll back", description = "Generation write rejected"
            });
            failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await RemoveCacheGenerationUpdateFailureAsync(host.OwnerConnection);
        }

        (await ReadGenerationAsync(host.OwnerConnection, organizationId, logicalHash)).ShouldBe(generation);
        (await ReadProjectNameAsync(host.OwnerConnection, projectId)).ShouldBe("Rollback value");
        int projectReadsBeforeHit = reads.ProjectReadCount;
        JsonElement detail = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}");
        detail.GetProperty("name").GetString().ShouldBe("Rollback value");
        reads.ProjectReadCount.ShouldBe(projectReadsBeforeHit);
    }

    private static Task<AccessHost> StartCacheHostAsync(ProjectReadRecorder reads) => AccessHost.StartAsync(configureServices: services =>
    {
        services.AddDbContext<ApplicationDbContext>((_, options) => options.AddInterceptors(reads));
        services.Replace(ServiceDescriptor.Singleton<IOptions<OrganizationCacheOptions>>(
            Options.Create(new OrganizationCacheOptions { Mode = OrganizationCacheMode.LocalOnly })));
    });

    private static async Task<Guid> MutateAndAssertGenerationAsync(
        AccessHost host,
        HttpClient owner,
        Guid organizationId,
        string logicalHash,
        Guid previousGeneration,
        HttpMethod method,
        string path,
        object body)
    {
        using HttpResponseMessage response = await AccessHost.SendAsync(owner, method, path, body);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync());
        bool oldEntryMissed = false;
        _ = await host.Services.GetRequiredKeyedService<HybridCache>(OrganizationCacheRegistration.CacheServiceKey)
            .GetOrCreateAsync<byte[]>(CacheKeyPolicy.PhysicalKey(logicalHash, previousGeneration), _ =>
            {
                oldEntryMissed = true;
                return ValueTask.FromResult(Array.Empty<byte>());
            }, new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableDistributedCache });
        oldEntryMissed.ShouldBeTrue("The real HTTP commit interceptor must evict the warmed old local generation.");
        Guid current = await ReadGenerationAsync(host.OwnerConnection, organizationId, logicalHash);
        current.ShouldNotBe(Guid.Empty);
        current.ShouldNotBe(previousGeneration);
        return current;
    }

    private static async Task AssertDetailCacheHitAsync(
        HttpClient client,
        ProjectReadRecorder reads,
        Guid projectId,
        string expectedName,
        string expectedLifecycle)
    {
        reads.Reset();
        JsonElement first = await client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}");
        first.GetProperty("name").GetString().ShouldBe(expectedName);
        first.GetProperty("lifecycle").GetProperty("status").GetString().ShouldBe(expectedLifecycle);
        int sourceReads = reads.ProjectReadCount;
        sourceReads.ShouldBeGreaterThan(0, "The first request must load the detail from PostgreSQL before it can warm the cache.");

        JsonElement second = await client.GetFromJsonAsync<JsonElement>($"/api/v1/projects/{projectId}");
        second.GetProperty("name").GetString().ShouldBe(expectedName);
        second.GetProperty("lifecycle").GetProperty("status").GetString().ShouldBe(expectedLifecycle);
        reads.ProjectReadCount.ShouldBe(sourceReads, "The repeat request should use the warm LocalOnly HybridCache entry.");
    }

    private static string DetailHash(AccessHost host, Guid organizationId, Guid actorId, Guid projectId)
    {
        CacheHostIdentity identity = host.Services.GetRequiredService<CacheHostIdentity>();
        string parametersHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectId.ToString("N")))).ToLowerInvariant();
        ModuleCacheKey key = new("projects", "projects.detail", 1, 1, parametersHash, ModuleCacheAudience.Organization, "short");
        return CacheKeyPolicy.LogicalHash(key, identity.ApplicationNamespace, identity.Environment, organizationId, actorId);
    }

    private static async Task<Guid> ReadActorIdAsync(string ownerConnection, Guid membershipId)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"UserId\" FROM platform.memberships WHERE \"Id\" = @membership", connection);
        command.Parameters.AddWithValue("membership", membershipId);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Test membership was not found."));
    }

    private static async Task<Guid> ReadGenerationAsync(string ownerConnection, Guid organizationId, string logicalHash)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"Generation\" FROM app.cache_invalidations WHERE \"OrganizationId\" = @organization AND \"KeyHash\" = @hash", connection);
        command.Parameters.AddWithValue("organization", organizationId);
        command.Parameters.AddWithValue("hash", logicalHash);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<string> ReadProjectNameAsync(string ownerConnection, Guid projectId)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("SELECT \"Name\" FROM app.projects WHERE \"Id\" = @project", connection);
        command.Parameters.AddWithValue("project", projectId);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Test project was not found."));
    }

    private static async Task InstallCacheGenerationUpdateFailureAsync(string ownerConnection)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            CREATE FUNCTION app.reject_cache_generation_update() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                RAISE EXCEPTION 'Injected cache generation persistence failure';
            END $$;
            CREATE CONSTRAINT TRIGGER reject_cache_generation_update AFTER UPDATE ON app.cache_invalidations
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION app.reject_cache_generation_update();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task RemoveCacheGenerationUpdateFailureAsync(string ownerConnection)
    {
        await using NpgsqlConnection connection = new(ownerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            DROP TRIGGER reject_cache_generation_update ON app.cache_invalidations;
            DROP FUNCTION app.reject_cache_generation_update();
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ProjectReadRecorder : DbCommandInterceptor
    {
        private int projectReadCount;
        public int ProjectReadCount => Volatile.Read(ref projectReadCount);
        public void Reset() => Interlocked.Exchange(ref projectReadCount, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM app.projects", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref projectReadCount);
            return ValueTask.FromResult(result);
        }
    }
}
