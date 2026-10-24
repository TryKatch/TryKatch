using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Trykatch.Application.Features;
using Trykatch.Application.Identity;
using Trykatch.Identity;
using Trykatch.Infrastructure.Caching;
using Trykatch.Infrastructure.Features;
using Trykatch.Infrastructure.Persistence;

namespace Trykatch.IntegrationTests;

public sealed partial class AccessManagementBoundaryTests
{
    [TestMethod]
    public async Task FeatureAdministrationRejectsCurrentSuspendedUnconfirmedRevokedAndLegacyOnlyAuthority()
    {
        await using AccessHost host = await AccessHost.StartAsync();
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient target = await host.GrantAndSignInAsync(administrator, "feature-authority@trykatch.test", PlatformRoles.Administrator);
        await using IdentityDbContext owner = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options);
        Guid actor = await owner.Users.Where(user => user.Email == "feature-authority@trykatch.test").Select(user => user.Id).SingleAsync();
        foreach (string invalid in new[] { "suspended", "unconfirmed", "passwordless", "legacy-only" })
        {
            await owner.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update
                .SetProperty(user => user.IsPlatformAccessSuspended, false)
                .SetProperty(user => user.EmailConfirmed, true));
            switch (invalid)
            {
                case "suspended":
                    await owner.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.IsPlatformAccessSuspended, true));
                    break;
                case "unconfirmed":
                    await owner.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.EmailConfirmed, false));
                    break;
                case "passwordless":
                    await owner.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update.SetProperty(user => user.PasswordHash, (string?)null));
                    break;
                case "legacy-only":
                    await owner.UserRoles.Where(link => link.UserId == actor).ExecuteDeleteAsync();
                    await owner.Users.Where(user => user.Id == actor).ExecuteUpdateAsync(update => update
                        .SetProperty(user => user.IsPlatformAdministrator, true).SetProperty(user => user.PasswordHash, "fixture-present"));
                    break;
            }
            using IServiceScope scope = host.Services.CreateScope();
            FeatureAdministration service = scope.ServiceProvider.GetRequiredService<FeatureAdministration>();
            (await service.ListAsync(actor, null, CancellationToken.None)).ErrorCode.ShouldBe("forbidden", invalid);
            (await service.SaveEnvironmentAsync(actor, "projects.reads", new(true, false, Guid.Empty), CancellationToken.None))
                .ErrorCode.ShouldBe("forbidden", invalid);
        }
        (await owner.Users.Where(user => user.Id == actor).Select(user => user.IsPlatformAdministrator).SingleAsync()).ShouldBeTrue();
    }

    [TestMethod]
    public async Task FeatureBusyLockAndAuthorityRevocationAtAdmissionLeaveNoControlsOrHistory()
    {
        FeatureLockAdmissionProbe probe = new();
        await using AccessHost host = await AccessHost.StartAsync(configureServices: services =>
            services.AddDbContext<PlatformDbContext>((_, options) => options.AddInterceptors(probe)));
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        await using IdentityDbContext identity = new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(host.OwnerConnection).Options);
        Guid actor = await identity.Users.Where(user => user.Email == AccessHost.AdministratorEmail).Select(user => user.Id).SingleAsync();
        string runtime = host.Services.GetRequiredService<IConfiguration>().GetConnectionString("trykatch-platform")!;
        await using (PlatformDbContext holder = new(new DbContextOptionsBuilder<PlatformDbContext>().UseNpgsql(runtime).Options))
        await using (var transaction = await holder.Database.BeginTransactionAsync())
        {
            await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.actor_id', {actor.ToString()}, true)");
            var store = new FeatureControlStore(holder, TimeProvider.System);
            (await store.TryLockAsync(actor, "development", new("projects.reads"), CancellationToken.None)).ShouldBeTrue();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using HttpResponseMessage busy = await AccessHost.SendAsync(administrator, HttpMethod.Put,
                "/api/v1/platform/features/projects.reads/environment", new { defaultEnabled = false, forceDisabled = false, expectedVersion = Guid.Empty })
                .WaitAsync(timeout.Token);
            await AccessHost.AssertProblemAsync(busy, HttpStatusCode.Conflict, "feature_control_busy");
        }
        probe.OnLock = async () => await identity.Users.Where(user => user.Id == actor)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.IsPlatformAccessSuspended, true));
        using HttpResponseMessage revoked = await AccessHost.SendAsync(administrator, HttpMethod.Put,
            "/api/v1/platform/features/projects.reads/environment", new { defaultEnabled = false, forceDisabled = false, expectedVersion = Guid.Empty });
        await AccessHost.AssertProblemAsync(revoked, HttpStatusCode.Forbidden, "forbidden");
        probe.Invocations.ShouldBe(1);
        await using NpgsqlConnection connection = new(host.OwnerConnection);
        await connection.OpenAsync();
        await using NpgsqlCommand count = new("SELECT (SELECT count(*) FROM platform.feature_environment_controls) + (SELECT count(*) FROM platform.feature_control_history)", connection);
        ((long)(await count.ExecuteScalarAsync())!).ShouldBe(0);
    }

    [TestMethod]
    public async Task FeatureSnapshotSurvivesDistributedCacheFailureButReturns503ForAuthoritativeStoreFailure()
    {
        FeatureReadFailureProbe probe = new();
        await using AccessHost host = await AccessHost.StartAsync(configureServices: services =>
        {
            services.AddDbContext<OrganizationControlPlaneDbContext>((_, options) => options.AddInterceptors(probe));
            services.AddKeyedSingleton<IDistributedCache>(OrganizationCacheRegistration.DistributedServiceKey, new FailedFeatureCache());
        });
        using HttpClient administrator = await host.SignInAsync(AccessHost.AdministratorEmail);
        using HttpClient owner = await host.CreateOrganizationOwnerAsync(administrator);
        // Exercise the actual configured L2 boundary to prove that this fixture is unavailable.
        await Should.ThrowAsync<IOException>(() => host.Services.GetRequiredKeyedService<IDistributedCache>(
            OrganizationCacheRegistration.DistributedServiceKey).GetAsync("feature-test"));
        JsonElement enabled = await owner.GetFromJsonAsync<JsonElement>("/api/v1/features");
        enabled.GetProperty("values").GetProperty("projects.reads").GetBoolean().ShouldBeTrue();
        probe.Fail = true;
        using HttpResponseMessage failed = await owner.GetAsync("/api/v1/features");
        failed.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        string body = await failed.Content.ReadAsStringAsync();
        body.ShouldContain("feature_policy_unavailable");
        body.ShouldNotContain("private feature failure");
        probe.Fail = false;
        JsonElement recovered = await owner.GetFromJsonAsync<JsonElement>("/api/v1/features");
        recovered.GetProperty("values").GetProperty("projects.reads").GetBoolean().ShouldBeTrue();
    }

    private sealed class FeatureLockAdmissionProbe : DbCommandInterceptor
    {
        public Func<Task>? OnLock { get; set; }
        public int Invocations { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_try_advisory_xact_lock", StringComparison.Ordinal) && OnLock is { } action)
            {
                OnLock = null;
                Invocations++;
                await action();
            }
            return result;
        }
    }

    private sealed class FeatureReadFailureProbe : DbCommandInterceptor
    {
        public bool Fail { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.Contains("feature_environment_controls", StringComparison.Ordinal))
                throw new NpgsqlException("private feature failure");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailedFeatureCache : IDistributedCache
    {
        public byte[]? Get(string key) => throw new IOException("Feature cache fixture unavailable.");
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw new IOException("Feature cache fixture unavailable.");
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw new IOException("Feature cache fixture unavailable.");
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw new IOException("Feature cache fixture unavailable.");
        public void Refresh(string key) => throw new IOException("Feature cache fixture unavailable.");
        public Task RefreshAsync(string key, CancellationToken token = default) => throw new IOException("Feature cache fixture unavailable.");
        public void Remove(string key) => throw new IOException("Feature cache fixture unavailable.");
        public Task RemoveAsync(string key, CancellationToken token = default) => throw new IOException("Feature cache fixture unavailable.");
    }
}
