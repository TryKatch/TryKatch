using System.Collections.Frozen;
using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Trykatch.Application.Features;
using Trykatch.Application.Organizations;
using Trykatch.Infrastructure.Features;
using Trykatch.Infrastructure.Organizations;
using Trykatch.Infrastructure.Persistence;
using Trykatch.Modules;

namespace Trykatch.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class OrganizationFeatureIntegrationTests
{
    private const string Image = "postgres:18.6-alpine3.23@sha256:697c180dbf244d3ce4a8f4cbc0156cde840af055c1bf8b76aebe422a4822086f";
    private const string Reads = "projects.reads";
    private const string Writes = "projects.writes";
    private static readonly FeaturePolicySettings Settings = new("production", FrozenSet<string>.Empty,
        new Dictionary<string, bool> { [Writes] = false }.ToFrozenDictionary(StringComparer.Ordinal));

    [TestMethod]
    public async Task SnapshotUsesOneStatementAndOnlyCurrentOrganizationWithFreshPrecedence()
    {
        await using PostgreSqlContainer postgres = await DatabaseAsync();
        string owner = postgres.GetConnectionString();
        var runtime = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
        Guid first = await OrganizationAsync(owner), second = await OrganizationAsync(owner), actor = Guid.NewGuid();
        await EnvironmentAsync(owner, Reads, false);
        await OverrideAsync(owner, second, Reads, true);
        await OverrideAsync(owner, first, Writes, true);
        PolicyProbe probe = new();
        await using OrganizationControlPlaneDbContext db = Context(runtime.Organization, probe);
        await using var tx = await BeginAsync(db, actor, first);
        OrganizationFeatureEvaluator evaluator = Evaluator(db, actor, first);
        probe.Reads = 0;
        OrganizationFeatureSnapshot snapshot = await evaluator.ReadAsync(CancellationToken.None);
        snapshot.OrganizationId.ShouldBe(first);
        snapshot.EvaluatedAtUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        snapshot.Values.Count.ShouldBe(2);
        snapshot.Values[Reads].ShouldBeFalse();
        snapshot.Values[Writes].ShouldBeTrue();
        probe.Reads.ShouldBe(1);

        // Another committed policy change must affect the next decision, within this same ReadCommitted scope.
        await OverrideAsync(owner, first, Reads, true);
        (await evaluator.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeTrue();
        await EnvironmentAsync(owner, Reads, true, forceDisabled: true);
        (await evaluator.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeFalse();
        await EnvironmentAsync(owner, Reads, null);
        await OverrideAsync(owner, first, Reads, null);
        (await evaluator.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeTrue();
        await EnvironmentAsync(owner, Writes, null);
        await OverrideAsync(owner, first, Writes, null);
        (await evaluator.IsEnabledAsync(Writes, CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task DeploymentVetoAndAbsentDeclarationsCannotBeEnabledByRetainedPolicies()
    {
        await using PostgreSqlContainer postgres = await DatabaseAsync();
        string owner = postgres.GetConnectionString();
        var runtime = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
        Guid org = await OrganizationAsync(owner), actor = Guid.NewGuid();
        await EnvironmentAsync(owner, Reads, true);
        await OverrideAsync(owner, org, Reads, true);
        await using OrganizationControlPlaneDbContext db = Context(runtime.Organization);
        await using var tx = await BeginAsync(db, actor, org);
        FeaturePolicySettings disabled = Settings with { Disabled = new[] { Reads }.ToFrozenSet(StringComparer.Ordinal) };
        (await Evaluator(db, actor, org, settings: disabled).IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeFalse();
        OrganizationFeatureEvaluator absent = Evaluator(db, actor, org, modules: new ModuleCatalog([]));
        (await absent.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeFalse();
        (await absent.ReadAsync(CancellationToken.None)).Values.ShouldBeEmpty();
        (await Evaluator(db, actor, org).IsEnabledAsync("unknown.feature", CancellationToken.None)).ShouldBeFalse();
    }

    [TestMethod]
    public async Task EvaluationRequiresMatchingOrganizationActorProfileAndReadCommittedTransaction()
    {
        await using PostgreSqlContainer postgres = await DatabaseAsync();
        var runtime = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(postgres.GetConnectionString());
        Guid org = await OrganizationAsync(postgres.GetConnectionString()), actor = Guid.NewGuid();
        await using OrganizationControlPlaneDbContext db = Context(runtime.Organization);
        await Should.ThrowAsync<InvalidOperationException>(() => Evaluator(db, actor, org).ReadAsync(CancellationToken.None));
        await using (var tx = await BeginAsync(db, actor, org))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => Evaluator(db, Guid.NewGuid(), org).ReadAsync(CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => Evaluator(db, actor, Guid.NewGuid()).ReadAsync(CancellationToken.None));
            await Should.ThrowAsync<InvalidOperationException>(() => new OrganizationFeatureEvaluator(db,
                new OrganizationContext(), Catalog(), Settings, TimeProvider.System).ReadAsync(CancellationToken.None));
        }
        await using (var tx = await BeginAsync(db, actor, org, IsolationLevel.RepeatableRead))
            await Should.ThrowAsync<InvalidOperationException>(() => Evaluator(db, actor, org).ReadAsync(CancellationToken.None));
        await using OrganizationControlPlaneDbContext platform = Context(runtime.Platform);
        await using var wrongProfile = await BeginAsync(platform, actor, org);
        await Should.ThrowAsync<InvalidOperationException>(() => Evaluator(platform, actor, org).ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task StoreOutageFailsClosedAfterAnEnabledResultAndCancellationIsPreserved()
    {
        await using PostgreSqlContainer postgres = await DatabaseAsync();
        string owner = postgres.GetConnectionString();
        var runtime = await PostgresRuntimeRoleFixture.CreateConnectionStringsAsync(owner);
        Guid org = await OrganizationAsync(owner), actor = Guid.NewGuid();
        PolicyProbe probe = new();
        await using OrganizationControlPlaneDbContext db = Context(runtime.Organization, probe);
        await using var tx = await BeginAsync(db, actor, org);
        OrganizationFeatureEvaluator evaluator = Evaluator(db, actor, org);
        (await evaluator.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeTrue();
        probe.Fail = true;
        OrganizationFeaturePolicyUnavailableException failure = await Should.ThrowAsync<OrganizationFeaturePolicyUnavailableException>(
            () => evaluator.IsEnabledAsync(Reads, CancellationToken.None));
        failure.Message.ShouldNotContain("sensitive fixture failure");
        await Should.ThrowAsync<OrganizationFeaturePolicyUnavailableException>(() => evaluator.ReadAsync(CancellationToken.None));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => evaluator.IsEnabledAsync(Reads, cancellation.Token));
        await Should.ThrowAsync<OperationCanceledException>(() => evaluator.IsEnabledAsync("unknown.feature", cancellation.Token));
        probe.Fail = false;
        (await evaluator.IsEnabledAsync(Reads, CancellationToken.None)).ShouldBeTrue();
    }

    private static OrganizationFeatureEvaluator Evaluator(OrganizationControlPlaneDbContext db, Guid actor, Guid org,
        FeaturePolicySettings? settings = null, ModuleCatalog? modules = null)
    {
        OrganizationContext context = new();
        context.Initialize(new(org, "features", actor, Guid.NewGuid(), FrozenSet<string>.Empty));
        return new(db, context, modules ?? Catalog(), settings ?? Settings, TimeProvider.System);
    }

    private static ModuleCatalog Catalog() => new([new FeatureModule()]);
    private sealed class FeatureModule : IModule
    {
        public ModuleDescriptor Descriptor { get; } = new("projects", "Projects", "1.4.0", "Projects.", [], [], ModuleCapabilities.None, [])
        {
            Features = [new(Reads, "Reads", "View projects.", true), new(Writes, "Writes", "Change projects.", true)]
        };
        public void Register(IServiceCollection services, IConfiguration configuration) { }
    }

    private static OrganizationControlPlaneDbContext Context(string connection, PolicyProbe? probe = null)
    {
        var builder = new DbContextOptionsBuilder<OrganizationControlPlaneDbContext>().UseNpgsql(connection);
        if (probe is not null) builder.AddInterceptors(probe);
        return new(builder.Options);
    }

    private static async Task<IDbContextTransaction> BeginAsync(OrganizationControlPlaneDbContext db, Guid actor, Guid org,
        IsolationLevel isolation = IsolationLevel.ReadCommitted)
    {
        var tx = await db.Database.BeginTransactionAsync(isolation);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('app.actor_id', {actor.ToString()}, true), set_config('app.organization_id', {org.ToString()}, true)");
        return tx;
    }

    private static async Task<PostgreSqlContainer> DatabaseAsync()
    {
        var postgres = new PostgreSqlBuilder(Image).Build();
        await postgres.StartAsync();
        await PostgresRuntimeRoleFixture.EnsureRuntimeRolesAsync(postgres.GetConnectionString());
        var migration = await ModuleJobMigrationIntegrationTests.InvokeMigratorAsync(postgres.GetConnectionString());
        migration.Exit.ShouldBe(0, migration.Output + migration.Error);
        return postgres;
    }

    private static async Task<Guid> OrganizationAsync(string owner)
    {
        Guid org = Guid.NewGuid();
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.organizations ("Id","Name","Slug","IsActive","CreatedAt")
            VALUES (@id, 'Feature organization', @slug, true, CURRENT_TIMESTAMP)
            """, connection);
        command.Parameters.AddWithValue("id", org);
        command.Parameters.AddWithValue("slug", "feature-" + org.ToString("N"));
        await command.ExecuteNonQueryAsync();
        return org;
    }

    private static async Task EnvironmentAsync(string owner, string key, bool? enabled, bool forceDisabled = false)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.feature_environment_controls ("Environment","FeatureKey","DefaultEnabled","ForceDisabled","Version")
            VALUES ('production',@key,@enabled,@disabled,@version)
            ON CONFLICT ("Environment","FeatureKey") DO UPDATE SET "DefaultEnabled"=EXCLUDED."DefaultEnabled",
            "ForceDisabled"=EXCLUDED."ForceDisabled", "Version"=EXCLUDED."Version"
            """, connection);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("enabled", NpgsqlTypes.NpgsqlDbType.Boolean, (object?)enabled ?? DBNull.Value);
        command.Parameters.AddWithValue("disabled", forceDisabled);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private static async Task OverrideAsync(string owner, Guid org, string key, bool? enabled)
    {
        await using var connection = new NpgsqlConnection(owner);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO platform.organization_feature_overrides ("OrganizationId","Environment","FeatureKey","Enabled","Version")
            VALUES (@org,'production',@key,@enabled,@version)
            ON CONFLICT ("OrganizationId","Environment","FeatureKey") DO UPDATE SET "Enabled"=EXCLUDED."Enabled", "Version"=EXCLUDED."Version"
            """, connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("enabled", NpgsqlTypes.NpgsqlDbType.Boolean, (object?)enabled ?? DBNull.Value);
        command.Parameters.AddWithValue("version", Guid.NewGuid());
        await command.ExecuteNonQueryAsync();
    }

    private sealed class PolicyProbe : DbCommandInterceptor
    {
        internal int Reads { get; set; }
        internal bool Fail { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("feature_environment_controls", StringComparison.Ordinal))
            {
                Reads++;
                if (Fail) throw new NpgsqlException("sensitive fixture failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
